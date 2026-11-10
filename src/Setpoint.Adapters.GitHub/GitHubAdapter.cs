using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Api;
using Setpoint.Adapters.GitHub.Auth;
using Setpoint.Adapters.GitHub.Policy;
using Setpoint.Adapters.GitHub.Rulesets;
using Setpoint.Adapters.GitHub.Teams;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;

namespace Setpoint.Adapters.GitHub;

/// <summary>
/// The GitHub adapter. Setpoint owns one ruleset per repo, named <c>setpoint</c>, on the default branch; every other
/// ruleset and the repo's own merge settings are unmanaged, and are only read to warn when they are stricter. It
/// also owns the permission on the repo of each team the <c>access</c> rule names.
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
    private readonly ConcurrentDictionary<RepoRef, TeamContext> _teams = new();
    private readonly ConcurrentDictionary<(string Target, string Org), Lazy<Task<IReadOnlySet<string>?>>> _orgTeams = new();

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
        GitHubCredentials credentials = auth switch
        {
            AuthMethod.Pat => new PatCredentials(Secret(TargetCredentials.TokenVariable(target), "token")),
            AuthMethod.App => new AppCredentials(
                target.Definition.AppId ?? throw new InvalidOperationException("auth: app needs the App's appId in targets.yml"),
                GitHubAppJwt.ImportKey(Secret(TargetCredentials.AppKeyVariable(target), "App private key"), TargetCredentials.AppKeyVariable(target)),
                _options.Time,
                _redactor.Add),
            _ => throw new NotSupportedException($"auth: {auth.ToString().ToLowerInvariant()} isn't available on GitHub; use pat or app"),
        };

        Uri apiRoot = GitHubEndpoint.ApiRoot(target.Definition.Url);
        HttpClient http = TargetHttpClientFactory.Create(
            target.Name,
            apiRoot,
            _options.Http,
            _options.Logger,
            _redactor,
            [new GitHubAuthHandler(credentials)],
            timeProvider: _options.Time);
        var api = new GitHubApiClient(http);
        try
        {
            Session session;
            string? enterpriseVersion;
            if (credentials is AppCredentials app)
            {
                app.Attach(api);
                (GitHubApp self, enterpriseVersion) = await api.GetAppAsync(cancellationToken).ConfigureAwait(false);
                await FindInstallationsAsync(api, app, target, cancellationToken).ConfigureAwait(false);
                session = new Session(api, $"{self.Slug}[bot]", app);
            }
            else
            {
                (GitHubUser user, enterpriseVersion) = await api.GetUserAsync(cancellationToken).ConfigureAwait(false);
                session = new Session(api, user.Login, null);
            }

            if (_sessions.TryRemove(target.Name, out Session? previous))
            {
                previous.Dispose();
            }

            _sessions[target.Name] = session;
            return new TargetInfo(GitHubEndpoint.Describe(apiRoot, enterpriseVersion), GitHubApiClient.ApiVersion, session.Login);
        }
        catch
        {
            api.Dispose();
            (credentials as IDisposable)?.Dispose();
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
        IEnumerable<IAsyncEnumerable<GitHubRepository>> sources = session.App is { } app
            ? app.Owners.Select(o => session.Api.ListInstallationReposAsync(o, cancellationToken))
            : anyOwner
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

        _teams[repo] = await ReadTeamsAsync(session, repo, details.Owner, objects, unknown, cancellationToken).ConfigureAwait(false);
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

        TeamContext teams = _teams.GetValueOrDefault(desired.Repo) ?? new TeamContext(new GitHubAccount(desired.Repo.Owner), null);
        return [new DesiredObject(MatchKey, CreateKey, rules, payload, $"{RulesetTitle} (new)"), .. TeamAccess.Map(desired.Rules, teams.Owner, teams.OrgTeams)];
    }

    // A repo's teams, each with its permission, as live objects. A token that may not list them leaves access
    // unknown. The organization's teams are listed once per run, so a misspelt slug fails the plan, not the apply.
    private async Task<TeamContext> ReadTeamsAsync(Session session, RepoRef repo, GitHubAccount owner, List<LiveObject> objects, List<string> unknown, CancellationToken cancellationToken)
    {
        if (!owner.MayHaveTeams)
        {
            return new TeamContext(owner, null);
        }

        List<GitHubTeam>? teams = await session.Api.ListRepoTeamsAsync(repo.Owner, repo.Name, cancellationToken).ConfigureAwait(false);
        if (teams is null)
        {
            unknown.AddRange(TeamAccess.Rules);
        }
        else
        {
            objects.AddRange(TeamAccess.Read(teams));
        }

        IReadOnlySet<string>? orgTeams = await _orgTeams
            .GetOrAdd((repo.Target, owner.Login.ToLowerInvariant()), key => new Lazy<Task<IReadOnlySet<string>?>>(async () =>
                (await session.Api.ListOrgTeamsAsync(owner.Login, cancellationToken).ConfigureAwait(false))?
                    .Select(t => t.Slug.ToLowerInvariant())
                    .ToHashSet(StringComparer.Ordinal)))
            .Value.ConfigureAwait(false);
        return new TeamContext(owner, orgTeams);
    }

    /// <summary>
    /// Writes the planned setpoint ruleset: the planned payload is sent exactly as it is in the plan, with nothing
    /// recomputed. Only the setpoint ruleset is ever written; other rulesets, classic branch protection and repo
    /// settings are never touched. The first failed write stops the repo, and the rest are reported as not attempted.
    /// </summary>
    public async Task<ApplyResult> ApplyAsync(RepoRef repo, RepoChangeSet changes, CancellationToken cancellationToken)
    {
        Session session = SessionFor(repo.Target);
        var outcomes = new List<ObjectApplyOutcome>();
        string? stoppedBy = null;
        foreach (PlanObject planned in changes.Objects)
        {
            if (stoppedBy is not null)
            {
                outcomes.Add(new ObjectApplyOutcome(planned.Key, false, $"Not attempted, because writing {stoppedBy} failed."));
                continue;
            }

            string? error = await WriteAsync(session.Api, repo, planned, cancellationToken).ConfigureAwait(false);
            outcomes.Add(new ObjectApplyOutcome(planned.Key, error is null, error));
            if (error is not null)
            {
                stoppedBy = planned.Key;
            }
        }

        return new ApplyResult(outcomes);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (Session session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private static async Task<string?> WriteAsync(GitHubApiClient api, RepoRef repo, PlanObject planned, CancellationToken cancellationToken)
    {
        if (TeamAccess.Slug(planned.MatchKey) is { } slug)
        {
            return await WriteTeamAsync(api, repo, slug, planned, cancellationToken).ConfigureAwait(false);
        }

        if (planned.MatchKey != MatchKey)
        {
            return $"{planned.Key} is not the setpoint ruleset or a team's access; the GitHub adapter writes nothing else.";
        }

        long? id = RulesetId(planned.Key);
        HttpResponseMessage response;
        try
        {
            response = (planned.Operation, planned.Payload, id) switch
            {
                (ObjectOperation.Create, JsonObject payload, _) => await api.CreateRulesetAsync(repo.Owner, repo.Name, payload, cancellationToken).ConfigureAwait(false),
                (ObjectOperation.Update, JsonObject payload, long existing) => await api.UpdateRulesetAsync(repo.Owner, repo.Name, existing, payload, cancellationToken).ConfigureAwait(false),
                (ObjectOperation.Delete, _, long existing) => await api.DeleteRulesetAsync(repo.Owner, repo.Name, existing, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"The plan's {planned.Operation.ToString().ToLowerInvariant()} of {planned.Key} has no payload or ruleset id to write."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A connection failure or timeout fails this repo only; the caller carries on with the others.
            return ex.Message;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string message = GitHubMessage(body) ?? response.ReasonPhrase ?? "no message";
            const string permission = "The token needs Administration: write on the repo (a classic token needs the repo scope), " +
                "and its account a role that may edit repository rules.";
            return (int)response.StatusCode switch
            {
                403 => $"GitHub refused to write the setpoint ruleset (403: {message}). {permission}",
                404 when planned.Operation == ObjectOperation.Create => $"GitHub couldn't find the repo to write the setpoint ruleset (404: {message}). {permission}",
                404 => $"The setpoint ruleset {planned.Key} no longer exists, or the token can't see it (404: {message}). Plan again.",
                422 => $"GitHub rejected the setpoint ruleset (422: {message}).",
                int status => $"Writing the setpoint ruleset failed ({status}: {message}).",
            };
        }
    }

    private static long? RulesetId(string key) =>
        key.StartsWith("gh:ruleset/", StringComparison.Ordinal) && long.TryParse(key["gh:ruleset/".Length..], out long id) ? id : null;

    private static string? GitHubMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["message"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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

    private string Secret(string variable, string what)
    {
        string value = _options.GetEnvironmentVariable(variable) is { Length: > 0 } set
            ? set
            : throw new InvalidOperationException($"the environment variable {variable} holding its {what} is not set");
        _redactor.Add(value);
        return value;
    }

    // An App sees only the accounts it is installed on: one per literal scope owner, or all of them for a wildcard.
    private static async Task FindInstallationsAsync(GitHubApiClient api, AppCredentials app, Target target, CancellationToken cancellationToken)
    {
        string[] owners = [.. (target.Definition.Scope ?? []).Select(g => g.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (owners.Any(o => o.Contains('*', StringComparison.Ordinal) || o.Contains('?', StringComparison.Ordinal)))
        {
            foreach (GitHubInstallation installation in await api.ListInstallationsAsync(cancellationToken).ConfigureAwait(false))
            {
                app.AddInstallation(installation.Account.Login, installation.Id);
            }
        }

        foreach (string owner in owners.Where(o => !o.Contains('*', StringComparison.Ordinal) && !o.Contains('?', StringComparison.Ordinal)))
        {
            GitHubInstallation installation = await api.FindInstallationAsync(owner, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"the GitHub App isn't installed on '{owner}'; install it there or take the owner out of scope");
            app.AddInstallation(installation.Account.Login, installation.Id);
        }
    }

    // A team's permission on the repo: PUT with the planned payload sets it, DELETE takes the repo away from the team.
    private static async Task<string?> WriteTeamAsync(GitHubApiClient api, RepoRef repo, string slug, PlanObject planned, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = (planned.Operation, planned.Payload) switch
            {
                (ObjectOperation.Create or ObjectOperation.Update, JsonObject payload) =>
                    await api.SetTeamRepoAsync(repo.Owner, slug, repo.Owner, repo.Name, payload, cancellationToken).ConfigureAwait(false),
                (ObjectOperation.Delete, _) => await api.RemoveTeamRepoAsync(repo.Owner, slug, repo.Owner, repo.Name, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"The plan's {planned.Operation.ToString().ToLowerInvariant()} of {planned.Key} has no payload to write."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ex.Message;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string message = GitHubMessage(body) ?? response.ReasonPhrase ?? "no message";
            const string permission = "The token needs Administration: write on the repo and Members: read on the organization " +
                "(a classic token needs the repo and read:org scopes), and its account must be an organization owner, or a maintainer of the team with admin on the repo.";
            return (int)response.StatusCode switch
            {
                403 => $"GitHub refused to change team {slug}'s access (403: {message}). {permission}",
                404 => $"GitHub couldn't find team {slug} or the repo (404: {message}). Check the slug. {permission}",
                422 => $"GitHub rejected team {slug}'s access (422: {message}).",
                int status => $"Changing team {slug}'s access failed ({status}: {message}).",
            };
        }
    }

    /// <summary>What mapping the access rule needs to know about a repo: its owner, and the owner's team slugs if they could be listed.</summary>
    private sealed record TeamContext(GitHubAccount Owner, IReadOnlySet<string>? OrgTeams);

    private sealed record Session(GitHubApiClient Api, string Login, AppCredentials? App) : IDisposable
    {
        public void Dispose()
        {
            Api.Dispose();
            App?.Dispose();
        }
    }
}
