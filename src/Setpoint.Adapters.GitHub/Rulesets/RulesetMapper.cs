using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Api;

namespace Setpoint.Adapters.GitHub.Rulesets;

/// <summary>The rule values a ruleset carries, and whether its bypass actors were visible to the token.</summary>
/// <param name="Rules">Rule values in the neutral shape, keyed by rule. A rule the ruleset doesn't set is absent.</param>
/// <param name="BypassActorsKnown">False when GitHub left <c>bypass_actors</c> out, which it does for tokens without write access.</param>
internal sealed record RulesetRules(IReadOnlyDictionary<string, JsonNode?> Rules, bool BypassActorsKnown);

/// <summary>
/// Translates between the <c>setpoint</c> ruleset and the neutral model, using the mapping table from the spec.
/// Reading and building are inverse: <see cref="Read"/> of a payload from <see cref="Build"/> gives back the values
/// it was built from, which is how the adapter tells the core what a ruleset will carry once written.
/// </summary>
internal static class RulesetMapper
{
    /// <summary>The name of the one ruleset per repo that Setpoint owns.</summary>
    public const string RulesetName = "setpoint";

    /// <summary>The default branch, as a ruleset condition names it.</summary>
    public const string DefaultBranch = "~DEFAULT_BRANCH";

    public const string MinimumReviewers = "minimumReviewers";
    public const string RequiredChecks = "requiredChecks";
    public const string RequireCommentResolution = "requireCommentResolution";
    public const string AllowedMergeTypes = "allowedMergeTypes";
    public const string ProtectFromForcePushAndDelete = "protectFromForcePushAndDelete";
    public const string PullRequestOptions = "github.pullRequest";
    public const string RequiredChecksOptions = "github.requiredChecks";
    public const string BypassActors = "github.bypassActors";

    /// <summary>Every rule the setpoint ruleset can carry.</summary>
    public static readonly IReadOnlyList<string> Rules =
    [
        MinimumReviewers,
        RequiredChecks,
        RequireCommentResolution,
        AllowedMergeTypes,
        ProtectFromForcePushAndDelete,
        PullRequestOptions,
        RequiredChecksOptions,
        BypassActors,
    ];

    /// <summary>GitHub's merge methods, in the order GitHub lists them.</summary>
    public static readonly IReadOnlyList<string> AllMergeMethods = ["merge", "squash", "rebase"];

    // The native rule types Setpoint writes. Any other rule type on the setpoint ruleset is kept as it is.
    private static readonly HashSet<string> s_managedTypes = new(StringComparer.Ordinal)
    {
        "pull_request",
        "required_status_checks",
        "non_fast_forward",
        "deletion",
    };

    // Fields left out of the fingerprint. bypass_actors and current_user_can_bypass depend on who reads the
    // ruleset, and plan and apply run with different tokens; any real edit still moves updated_at.
    private static readonly string[] s_volatileFields = ["_links", "current_user_can_bypass", "bypass_actors"];

    /// <summary>The ruleset as it is fingerprinted: GitHub's response without the fields that depend on the reader.</summary>
    public static JsonObject Native(JsonObject ruleset)
    {
        var native = (JsonObject)ruleset.DeepClone();
        foreach (string field in s_volatileFields)
        {
            native.Remove(field);
        }

        return native;
    }

    /// <summary>
    /// The rule values a ruleset carries. A ruleset that isn't active, or doesn't target exactly the default branch,
    /// carries none, because none of its rules take effect there the way the policy means.
    /// </summary>
    public static RulesetRules Read(JsonObject ruleset)
    {
        var rules = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        bool bypassKnown = ruleset["bypass_actors"] is JsonArray;
        if (bypassKnown)
        {
            rules[BypassActors] = ReadBypassActors((JsonArray)ruleset["bypass_actors"]!);
        }

        if (!AppliesToDefaultBranch(ruleset))
        {
            return new RulesetRules(rules, bypassKnown);
        }

        foreach (JsonObject rule in (ruleset["rules"] as JsonArray ?? []).OfType<JsonObject>())
        {
            ReadRule(Text(rule["type"]), rule["parameters"] as JsonObject, rules);
        }

        if (rules.Remove("non_fast_forward") | rules.Remove("deletion"))
        {
            // Both halves are one neutral rule; only one of them is a partial protection, read as false.
            bool both = HasType(ruleset, "non_fast_forward") && HasType(ruleset, "deletion");
            rules[ProtectFromForcePushAndDelete] = JsonValue.Create(both);
        }

        return new RulesetRules(rules, bypassKnown);
    }

    /// <summary>
    /// The neutral reading of one active rule from a ruleset Setpoint doesn't own, for the unmanaged summary and the
    /// SP2101 strictness check. A rule type with no neutral meaning is reported under <c>github.&lt;type&gt;</c>.
    /// </summary>
    public static IEnumerable<(string Rule, JsonNode? Value)> ReadUnmanaged(GitHubBranchRule rule)
    {
        var rules = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        ReadRule(rule.Type, rule.Parameters, rules);
        if (rules.Remove("non_fast_forward") | rules.Remove("deletion"))
        {
            rules[ProtectFromForcePushAndDelete] = JsonValue.Create(true);
        }

        // Only a restriction is worth reporting; a pull request rule that allows every method allows every method.
        if (rules.TryGetValue(AllowedMergeTypes, out JsonNode? methods) && methods is JsonArray { Count: 3 })
        {
            rules.Remove(AllowedMergeTypes);
        }

        rules.Remove(PullRequestOptions);
        rules.Remove(RequiredChecksOptions);
        if (rules.Count == 0)
        {
            rules[$"github.{rule.Type}"] = JsonValue.Create(true);
        }

        return rules.Select(r => (r.Key, r.Value));
    }

    /// <summary>
    /// The full ruleset payload for these rule values, or null when they leave the ruleset with no rule at all, in
    /// which case it should not exist. A value that is absent or null is not set. Rule types Setpoint doesn't manage
    /// are kept from <paramref name="live"/>. <c>bypass_actors</c> is sent only when <paramref name="values"/> has
    /// it, so a ruleset whose bypass actors the token couldn't see keeps them.
    /// </summary>
    public static JsonObject? Build(IReadOnlyDictionary<string, JsonNode?> values, JsonObject? live)
    {
        JsonNode? Value(string rule) => values.GetValueOrDefault(rule);

        var rules = new JsonArray();
        JsonNode? reviewers = Value(MinimumReviewers);
        bool commentResolution = Value(RequireCommentResolution) is JsonValue resolution && resolution.TryGetValue(out bool required) && required;
        JsonArray? mergeMethods = Value(AllowedMergeTypes) as JsonArray;
        JsonNode? pullRequestOptions = Value(PullRequestOptions);
        bool codeOwners = Flag(pullRequestOptions, "requireCodeOwnerReview");
        bool lastPush = Flag(pullRequestOptions, "requireLastPushApproval");

        // Any pull_request rule requires a pull request, so it is only written when something asks for it.
        if (reviewers is not null || commentResolution || mergeMethods is not null || codeOwners || lastPush)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "pull_request",
                ["parameters"] = new JsonObject
                {
                    ["required_approving_review_count"] = reviewers?["count"]?.GetValue<int>() ?? 0,
                    ["dismiss_stale_reviews_on_push"] = Flag(reviewers, "resetOnPush"),
                    ["require_code_owner_review"] = codeOwners,
                    ["require_last_push_approval"] = lastPush,
                    ["required_review_thread_resolution"] = commentResolution,
                    ["allowed_merge_methods"] = new JsonArray([.. (mergeMethods is null ? AllMergeMethods : OrderedMethods(mergeMethods)).Select(m => (JsonNode)m)]),
                },
            });
        }

        if (Value(RequiredChecks) is JsonArray { Count: > 0 } checks)
        {
            JsonNode? options = Value(RequiredChecksOptions);
            JsonObject? integrations = options?["integrationIds"] as JsonObject;
            var statusChecks = new JsonArray();
            foreach (string name in checks.Select(c => Text(c?["name"])).Order(StringComparer.Ordinal))
            {
                var check = new JsonObject { ["context"] = name };
                if (integrations?[name] is JsonValue id)
                {
                    check["integration_id"] = id.GetValue<long>();
                }

                statusChecks.Add(check);
            }

            rules.Add(new JsonObject
            {
                ["type"] = "required_status_checks",
                ["parameters"] = new JsonObject
                {
                    ["strict_required_status_checks_policy"] = Flag(options, "strict"),
                    ["required_status_checks"] = statusChecks,
                },
            });
        }

        if (Value(ProtectFromForcePushAndDelete) is JsonValue protect && protect.TryGetValue(out bool isProtected) && isProtected)
        {
            rules.Add(new JsonObject { ["type"] = "non_fast_forward" });
            rules.Add(new JsonObject { ["type"] = "deletion" });
        }

        foreach (JsonObject kept in (live?["rules"] as JsonArray ?? []).OfType<JsonObject>().Where(r => !s_managedTypes.Contains(Text(r["type"]))))
        {
            rules.Add(kept.DeepClone());
        }

        if (rules.Count == 0)
        {
            return null;
        }

        var payload = new JsonObject
        {
            ["name"] = RulesetName,
            ["target"] = "branch",
            ["enforcement"] = "active",
            ["conditions"] = new JsonObject
            {
                ["ref_name"] = new JsonObject { ["include"] = new JsonArray(DefaultBranch), ["exclude"] = new JsonArray() },
            },
            ["rules"] = rules,
        };

        if (values.TryGetValue(BypassActors, out JsonNode? actors))
        {
            payload["bypass_actors"] = WriteBypassActors(actors as JsonArray ?? []);
        }

        return payload;
    }

    private static void ReadRule(string type, JsonObject? parameters, Dictionary<string, JsonNode?> rules)
    {
        switch (type)
        {
            case "pull_request":
                int count = Int(parameters?["required_approving_review_count"]);
                bool reset = Bool(parameters?["dismiss_stale_reviews_on_push"]);
                if (count > 0 || reset)
                {
                    rules[MinimumReviewers] = new JsonObject { ["count"] = count, ["resetOnPush"] = reset };
                }

                if (Bool(parameters?["required_review_thread_resolution"]))
                {
                    rules[RequireCommentResolution] = JsonValue.Create(true);
                }

                rules[AllowedMergeTypes] = new JsonArray([
                    .. (parameters?["allowed_merge_methods"] is JsonArray methods ? OrderedMethods(methods) : AllMergeMethods).Select(m => (JsonNode)m)]);
                rules[PullRequestOptions] = new JsonObject
                {
                    ["requireCodeOwnerReview"] = Bool(parameters?["require_code_owner_review"]),
                    ["requireLastPushApproval"] = Bool(parameters?["require_last_push_approval"]),
                };
                break;

            case "required_status_checks":
                JsonObject[] checks = [.. (parameters?["required_status_checks"] as JsonArray ?? []).OfType<JsonObject>()];
                if (checks.Length == 0)
                {
                    break;
                }

                rules[RequiredChecks] = new JsonArray([.. checks.Select(c => (JsonNode)new JsonObject { ["name"] = Text(c["context"]) })]);
                var options = new JsonObject { ["strict"] = Bool(parameters?["strict_required_status_checks_policy"]) };
                JsonObject[] pinned = [.. checks.Where(c => c["integration_id"] is JsonValue)];
                if (pinned.Length > 0)
                {
                    options["integrationIds"] = new JsonObject(pinned.Select(c =>
                        KeyValuePair.Create(Text(c["context"]), (JsonNode?)JsonValue.Create(c["integration_id"]!.GetValue<long>()))));
                }

                rules[RequiredChecksOptions] = options;
                break;

            case "non_fast_forward" or "deletion":
                rules[type] = JsonValue.Create(true);
                break;
        }
    }

    private static bool AppliesToDefaultBranch(JsonObject ruleset)
    {
        if (Text(ruleset["enforcement"]) != "active" || (ruleset["target"] is JsonValue && Text(ruleset["target"]) != "branch"))
        {
            return false;
        }

        JsonNode? refName = ruleset["conditions"]?["ref_name"];
        string[] include = [.. (refName?["include"] as JsonArray ?? []).Select(Text)];
        bool excludesNothing = refName?["exclude"] is not JsonArray { Count: > 0 };
        return excludesNothing && include.Any(r => r is DefaultBranch or "~ALL");
    }

    private static bool HasType(JsonObject ruleset, string type) =>
        (ruleset["rules"] as JsonArray ?? []).OfType<JsonObject>().Any(r => Text(r["type"]) == type);

    // The API's actor types are PascalCase (OrganizationAdmin) and its modes snake_case (pull_request); the policy
    // spells both in camelCase. Organization admins have no actor id that means anything, so it is dropped.
    private static JsonArray ReadBypassActors(JsonArray actors) => new([
        .. actors.OfType<JsonObject>().Select(actor =>
        {
            string type = Text(actor["actor_type"]);
            var normalised = new JsonObject { ["actorType"] = type.Length > 0 ? char.ToLowerInvariant(type[0]) + type[1..] : type };
            if (type != "OrganizationAdmin" && actor["actor_id"] is JsonValue id)
            {
                normalised["actorId"] = id.GetValue<long>();
            }

            normalised["bypassMode"] = Text(actor["bypass_mode"]) switch
            {
                "pull_request" => "pullRequest",
                "" => "always",
                string mode => mode,
            };
            return (JsonNode)normalised;
        })]);

    private static JsonArray WriteBypassActors(JsonArray actors) => new([
        .. actors.OfType<JsonObject>().Select(actor =>
        {
            string type = Text(actor["actorType"]);
            var native = new JsonObject();
            if (actor["actorId"] is JsonValue id)
            {
                native["actor_id"] = id.GetValue<long>();
            }
            else if (type == "organizationAdmin")
            {
                // GitHub wants an id here too; organization admin is the one actor where it is fixed.
                native["actor_id"] = 1;
            }

            native["actor_type"] = type.Length > 0 ? char.ToUpperInvariant(type[0]) + type[1..] : type;
            native["bypass_mode"] = Text(actor["bypassMode"]) switch
            {
                "pullRequest" => "pull_request",
                "" => "always",
                string mode => mode,
            };
            return (JsonNode)native;
        })]);

    private static IEnumerable<string> OrderedMethods(JsonArray methods)
    {
        var present = new HashSet<string>(methods.Select(Text), StringComparer.Ordinal);
        return AllMergeMethods.Where(present.Contains).Concat(present.Except(AllMergeMethods).Order(StringComparer.Ordinal));
    }

    private static bool Flag(JsonNode? node, string name) => node?[name] is JsonValue v && v.TryGetValue(out bool flag) && flag;

    private static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool flag) && flag;

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue(out int number) ? number : 0;

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? text) ? text : "";
}
