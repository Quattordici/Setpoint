using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Api;
using Setpoint.Adapters.GitHub.Policy;
using Setpoint.Adapters.GitHub.Rulesets;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;

namespace Setpoint.Adapters.GitHub;

/// <summary>
/// The GitHub adapter. Setpoint owns one ruleset per repo, named <c>setpoint</c>, on the default branch; every other
/// ruleset and the repo's own merge settings are unmanaged, and are only read to warn when they are stricter.
/// </summary>
public sealed class GitHubAdapter : IProviderAdapter, IDisposable
{
    /// <summary>The ruleset's plan key when it has to be created.</summary>
    public const string CreateKey = "gh:new/ruleset/setpoint";

    /// <summary>What the setpoint ruleset is matched on: there is at most one per repo.</summary>
    public const string MatchKey = "gh:ruleset/setpoint";

    private const string RulesetTitle = "ruleset setpoint";

    private readonly GitHubAdapterOptions _options;
    private readonly SecretRedactor _redactor = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RepoRef, GitHubRepository> _listed = new();
    private readonly ConcurrentDictionary<RepoRef, List<GitHubRulesetSummary>> _rulesets = new();

    /// <summary>Creates the adapter.</summary>
    public GitHubAdapter(GitHubAdapterOptions? options = null) => _options = options ?? new GitHubAdapterOptions();

    /// <inheritdoc />
    public Platform Platform => Platform.GitHub;

    /// <inheritdoc />
    public IProviderBlockDefinition? ProviderBlock => GitHubProviderBlockDefinition.Instance;

    /// <inheritdoc />
    public async Task<TargetInfo> DescribeAsync(Target target, CancellationToken cancellationToken)
    {
        AuthMethod auth = target.Definition.Auth ?? AuthMethod.Pat;
        if (auth != AuthMethod.Pat)
        {
            throw new NotSupportedException($"auth: {auth.ToString().ToLowerInvariant()} isn't available for GitHub in this build of Setpoint; use a token");
        }

        string variable = TargetCredentials.TokenVariable(target);
        string token = _options.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"the environment variable {variable} holding its token is not set");
        _redactor.Add(token);

        Uri apiRoot = GitHubEndpoint.ApiRoot(target.Definition.Url);
        HttpClient http = TargetHttpClientFactory.Create(
            target.Name,
            apiRoot,
            _options.Http,
            _options.Logger,
            _redactor,
            [new BearerTokenHandler(token)],
            timeProvider: _options.Time);
        var api = new GitHubApiClient(http);
        try
        {
            (GitHubUser user, string? enterpriseVersion) = await api.GetUserAsync(cancellationToken).ConfigureAwait(false);
            if (_sessions.TryRemove(target.Name, out Session? previous))
            {
                previous.Api.Dispose();
            }

            _sessions[target.Name] = new Session(api, user.Login);
            return new TargetInfo(GitHubEndpoint.Describe(apiRoot, enterpriseVersion), GitHubApiClient.ApiVersion, user.Login);
        }
        catch
        {
            api.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ListedRepo> ListReposAsync(Target target, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Session session = SessionFor(target.Name);
        string[] owners = [.. (target.Definition.Scope ?? []).Select(g => g.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase)];
        bool anyOwner = owners.Any(o => o.Contains('*', StringComparison.Ordinal) || o.Contains('?', StringComparison.Ordinal));

        var seen = new HashSet<RepoRef>();
        IEnumerable<IAsyncEnumerable<GitHubRepository>> sources = anyOwner
            ? [session.Api.ListAccessibleReposAsync(cancellationToken)]
            : owners.Select(o => session.Api.ListOwnerReposAsync(o, o.Equals(session.Login, StringComparison.OrdinalIgnoreCase), cancellationToken));

        foreach (IAsyncEnumerable<GitHubRepository> source in sources)
        {
            await foreach (GitHubRepository repo in source.ConfigureAwait(false))
            {
                var reference = new RepoRef(target.Name, repo.Owner.Login, repo.Name);
                if (!target.InScope(reference) || !seen.Add(reference))
                {
                    continue;
                }

                _listed[reference] = repo;
                yield return new ListedRepo(reference, await SkipReasonAsync(session, target, repo, cancellationToken).ConfigureAwait(false));
            }
        }
    }

    /// <inheritdoc />
    public async Task<RepoCapabilities> GetCapabilitiesAsync(RepoRef repo, CancellationToken cancellationToken)
    {
        Session session = SessionFor(repo.Target);
        GitHubRepository listed = _listed.TryGetValue(repo, out GitHubRepository? known)
            ? known
            : await session.Api.GetRepoAsync(repo.Owner, repo.Name, cancellationToken).ConfigureAwait(false);
        if (!listed.Private)
        {
            return RepoCapabilities.Full;
        }

        // GitHub Free has no rulesets on private repos. Asking for them is the one reliable test, whoever owns the repo.
        List<GitHubRulesetSummary>? rulesets = await session.Api.ListRulesetsAsync(repo.Owner, repo.Name, cancellationToken).ConfigureAwait(false);
        if (rulesets is null)
        {
            return new RepoCapabilities(Diagnostic.Warning(
                DiagnosticCodes.PrivateRepoOnGitHubFree,
                "Rulesets don't apply to private repos on GitHub Free, so the repo is skipped. Make it public or move its owner to a paid plan to manage it."));
        }

        _rulesets[repo] = rulesets;
        return RepoCapabilities.Full;
    }

    /// <inheritdoc />
    public async Task<LiveSnapshot> ReadAsync(RepoRef repo, CancellationToken cancellationToken)
    {
        Session session = SessionFor(repo.Target);
        GitHubRepository details = await session.Api.GetRepoAsync(repo.Owner, repo.Name, cancellationToken).ConfigureAwait(false);
        List<GitHubRulesetSummary> rulesets = _rulesets.TryRemove(repo, out List<GitHubRulesetSummary>? cached)
            ? cached
            : await session.Api.ListRulesetsAsync(repo.Owner, repo.Name, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("GitHub says rulesets aren't available on this repo's plan");

        var objects = new List<LiveObject>();
        var unknown = new List<string>();
        GitHubRulesetSummary? owned = rulesets.FirstOrDefault(r =>
            r.SourceType == "Repository" && r.Name.Equals(RulesetMapper.RulesetName, StringComparison.OrdinalIgnoreCase));
        if (owned is not null)
        {
            JsonObject ruleset = await session.Api.GetRulesetAsync(repo.Owner, repo.Name, owned.Id, cancellationToken).ConfigureAwait(false);
            RulesetRules read = RulesetMapper.Read(ruleset);
            objects.Add(new LiveObject($"gh:ruleset/{owned.Id}", MatchKey, RulesetMapper.Native(ruleset), read.Rules, RulesetTitle));
            if (!read.BypassActorsKnown)
            {
                unknown.Add(RulesetMapper.BypassActors);
            }
        }

        var unmanaged = new List<UnmanagedSetting>();
        if (details.DefaultBranch is { } branch)
        {
            List<GitHubBranchRule> rules = await session.Api.GetBranchRulesAsync(repo.Owner, repo.Name, branch, cancellationToken).ConfigureAwait(false);
            unmanaged.AddRange(OtherRulesets(rules, rulesets, owned?.Id));
        }

        if (MergeFlags(details) is { Count: < 3 } allowed)
        {
            unmanaged.Add(new UnmanagedSetting(RulesetMapper.AllowedMergeTypes, "repo merge settings", new JsonArray([.. allowed.Select(m => (JsonNode)m)])));
        }

        return new LiveSnapshot(objects, unmanaged, [], unknown);
    }

    /// <inheritdoc />
    public IReadOnlyList<DesiredObject> MapObjects(DesiredState desired, LiveSnapshot live)
    {
        LiveObject? current = live.Objects.FirstOrDefault(o => o.MatchKey == MatchKey);

        // The ruleset is one object, so rules the policy leaves unmanaged keep their live values.
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (string rule in RulesetMapper.Rules)
        {
            if (desired.Rules.TryGetValue(rule, out DesiredRule? declared))
            {
                values[rule] = declared.Value?.DeepClone();
            }
            else if (current is not null && current.Rules.TryGetValue(rule, out JsonNode? liveValue))
            {
                values[rule] = liveValue?.DeepClone();
            }
        }

        JsonObject? payload = RulesetMapper.Build(values, current?.Native as JsonObject);

        // What the ruleset will carry once written, read back the way a live one is, so a value GitHub can't hold
        // (an empty check list, a pull request option on no pull request rule) doesn't show as a change forever.
        IReadOnlyDictionary<string, JsonNode?> carried = payload is null ? new Dictionary<string, JsonNode?>() : RulesetMapper.Read(payload).Rules;
        var rules = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (string rule in RulesetMapper.Rules.Where(desired.Rules.ContainsKey))
        {
            rules[rule] = carried.GetValueOrDefault(rule)?.DeepClone();
        }

        return [new DesiredObject(MatchKey, CreateKey, rules, payload, $"{RulesetTitle} (new)")];
    }

    /// <inheritdoc />
    public Task<ApplyResult> ApplyAsync(RepoRef repo, RepoChangeSet changes, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Writing to GitHub isn't available in this build of Setpoint yet.");

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (Session session in _sessions.Values)
        {
            session.Api.Dispose();
        }

        _sessions.Clear();
    }

    private static async Task<Diagnostic?> SkipReasonAsync(Session session, Target target, GitHubRepository repo, CancellationToken cancellationToken)
    {
        if (repo.Archived || repo.Disabled)
        {
            string state = repo.Archived ? "archived" : "disabled";
            return Diagnostic.Info(DiagnosticCodes.ArchivedRepoSkipped, $"The repo is {state}, so it is skipped.");
        }

        if (repo.Fork && !target.Definition.IncludeForks)
        {
            return Diagnostic.Info(DiagnosticCodes.ForkSkipped, "The repo is a fork, so it is skipped. Set includeForks: true on the target to manage forks.");
        }

        // Size is 0 for an empty repo, but a tiny repo can report 0 too, so only then is it worth asking for a branch.
        bool empty = repo.DefaultBranch is null
            || (repo.Size == 0 && !await session.Api.HasBranchesAsync(repo.Owner.Login, repo.Name, cancellationToken).ConfigureAwait(false));
        return empty
            ? Diagnostic.Warning(DiagnosticCodes.EmptyRepoSkipped, "The repo is empty, so it has no default branch to protect and is skipped.")
            : null;
    }

    private static IEnumerable<UnmanagedSetting> OtherRulesets(List<GitHubBranchRule> rules, List<GitHubRulesetSummary> rulesets, long? ownedId)
    {
        foreach (IGrouping<long?, GitHubBranchRule> ruleset in rules.Where(r => r.RulesetId != ownedId).GroupBy(r => r.RulesetId))
        {
            GitHubBranchRule first = ruleset.First();
            string? name = rulesets.FirstOrDefault(r => r.Id == ruleset.Key)?.Name;
            string source = name is null ? $"ruleset #{ruleset.Key}" : $"ruleset '{name}'";
            if (first.RulesetSourceType is "Organization" or "Enterprise")
            {
                source += $" ({first.RulesetSourceType.ToLowerInvariant()} {first.RulesetSource})";
            }

            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string rule, JsonNode? value) in ruleset.SelectMany(RulesetMapper.ReadUnmanaged))
            {
                if (reported.Add(rule))
                {
                    yield return new UnmanagedSetting(rule, source, value);
                }
            }
        }
    }

    // The merge methods the repo's own settings allow, or null when GitHub left the flags out because the token
    // can't read the repo's contents. Then there is nothing to compare against.
    private static List<string>? MergeFlags(GitHubRepository repo) =>
        (repo.AllowMergeCommit, repo.AllowSquashMerge, repo.AllowRebaseMerge) is (bool merge, bool squash, bool rebase)
            ? [.. new[] { (merge, "merge"), (squash, "squash"), (rebase, "rebase") }.Where(m => m.Item1).Select(m => m.Item2)]
            : null;

    private Session SessionFor(string target) =>
        _sessions.TryGetValue(target, out Session? session)
            ? session
            : throw new InvalidOperationException($"Target '{target}' hasn't been described yet.");

    private sealed record Session(GitHubApiClient Api, string Login);
}
