using System.Text.Json.Nodes;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.AzureDevOps.Policies;

/// <summary>
/// The neutral <c>environments</c> rule on Azure DevOps: each named environment of the repo's project, with the one
/// Approval check Setpoint owns on it. Environments belong to the project, so every repo there that declares one
/// must declare it the same way, and the plan writes it once.
/// </summary>
/// <remarks>
/// <c>reviewers</c> are the check's approvers, and all of them must approve. Azure DevOps environments have no wait
/// before a deployment (an approval's timeout is how long it may wait for approvers, which is something else), and
/// restrict branches with a separate Branch control check, so <c>waitMinutes</c> and <c>branches: default</c> are
/// ignored here with SP3002. Other checks on an environment are unmanaged; several Approval checks on one
/// environment are ambiguous (SP2201), and that environment is left alone.
/// </remarks>
internal static class EnvironmentApproval
{
    /// <summary>The neutral rule.</summary>
    public const string Environments = "environments";

    /// <summary>The Approval check type.</summary>
    public const string ApprovalTypeId = "8c6f20a7-a545-4486-9777-f762fafe0d4d";

    /// <summary>How long a new approval waits for its approvers: 30 days, the web UI's default.</summary>
    public const int DefaultTimeoutMinutes = 43200;

    private const string CreatePrefix = "ado:new/environment/";

    /// <summary>An environment's match key: <c>ado:environment/{project}/{name}</c>. Environment names ignore case.</summary>
    public static string MatchKey(string projectId, string name) => $"ado:environment/{projectId.ToLowerInvariant()}/{name.ToLowerInvariant()}";

    /// <summary>True when the match key is an environment's.</summary>
    public static bool IsMatchKey(string matchKey) => matchKey.StartsWith("ado:environment/", StringComparison.Ordinal);

    /// <summary>The project's scope, for objects its repos share.</summary>
    public static string SharedBy(string projectName) => $"project {projectName}";

    /// <summary>What one environment reads as: its live object, unless it is ambiguous, and what is left unmanaged on it.</summary>
    public sealed record ReadResult(LiveObject? Object, IReadOnlyList<UnmanagedSetting> Unmanaged, RuleWarning? Ambiguous);

    /// <summary>
    /// Reads one environment and its checks. The live object's native form is the environment's name and id and the
    /// owned Approval check without who changed it last, so the fingerprint moves when the check does.
    /// </summary>
    /// <param name="projectId">The project's id.</param>
    /// <param name="environment">The environment, as Azure DevOps lists it.</param>
    /// <param name="checks">The checks on it, with their settings.</param>
    /// <param name="approverName">The name an approver is known by in the policy, from its identity id and display name.</param>
    public static ReadResult Read(string projectId, JsonObject environment, IReadOnlyList<JsonObject> checks, Func<string, string?, string> approverName)
    {
        string name = Text(environment["name"]) ?? string.Empty;
        long id = Number(environment["id"]);
        JsonObject[] approvals = [.. checks.Where(IsApproval)];
        var unmanaged = checks.Where(c => !IsApproval(c))
            .Select(c => new UnmanagedSetting(Environments, $"check '{Text(c["type"]?["name"]) ?? "unknown"}' on environment {name}", null))
            .ToList();

        if (approvals.Length > 1)
        {
            var warning = Diagnostic.Warning(
                DiagnosticCodes.AmbiguousMatch,
                $"environments: environment {name} has {approvals.Length} Approval checks ({string.Join(", ", approvals.Select(c => $"#{Number(c["id"])}"))}); left alone.");
            return new ReadResult(null, unmanaged, new RuleWarning(Environments, warning));
        }

        JsonObject? check = approvals.FirstOrDefault() is { } owned ? Check(owned) : null;
        var native = new JsonObject { ["name"] = name, ["id"] = id, ["check"] = check };
        return new ReadResult(
            new LiveObject($"ado:environment/{id}", MatchKey(projectId, name), native, ReadRules(native, approverName), $"environment {name}"),
            unmanaged,
            null);
    }

    /// <summary>The rule values an environment in its native shape carries.</summary>
    public static Dictionary<string, JsonNode?> ReadRules(JsonObject native, Func<string, string?, string> approverName)
    {
        var value = new JsonObject();
        if (native["check"]?["settings"] is JsonObject settings)
        {
            JsonObject[] approvers = [.. (settings["approvers"] as JsonArray ?? []).OfType<JsonObject>()];
            value["reviewers"] = new JsonArray([.. approvers
                .Select(a => approverName(Text(a["id"]) ?? string.Empty, Text(a["displayName"])))
                .Order(StringComparer.Ordinal)
                .Select(n => (JsonNode)n)]);
            long required = Number(settings["minRequiredApprovers"]);
            if (required != 0 && required != approvers.Length)
            {
                // Fewer than all is no neutral setting, so it shows as a change from what the policy wants.
                value["minRequiredApprovers"] = required;
            }
        }
        else
        {
            value["reviewers"] = new JsonArray();
        }

        string key = (Text(native["name"]) ?? string.Empty).ToLowerInvariant();
        return new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { [Environments] = new JsonObject { [key] = value } };
    }

    /// <summary>
    /// The warnings for fields Azure DevOps environments don't have, for each enabled environment that sets one.
    /// </summary>
    public static IEnumerable<RuleWarning> IgnoredFields(JsonObject declared)
    {
        foreach ((string name, JsonNode? node) in declared)
        {
            if (node is not JsonObject environment || Enabled(environment) is false)
            {
                continue;
            }

            if (Number(environment["waitMinutes"]) > 0)
            {
                yield return new RuleWarning(Environments, Diagnostic.Warning(
                    DiagnosticCodes.RuleUnavailable,
                    $"environments: '{name}' sets waitMinutes, and Azure DevOps environments have no wait before a deployment, so it is ignored here."));
            }

            if (Text(environment["branches"]) == "default")
            {
                yield return new RuleWarning(Environments, Diagnostic.Warning(
                    DiagnosticCodes.RuleUnavailable,
                    $"environments: '{name}' sets branches: default, and Azure DevOps restricts branches with a Branch control check, which Setpoint doesn't manage, so it is ignored here."));
            }
        }
    }

    /// <summary>
    /// One object per environment the policy names: the environment, created if missing, and its Approval check with
    /// the declared approvers, all of whom must approve. A disabled environment, or one with no reviewers, has no check.
    /// </summary>
    /// <param name="declared">The environments map, as the policy writes it.</param>
    /// <param name="principals">Each reviewer name resolved, by the name as the policy writes it.</param>
    /// <param name="live">The live snapshot, holding the project's environments.</param>
    /// <param name="project">The repo's project.</param>
    /// <param name="ambiguous">Environments left alone, by lower-case name.</param>
    /// <param name="approverName">The name a live approver is known by, as <see cref="Read"/> names it.</param>
    public static IEnumerable<DesiredObject> Map(
        JsonObject declared,
        IReadOnlyDictionary<string, Principal> principals,
        LiveSnapshot live,
        (string Id, string Name) project,
        IReadOnlySet<string> ambiguous,
        Func<string, string?, string> approverName)
    {
        foreach ((string name, JsonNode? node) in declared)
        {
            if (node is not JsonObject environment || ambiguous.Contains(name.ToLowerInvariant()))
            {
                continue;
            }

            string matchKey = MatchKey(project.Id, name);
            LiveObject? current = live.Objects.FirstOrDefault(o => o.MatchKey == matchKey);
            JsonObject? liveCheck = current?.Native["check"] as JsonObject;
            bool disabled = Enabled(environment) is false;

            List<Principal> approvers;
            if (disabled)
            {
                approvers = [];
            }
            else if (environment["reviewers"] is JsonArray reviewers)
            {
                approvers = [.. reviewers.Select(r => Text(r) ?? string.Empty).Select(r => principals.GetValueOrDefault(r)
                    ?? throw new PolicyMappingException(DiagnosticCodes.PrincipalNotFound, $"environments: '{r}' wasn't looked up."))];
            }
            else
            {
                // reviewers left out keeps the live approvers; they are already named the way the policy would name them.
                approvers = [.. (liveCheck?["settings"]?["approvers"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(a => new Principal(approverName(Text(a["id"]) ?? string.Empty, Text(a["displayName"])), string.Empty, Text(a["id"])))];
            }

            approvers = [.. approvers.DistinctBy(a => a.Id ?? a.Descriptor, StringComparer.OrdinalIgnoreCase)];
            JsonObject? check = approvers.Count == 0 ? null : ApprovalCheck(approvers, liveCheck, name);
            var carriedValue = new JsonObject
            {
                ["reviewers"] = new JsonArray([.. approvers.Select(a => a.Name).Order(StringComparer.Ordinal).Select(n => (JsonNode)n)]),
            };
            var carried = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { [Environments] = new JsonObject { [name.ToLowerInvariant()] = carriedValue } };

            // A disabled environment that doesn't exist stays that way: environments are created only to protect them.
            JsonObject? payload = current is null && disabled
                ? null
                : new JsonObject
                {
                    ["environment"] = Text(current?.Native["name"]) ?? name,
                    ["checkId"] = liveCheck is null ? null : Number(liveCheck["id"]),
                    ["check"] = check,
                };
            yield return new DesiredObject(matchKey, CreatePrefix + name, carried, payload, $"environment {name} (new)", SharedBy(project.Name));
        }
    }

    /// <summary>True when a check is an Approval check.</summary>
    public static bool IsApproval(JsonObject check) => string.Equals(Text(check["type"]?["id"]), ApprovalTypeId, StringComparison.OrdinalIgnoreCase);

    // The owned check as Setpoint keeps it: what it is and does, without who changed it last.
    private static JsonObject Check(JsonObject check) => new()
    {
        ["id"] = Number(check["id"]),
        ["version"] = Number(check["version"]),
        ["type"] = check["type"]?.DeepClone(),
        ["settings"] = check["settings"]?.DeepClone(),
        ["timeout"] = Number(check["timeout"]),
    };

    // The Approval check to write: the live one's settings Setpoint doesn't manage, such as its instructions and
    // timeout, are kept. The resource id is filled in at apply, because a new environment has none yet.
    private static JsonObject ApprovalCheck(List<Principal> approvers, JsonObject? live, string environment)
    {
        JsonObject settings = live?["settings"]?.DeepClone() as JsonObject ?? new JsonObject
        {
            ["executionOrder"] = "anyOrder",
            ["instructions"] = string.Empty,
            ["blockedApprovers"] = new JsonArray(),
            ["requesterCannotBeApprover"] = false,
        };
        settings["approvers"] = new JsonArray([.. approvers
            .OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["displayName"] = a.Name })]);
        settings["minRequiredApprovers"] = approvers.Count;
        var check = new JsonObject
        {
            ["type"] = new JsonObject { ["id"] = ApprovalTypeId, ["name"] = "Approval" },
            ["settings"] = settings,
            ["resource"] = new JsonObject { ["type"] = "environment", ["name"] = environment },
            ["timeout"] = live is null ? DefaultTimeoutMinutes : Number(live["timeout"]),
        };
        if (live is not null)
        {
            check["id"] = Number(live["id"]);
            check["version"] = Number(live["version"]);
        }

        return check;
    }

    private static bool? Enabled(JsonObject environment) =>
        environment["enabled"] is JsonValue value && value.TryGetValue(out bool enabled) ? enabled : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static long Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out long number) ? number : 0;
}
