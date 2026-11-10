using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Api;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.AzureDevOps.Policies;

/// <summary>What the adapter read for one repo, besides its policies: what mapping the desired state needs.</summary>
/// <param name="Repo">The repo, with its ids and default branch.</param>
/// <param name="Pipelines">The project's pipelines, for resolving required check names.</param>
/// <param name="ValidUsersDescriptor">
/// The project's Project Valid Users group, whose Force push deny on the default branch Setpoint owns, or null when
/// the token can't read identities.
/// </param>
internal sealed record RepoContext(AdoRepository Repo, IReadOnlyList<AdoBuildDefinition> Pipelines, string? ValidUsersDescriptor)
{
    /// <summary>The default branch, such as <c>refs/heads/main</c>.</summary>
    public string RefName => Repo.DefaultBranch ?? throw new InvalidOperationException($"The repo '{Repo.Name}' has no default branch.");

    /// <summary>The branch name without <c>refs/heads/</c>.</summary>
    public string Branch => RefName.StartsWith("refs/heads/", StringComparison.Ordinal) ? RefName["refs/heads/".Length..] : RefName;

    /// <summary>The default branch's token in the Git Repositories security namespace.</summary>
    public string BranchToken => ForcePushAcl.BranchToken(Repo.Project.Id, Repo.Id, Branch);

    /// <summary>A pipeline's name by its id.</summary>
    public string? PipelineName(int id) => Pipelines.FirstOrDefault(p => p.Id == id)?.Name;
}

/// <summary>
/// Turns one repo's live policy configurations and default-branch ACL into a <see cref="LiveSnapshot"/>. Setpoint
/// owns the configurations of its five types scoped to exactly this repo's default branch, and the Force push deny
/// for Project Valid Users on it. Everything else that applies to the branch is unmanaged.
/// </summary>
internal static class LiveReader
{
    /// <summary>Reads a repo's live state.</summary>
    /// <param name="context">The repo and what was read about its project.</param>
    /// <param name="configurations">Every policy configuration in the repo's project.</param>
    /// <param name="acl">The default branch's ACL, empty when none is set, or null when the token can't read it.</param>
    /// <param name="identityName">An identity's name by its descriptor.</param>
    public static LiveSnapshot Read(RepoContext context, IReadOnlyList<JsonObject> configurations, JsonObject? acl, Func<string, string> identityName)
    {
        var objects = new List<LiveObject>();
        var unmanaged = new List<UnmanagedSetting>();
        var ambiguous = new List<AmbiguousMatch>();
        var unknown = new List<string>();
        var warnings = new List<RuleWarning>();

        var owned = new List<(string MatchKey, JsonObject Config)>();
        foreach (JsonObject config in configurations.Where(c => !PolicyMapper.Flag(c["isDeleted"], false)))
        {
            ScopeMatch scope = Scope(PolicyMapper.Settings(config), context.Repo.Id, context.RefName);
            if (!scope.Applies)
            {
                continue;
            }

            string? matchKey = PolicyMapper.MatchKey(config, context.RefName);
            if (scope.Owned && matchKey is not null)
            {
                owned.Add((matchKey, config));
            }
            else
            {
                unmanaged.AddRange(Unmanaged(config, scope.Text, context));
            }
        }

        foreach (IGrouping<string, (string MatchKey, JsonObject Config)> match in owned.GroupBy(o => o.MatchKey, StringComparer.Ordinal))
        {
            if (match.Count() > 1)
            {
                string[] keys = [.. match.Select(m => Key(m.Config))];
                foreach (string rule in PolicyMapper.RulesOf(PolicyMapper.TypeId(match.First().Config)))
                {
                    ambiguous.Add(new AmbiguousMatch(rule, keys, $"type + branch {context.Branch}", match.Key));
                }

                continue;
            }

            JsonObject config = match.Single().Config;
            objects.Add(new LiveObject(
                Key(config),
                match.Key,
                PolicyMapper.Native(config),
                PolicyMapper.ReadRules(config, context.PipelineName),
                $"{TypeName(config)} #{config["id"]}"));
        }

        if (acl is null || context.ValidUsersDescriptor is null)
        {
            unknown.Add(PolicyMapper.ProtectFromForcePushAndDelete);
        }
        else
        {
            ReadAcl(context, acl, identityName, objects, unmanaged, warnings);
        }

        return new LiveSnapshot(objects, unmanaged, ambiguous, unknown, warnings);
    }

    /// <summary>Whether a policy's scope covers the branch, whether Setpoint owns it, and how to describe it.</summary>
    internal static ScopeMatch Scope(JsonObject settings, string repoId, string refName)
    {
        if (settings["scope"] is not JsonArray { Count: > 0 } scopes)
        {
            return new ScopeMatch(true, false, "project-wide");
        }

        JsonObject? hit = null;
        foreach (JsonObject scope in scopes.OfType<JsonObject>())
        {
            string? repository = PolicyMapper.Text(scope["repositoryId"]);
            string? scopeRef = PolicyMapper.Text(scope["refName"]);
            string kind = PolicyMapper.Text(scope["matchKind"]) ?? "Exact";
            bool repoMatches = repository is null || repository.Equals(repoId, StringComparison.OrdinalIgnoreCase);
            bool refMatches = scopeRef is null
                || kind.Equals("DefaultBranch", StringComparison.OrdinalIgnoreCase)
                || (kind.Equals("Prefix", StringComparison.OrdinalIgnoreCase) && refName.StartsWith(scopeRef, StringComparison.OrdinalIgnoreCase))
                || (kind.Equals("Exact", StringComparison.OrdinalIgnoreCase) && refName.Equals(scopeRef, StringComparison.OrdinalIgnoreCase));
            if (repoMatches && refMatches)
            {
                hit = scope;
                break;
            }
        }

        if (hit is null)
        {
            return new ScopeMatch(false, false, "");
        }

        string? hitRepo = PolicyMapper.Text(hit["repositoryId"]);
        string? hitRef = PolicyMapper.Text(hit["refName"]);
        string hitKind = PolicyMapper.Text(hit["matchKind"]) ?? "Exact";
        bool owned = scopes.Count == 1 && hitRepo is not null && hitRef is not null && hitKind.Equals("Exact", StringComparison.OrdinalIgnoreCase);
        string text = scopes.Count > 1 ? $"shared by {scopes.Count} scopes"
            : hitRepo is null ? "project-wide"
            : hitKind.Equals("Prefix", StringComparison.OrdinalIgnoreCase) ? $"branches {hitRef}*"
            : hitKind.Equals("DefaultBranch", StringComparison.OrdinalIgnoreCase) || hitRef is null ? "repo-wide"
            : "this branch";
        return new ScopeMatch(true, owned, text);
    }

    /// <summary>The plan key of a live configuration.</summary>
    public static string Key(JsonObject config) => $"ado:policy/{config["id"]}";

    private static string TypeName(JsonObject config) =>
        PolicyMapper.Text(config["type"]?["displayName"]) ?? AzureDevOpsPolicyTypes.Required.FirstOrDefault(t => t.Id == PolicyMapper.TypeId(config)).Name ?? "Policy";

    private static IEnumerable<UnmanagedSetting> Unmanaged(JsonObject config, string scope, RepoContext context)
    {
        string source = $"policy #{config["id"]} '{TypeName(config)}' ({scope})";
        Dictionary<string, JsonNode?> rules = PolicyMapper.ReadRules(config, context.PipelineName);
        if (PolicyMapper.RulesOf(PolicyMapper.TypeId(config)).Count == 0)
        {
            // A type Setpoint doesn't manage, such as work item linking: reported so the plan says it is there.
            if (PolicyMapper.Flag(config["isEnabled"], true))
            {
                yield return new UnmanagedSetting($"azuredevops.{CamelCase(TypeName(config))}", source, JsonValue.Create(PolicyMapper.Flag(config["isBlocking"], true)));
            }

            yield break;
        }

        foreach ((string rule, JsonNode? value) in rules.Where(r => !r.Key.Contains('.', StringComparison.Ordinal)))
        {
            yield return new UnmanagedSetting(rule, source, value);
        }
    }

    private static void ReadAcl(
        RepoContext context,
        JsonObject acl,
        Func<string, string> identityName,
        List<LiveObject> objects,
        List<UnmanagedSetting> unmanaged,
        List<RuleWarning> warnings)
    {
        foreach (AccessControlEntry ace in ForcePushAcl.Entries(acl))
        {
            bool ownedAce = ace.Descriptor.Equals(context.ValidUsersDescriptor, StringComparison.OrdinalIgnoreCase);
            if (ownedAce)
            {
                var rules = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                if (ace.DeniesForcePush)
                {
                    rules[PolicyMapper.ProtectFromForcePushAndDelete] = JsonValue.Create(true);
                }

                objects.Add(new LiveObject(
                    ForcePushAcl.LiveKey(context),
                    ForcePushAcl.MatchKey(context.RefName),
                    ace.ToNative(context.BranchToken),
                    rules,
                    $"Force push permission on {context.Branch}"));
                continue;
            }

            if (ace.DeniesForcePush)
            {
                unmanaged.Add(new UnmanagedSetting(PolicyMapper.ProtectFromForcePushAndDelete, $"Force push deny for {identityName(ace.Descriptor)}", JsonValue.Create(true)));
            }
            else if (ace.AllowsForcePush)
            {
                warnings.Add(new RuleWarning(PolicyMapper.ProtectFromForcePushAndDelete, Diagnostic.Warning(
                    DiagnosticCodes.ForcePushAllowed,
                    $"protectFromForcePushAndDelete: {identityName(ace.Descriptor)} has an explicit Allow for Force push on {context.Branch}, which the deny " +
                    "Setpoint sets doesn't override. Setpoint leaves it alone; remove it in the branch's security settings to make the protection complete.")));
            }
        }
    }

    private static string CamelCase(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Select((w, i) => i == 0 ? w.ToLowerInvariant() : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}

/// <summary>How a policy's scope relates to one repo's default branch.</summary>
/// <param name="Applies">The policy takes effect on the branch.</param>
/// <param name="Owned">Its only scope is exactly this repo's default branch, so Setpoint may own it.</param>
/// <param name="Text">The scope for people: <c>project-wide</c>, <c>branches refs/heads/release*</c>.</param>
internal sealed record ScopeMatch(bool Applies, bool Owned, string Text);
