using System.Text.Json.Nodes;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.AzureDevOps.Policies;

/// <summary>
/// Maps a repo's desired state to the Azure DevOps objects that should exist: one policy configuration per managed
/// policy type on the default branch, one build validation policy per required pipeline, one status policy per
/// external status check, and the Force push deny. Each payload starts from the live object, so settings the policy
/// doesn't manage keep their values. The rules each object reports are its payload read back the way a live object
/// is read, so a value Azure DevOps can't hold never shows as a change forever.
/// </summary>
internal static class ObjectMapper
{
    /// <summary>Maps the desired state.</summary>
    /// <exception cref="PolicyMappingException">A required check names no pipeline in the project, or more than one (SP0206).</exception>
    public static IReadOnlyList<DesiredObject> Map(DesiredState desired, LiveSnapshot live, RepoContext context)
    {
        var objects = new List<DesiredObject>();
        IReadOnlyDictionary<string, DesiredRule> rules = desired.Rules;
        LiveObject? Live(string matchKey) => live.Objects.FirstOrDefault(o => o.MatchKey == matchKey);

        MapMinimumReviewers(rules, context, Live, objects);
        MapSingle(rules, context, Live, objects, AzureDevOpsPolicyTypes.CommentRequirements, PolicyMapper.RequireCommentResolution, null, "commentRequirements", "Comment requirements", (_, _) => { });
        MapSingle(rules, context, Live, objects, AzureDevOpsPolicyTypes.MergeStrategy, PolicyMapper.AllowedMergeTypes, PolicyMapper.MergeTypesOptions, "mergeStrategy", "Require a merge strategy", SetMergeTypes);
        MapBuilds(desired, live, context, objects);
        MapStatusChecks(rules, live, context, objects);
        MapForcePush(rules, context, Live, objects);
        return objects;
    }

    private static void MapMinimumReviewers(IReadOnlyDictionary<string, DesiredRule> rules, RepoContext context, Func<string, LiveObject?> live, List<DesiredObject> objects) =>
        MapSingle(rules, context, live, objects, AzureDevOpsPolicyTypes.MinimumReviewers, PolicyMapper.MinimumReviewers, PolicyMapper.MinimumReviewersOptions, "minimumReviewers", "Minimum number of reviewers", (settings, declared) =>
        {
            if (declared.TryGetValue(PolicyMapper.MinimumReviewers, out JsonNode? reviewers) && reviewers is JsonObject neutral)
            {
                if (PolicyMapper.Number(neutral["count"]) is { } count)
                {
                    settings["minimumApproverCount"] = count;
                }

                settings["resetOnSourcePush"] = PolicyMapper.Flag(neutral["resetOnPush"], false);
            }

            JsonObject? options = declared.GetValueOrDefault(PolicyMapper.MinimumReviewersOptions) as JsonObject;
            foreach (string flag in new[] { "creatorVoteCounts", "allowDownvotes", "blockLastPusherVote" })
            {
                if (options is not null)
                {
                    settings[flag] = PolicyMapper.Flag(options[flag], false);
                }
                else
                {
                    settings[flag] ??= false;
                }
            }
        });

    private static void SetMergeTypes(JsonObject settings, IReadOnlyDictionary<string, JsonNode?> declared)
    {
        if (declared.TryGetValue(PolicyMapper.AllowedMergeTypes, out JsonNode? types) && types is JsonArray list)
        {
            var names = list.Select(t => PolicyMapper.Text(t)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            settings["allowNoFastForward"] = names.Contains("merge");
            settings["allowSquash"] = names.Contains("squash");
            settings["allowRebase"] = names.Contains("rebase");
        }
        else
        {
            // Only the provider option is managed: keep the live types, in the modern form.
            string[] live = [.. PolicyMapper.MergeTypes(settings)];
            settings["allowNoFastForward"] = live.Contains("merge");
            settings["allowSquash"] = live.Contains("squash");
            settings["allowRebase"] = live.Contains("rebase");
        }

        settings.Remove("useSquashMerge");
        settings["allowRebaseMerge"] = declared.GetValueOrDefault(PolicyMapper.MergeTypesOptions) is JsonObject options
            ? PolicyMapper.Flag(options["allowRebaseMerge"], false)
            : PolicyMapper.Flag(settings["allowRebaseMerge"], false);
    }

    // A policy type there is one of per branch. The neutral rule decides whether it exists; its provider options
    // only adjust a policy that does.
    private static void MapSingle(
        IReadOnlyDictionary<string, DesiredRule> rules,
        RepoContext context,
        Func<string, LiveObject?> live,
        List<DesiredObject> objects,
        string typeId,
        string rule,
        string? optionsRule,
        string name,
        string title,
        Action<JsonObject, IReadOnlyDictionary<string, JsonNode?>> set)
    {
        string[] declared = [.. new[] { rule, optionsRule }.OfType<string>().Where(rules.ContainsKey)];
        if (declared.Length == 0)
        {
            return;
        }

        string matchKey = $"ado:{name}/{context.RefName}";
        LiveObject? current = live(matchKey);
        bool neutralDeclared = rules.TryGetValue(rule, out DesiredRule? neutral);
        if ((neutralDeclared && neutral!.MustBeAbsent) || (!neutralDeclared && current is null))
        {
            // Absent, or only options for a policy that doesn't exist: nothing to create.
            objects.Add(Absent(matchKey, $"ado:new/{name}", declared));
            return;
        }

        JsonObject payload = PolicyPayload(typeId, current, context, settings =>
            set(settings, declared.ToDictionary(r => r, r => rules[r].Value, StringComparer.Ordinal)));
        objects.Add(Carried(matchKey, $"ado:new/{name}", payload, declared, context, $"{title} (new)"));
    }

    private static void MapBuilds(DesiredState desired, LiveSnapshot live, RepoContext context, List<DesiredObject> objects)
    {
        IReadOnlyDictionary<string, DesiredRule> rules = desired.Rules;
        bool checksDeclared = rules.TryGetValue(PolicyMapper.RequiredChecks, out DesiredRule? checks);
        JsonObject? options = rules.GetValueOrDefault(PolicyMapper.RequiredChecksOptions)?.Value as JsonObject;
        string[] declared = [.. new[] { PolicyMapper.RequiredChecks, PolicyMapper.RequiredChecksOptions }.Where(rules.ContainsKey)];
        if (declared.Length == 0)
        {
            return;
        }

        LiveObject[] liveBuilds = [.. live.Objects.Where(o => o.MatchKey.StartsWith($"ado:build/{context.RefName}/", StringComparison.Ordinal))];
        if (!checksDeclared)
        {
            // Only the options are managed: they adjust the build policies that exist.
            foreach (LiveObject current in liveBuilds)
            {
                JsonObject payload = PolicyPayload(AzureDevOpsPolicyTypes.Build, current, context, s => SetBuildOptions(s, options));
                objects.Add(Carried(current.MatchKey, current.Key, payload, declared, context, current.Title));
            }

            return;
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in Names(checks!.Value))
        {
            var pipeline = Resolve(name, context);
            string matchKey = PolicyMapper.BuildMatchKey(context.RefName, pipeline.Id);
            wanted.Add(matchKey);
            LiveObject? current = liveBuilds.FirstOrDefault(o => o.MatchKey == matchKey);
            JsonObject payload = PolicyPayload(AzureDevOpsPolicyTypes.Build, current, context, settings =>
            {
                settings["buildDefinitionId"] = pipeline.Id;
                settings["queueOnSourceUpdateOnly"] ??= true;
                settings["manualQueueOnly"] ??= false;
                settings["validDuration"] ??= PolicyMapper.DefaultValidDuration;
                SetBuildOptions(settings, options);
            });
            objects.Add(Carried(matchKey, $"ado:new/build/{pipeline.Name}", payload, declared, context, "Build validation (new)"));
        }

        foreach (LiveObject extra in liveBuilds.Where(o => !wanted.Contains(o.MatchKey)))
        {
            objects.Add(Absent(extra.MatchKey, extra.Key, declared));
        }
    }

    private static void SetBuildOptions(JsonObject settings, JsonObject? options)
    {
        if (options is null)
        {
            return;
        }

        if (PolicyMapper.Number(options["validDuration"]) is { } minutes)
        {
            settings["validDuration"] = minutes;
        }

        if (options["pathFilters"] is JsonArray filters)
        {
            settings["filenamePatterns"] = filters.DeepClone();
        }
    }

    private static void MapStatusChecks(IReadOnlyDictionary<string, DesiredRule> rules, LiveSnapshot live, RepoContext context, List<DesiredObject> objects)
    {
        if (!rules.TryGetValue(PolicyMapper.RequiredChecksOptions, out DesiredRule? options))
        {
            return;
        }

        // Status checks are managed when the options list them; an absent options rule removes them all.
        JsonNode? statusChecks = options.Value is null ? new JsonArray() : options.Value["statusChecks"];
        if (statusChecks is not JsonArray checks)
        {
            return;
        }

        string[] declared = [PolicyMapper.RequiredChecksOptions];
        LiveObject[] liveStatus = [.. live.Objects.Where(o => o.MatchKey.StartsWith($"ado:status/{context.RefName}/", StringComparison.Ordinal))];
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonObject check in checks.OfType<JsonObject>())
        {
            string name = PolicyMapper.Text(check["name"]) ?? "";
            string? genre = PolicyMapper.Text(check["genre"]);
            string matchKey = PolicyMapper.StatusMatchKey(context.RefName, name, genre);
            wanted.Add(matchKey);
            LiveObject? current = liveStatus.FirstOrDefault(o => o.MatchKey == matchKey);
            JsonObject payload = PolicyPayload(AzureDevOpsPolicyTypes.Status, current, context, settings =>
            {
                settings["statusName"] = name;
                settings["statusGenre"] = genre;
                settings["invalidateOnSourceUpdate"] ??= false;
            });
            objects.Add(Carried(matchKey, $"ado:new/status/{PolicyMapper.StatusName(name, genre)}", payload, declared, context, "Status check (new)"));
        }

        foreach (LiveObject extra in liveStatus.Where(o => !wanted.Contains(o.MatchKey)))
        {
            objects.Add(Absent(extra.MatchKey, extra.Key, declared));
        }
    }

    private static void MapForcePush(IReadOnlyDictionary<string, DesiredRule> rules, RepoContext context, Func<string, LiveObject?> live, List<DesiredObject> objects)
    {
        const string rule = PolicyMapper.ProtectFromForcePushAndDelete;
        if (!rules.TryGetValue(rule, out DesiredRule? protect) || context.ValidUsersDescriptor is not { } descriptor)
        {
            return;
        }

        string matchKey = ForcePushAcl.MatchKey(context.RefName);
        LiveObject? current = live(matchKey);
        AccessControlEntry entry = current?.Native is JsonObject native
            ? new AccessControlEntry(descriptor, PolicyMapper.Number(native["allow"]) ?? 0, PolicyMapper.Number(native["deny"]) ?? 0)
            : new AccessControlEntry(descriptor, 0, 0);

        if (protect.MustBeAbsent && current is null)
        {
            objects.Add(Absent(matchKey, ForcePushAcl.CreateKey(context), [rule]));
            return;
        }

        // Taking the protection off clears the deny bit and keeps the entry, so it is an update, never a delete.
        AccessControlEntry written = entry.WithForcePushDenied(!protect.MustBeAbsent);
        JsonObject payload = ForcePushAcl.Payload(context.BranchToken, written);
        var carried = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { [rule] = written.DeniesForcePush ? JsonValue.Create(true) : null };
        objects.Add(new DesiredObject(matchKey, ForcePushAcl.CreateKey(context), carried, payload, $"Force push permission on {context.Branch} (new)"));
    }

    private static JsonObject PolicyPayload(string typeId, LiveObject? current, RepoContext context, Action<JsonObject> set)
    {
        var settings = current?.Native["settings"] is JsonObject live ? (JsonObject)live.DeepClone() : [];
        settings["scope"] = new JsonArray((JsonNode)new JsonObject
        {
            ["repositoryId"] = context.Repo.Id,
            ["refName"] = context.RefName,
            ["matchKind"] = "Exact",
        });
        set(settings);
        return new JsonObject
        {
            ["isEnabled"] = true,
            ["isBlocking"] = true,
            ["type"] = new JsonObject { ["id"] = typeId },
            ["settings"] = settings,
        };
    }

    private static DesiredObject Carried(string matchKey, string createKey, JsonObject payload, IReadOnlyList<string> declared, RepoContext context, string? title)
    {
        Dictionary<string, JsonNode?> read = PolicyMapper.ReadRules(payload, context.PipelineName);
        var carried = declared.ToDictionary(r => r, r => read.GetValueOrDefault(r)?.DeepClone(), StringComparer.Ordinal);
        return new DesiredObject(matchKey, createKey, carried, payload, title);
    }

    private static DesiredObject Absent(string matchKey, string createKey, IReadOnlyList<string> declared) =>
        new(matchKey, createKey, declared.ToDictionary(r => r, _ => (JsonNode?)null, StringComparer.Ordinal), null);

    private static IEnumerable<string> Names(JsonNode? checks) =>
        checks is JsonArray array ? array.Select(c => PolicyMapper.Text(c?["name"])).OfType<string>() : [];

    private static Api.AdoBuildDefinition Resolve(string name, RepoContext context)
    {
        Api.AdoBuildDefinition[] matches = [.. context.Pipelines.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))];
        return matches switch
        {
            [var only] => only,
            [] => throw new PolicyMappingException(
                DiagnosticCodes.PipelineNotFound,
                $"requiredChecks: no pipeline is named '{name}' in project {context.Repo.Project.Name}. On Azure DevOps a required check is the pipeline that build validation runs; check the name."),
            _ => throw new PolicyMappingException(
                DiagnosticCodes.PipelineNotFound,
                $"requiredChecks: {matches.Length} pipelines are named '{name}' in project {context.Repo.Project.Name} " +
                $"({string.Join(", ", matches.Select(m => $"{m.Path}\\{m.Name}".Replace("\\\\", "\\", StringComparison.Ordinal)))}). Rename one so the name is unique."),
        };
    }
}
