using System.Text.Json.Nodes;

namespace Setpoint.Adapters.AzureDevOps.Policies;

/// <summary>
/// Translates between Azure DevOps policy configurations and the neutral model, both ways. <see cref="ReadRules"/>
/// reads a live configuration and a payload the same way, so what <see cref="ObjectMapper"/> reports a
/// payload will carry is exactly what a read would find once it is written.
/// </summary>
internal static class PolicyMapper
{
    public const string MinimumReviewers = "minimumReviewers";
    public const string RequiredChecks = "requiredChecks";
    public const string RequireCommentResolution = "requireCommentResolution";
    public const string AllowedMergeTypes = "allowedMergeTypes";
    public const string ProtectFromForcePushAndDelete = "protectFromForcePushAndDelete";
    public const string MinimumReviewersOptions = "azuredevops.minimumReviewers";
    public const string RequiredChecksOptions = "azuredevops.requiredChecks";
    public const string MergeTypesOptions = "azuredevops.allowedMergeTypes";

    /// <summary>A build validation result's lifetime when the policy doesn't say: 12 hours, as the Azure DevOps UI sets it.</summary>
    public const int DefaultValidDuration = 720;

    /// <summary>The policy type id of a configuration or payload.</summary>
    public static string? TypeId(JsonObject config) => Text(config["type"]?["id"])?.ToLowerInvariant();

    /// <summary>The configuration's settings, or an empty object.</summary>
    public static JsonObject Settings(JsonObject config) => config["settings"] as JsonObject ?? [];

    /// <summary>
    /// The rules a configuration carries, keyed by rule. A configuration that is disabled, optional (not blocking)
    /// or deleted carries none, because it doesn't hold a pull request back the way the policy means.
    /// </summary>
    /// <param name="config">A live configuration or a payload.</param>
    /// <param name="pipelineName">The name of a pipeline by its id, or null when there is none.</param>
    public static Dictionary<string, JsonNode?> ReadRules(JsonObject config, Func<int, string?> pipelineName)
    {
        var rules = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (!Flag(config["isEnabled"], true) || !Flag(config["isBlocking"], true) || Flag(config["isDeleted"], false))
        {
            return rules;
        }

        JsonObject settings = Settings(config);
        switch (TypeId(config))
        {
            case AzureDevOpsPolicyTypes.MinimumReviewers:
                var reviewers = new JsonObject { ["resetOnPush"] = Flag(settings["resetOnSourcePush"], false) };
                if (Number(settings["minimumApproverCount"]) is { } count)
                {
                    reviewers["count"] = count;
                }

                rules[MinimumReviewers] = reviewers;
                rules[MinimumReviewersOptions] = new JsonObject
                {
                    ["allowDownvotes"] = Flag(settings["allowDownvotes"], false),
                    ["blockLastPusherVote"] = Flag(settings["blockLastPusherVote"], false),
                    ["creatorVoteCounts"] = Flag(settings["creatorVoteCounts"], false),
                };
                break;

            case AzureDevOpsPolicyTypes.Build:
                int id = Number(settings["buildDefinitionId"]) ?? 0;
                rules[RequiredChecks] = new JsonArray((JsonNode)new JsonObject { ["name"] = pipelineName(id) ?? $"#{id}" });
                var build = new JsonObject();
                if (Number(settings["validDuration"]) is { } minutes)
                {
                    build["validDuration"] = minutes;
                }

                if (settings["filenamePatterns"] is JsonArray { Count: > 0 } patterns)
                {
                    build["pathFilters"] = patterns.DeepClone();
                }

                rules[RequiredChecksOptions] = build;
                break;

            case AzureDevOpsPolicyTypes.Status:
                var check = new JsonObject { ["name"] = Text(settings["statusName"]) ?? "" };
                if (Text(settings["statusGenre"]) is { } genre)
                {
                    check["genre"] = genre;
                }

                rules[RequiredChecksOptions] = new JsonObject { ["statusChecks"] = new JsonArray((JsonNode)check) };
                break;

            case AzureDevOpsPolicyTypes.CommentRequirements:
                rules[RequireCommentResolution] = JsonValue.Create(true);
                break;

            case AzureDevOpsPolicyTypes.MergeStrategy:
                rules[AllowedMergeTypes] = new JsonArray([.. MergeTypes(settings).Select(t => (JsonNode)t)]);
                rules[MergeTypesOptions] = new JsonObject { ["allowRebaseMerge"] = Flag(settings["allowRebaseMerge"], false) };
                break;
        }

        return rules;
    }

    /// <summary>
    /// The merge types a merge strategy policy allows. Servers before the four <c>allow*</c> flags only had
    /// <c>useSquashMerge</c>: true meant squash only, and false meant a merge commit only.
    /// </summary>
    public static IEnumerable<string> MergeTypes(JsonObject settings)
    {
        bool modern = settings.ContainsKey("allowNoFastForward") || settings.ContainsKey("allowSquash")
            || settings.ContainsKey("allowRebase") || settings.ContainsKey("allowRebaseMerge");
        if (!modern)
        {
            yield return Flag(settings["useSquashMerge"], false) ? "squash" : "merge";
            yield break;
        }

        if (Flag(settings["allowNoFastForward"], false))
        {
            yield return "merge";
        }

        if (Flag(settings["allowSquash"], false))
        {
            yield return "squash";
        }

        if (Flag(settings["allowRebase"], false))
        {
            yield return "rebase";
        }
    }

    /// <summary>The match key of a configuration Setpoint could own on <paramref name="refName"/>.</summary>
    public static string? MatchKey(JsonObject config, string refName)
    {
        JsonObject settings = Settings(config);
        return TypeId(config) switch
        {
            AzureDevOpsPolicyTypes.MinimumReviewers => $"ado:minimumReviewers/{refName}",
            AzureDevOpsPolicyTypes.CommentRequirements => $"ado:commentRequirements/{refName}",
            AzureDevOpsPolicyTypes.MergeStrategy => $"ado:mergeStrategy/{refName}",
            AzureDevOpsPolicyTypes.Build => BuildMatchKey(refName, Number(settings["buildDefinitionId"]) ?? 0),
            AzureDevOpsPolicyTypes.Status => StatusMatchKey(refName, Text(settings["statusName"]) ?? "", Text(settings["statusGenre"])),
            _ => null,
        };
    }

    public static string BuildMatchKey(string refName, int definitionId) => $"ado:build/{refName}/{definitionId}";

    public static string StatusMatchKey(string refName, string name, string? genre) => $"ado:status/{refName}/{StatusName(name, genre)}";

    /// <summary>A status check as people write it: <c>genre/name</c>, or the name alone.</summary>
    public static string StatusName(string name, string? genre) => string.IsNullOrEmpty(genre) ? name : $"{genre}/{name}";

    /// <summary>The rules each managed policy type governs, for ambiguous matches.</summary>
    public static IReadOnlyList<string> RulesOf(string? typeId) => typeId switch
    {
        AzureDevOpsPolicyTypes.MinimumReviewers => [MinimumReviewers, MinimumReviewersOptions],
        AzureDevOpsPolicyTypes.Build => [RequiredChecks],
        AzureDevOpsPolicyTypes.Status => [RequiredChecksOptions],
        AzureDevOpsPolicyTypes.CommentRequirements => [RequireCommentResolution],
        AzureDevOpsPolicyTypes.MergeStrategy => [AllowedMergeTypes, MergeTypesOptions],
        _ => [],
    };

    /// <summary>The configuration without what changes on every read or depends on who reads it. The fingerprint is taken over it.</summary>
    public static JsonObject Native(JsonObject config)
    {
        var native = (JsonObject)config.DeepClone();
        native.Remove("_links");
        native.Remove("url");
        native.Remove("createdBy");
        return native;
    }

    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : null;

    public static bool Flag(JsonNode? node, bool fallback) =>
        node is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;

    // Azure DevOps writes some whole numbers as doubles (validDuration: 720.0), so read either.
    public static int? Number(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue(out int whole) ? whole : value.TryGetValue(out double real) ? (int)real : null
        : null;
}
