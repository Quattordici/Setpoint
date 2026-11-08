using System.Text.Json.Nodes;

namespace Setpoint.Adapters.GitHub.Tests.TestData;

/// <summary>
/// Builds a ruleset as <c>GET /repos/{owner}/{repo}/rulesets/{id}</c> returns it: by default the setpoint ruleset,
/// active on the default branch, with no rules and no bypass actors.
/// </summary>
internal sealed class RulesetBuilder
{
    private static readonly Bogus.Faker s_faker = new();

    private readonly JsonArray _rules = [];
    private long _id = s_faker.Random.Long(1, 999_999);
    private string _name = "setpoint";
    private string _enforcement = "active";
    private JsonArray _include = [(JsonNode)"~DEFAULT_BRANCH"];
    private JsonArray? _bypassActors = [];

    public long Id => _id;

    public string Name => _name;

    public RulesetBuilder WithId(long id)
    {
        _id = id;
        return this;
    }

    public RulesetBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public RulesetBuilder Disabled()
    {
        _enforcement = "disabled";
        return this;
    }

    public RulesetBuilder Including(params string[] refs)
    {
        _include = [.. refs.Select(r => (JsonNode)r)];
        return this;
    }

    public RulesetBuilder WithPullRequest(int count = 2, bool dismissStale = true, bool threadResolution = false, string[]? mergeMethods = null, bool codeOwners = false, bool lastPush = false)
    {
        _rules.Add(new JsonObject
        {
            ["type"] = "pull_request",
            ["parameters"] = new JsonObject
            {
                ["required_approving_review_count"] = count,
                ["dismiss_stale_reviews_on_push"] = dismissStale,
                ["require_code_owner_review"] = codeOwners,
                ["require_last_push_approval"] = lastPush,
                ["required_review_thread_resolution"] = threadResolution,
                ["allowed_merge_methods"] = new JsonArray([.. (mergeMethods ?? ["merge", "squash", "rebase"]).Select(m => (JsonNode)m)]),
            },
        });
        return this;
    }

    public RulesetBuilder WithStatusChecks(bool strict, params string[] contexts)
    {
        _rules.Add(new JsonObject
        {
            ["type"] = "required_status_checks",
            ["parameters"] = new JsonObject
            {
                ["strict_required_status_checks_policy"] = strict,
                ["required_status_checks"] = new JsonArray([.. contexts.Select(c => (JsonNode)new JsonObject { ["context"] = c })]),
            },
        });
        return this;
    }

    public RulesetBuilder WithRule(string type)
    {
        _rules.Add(new JsonObject { ["type"] = type });
        return this;
    }

    public RulesetBuilder WithForcePushAndDeleteProtection() => WithRule("non_fast_forward").WithRule("deletion");

    public RulesetBuilder WithBypassActor(string type, long? id, string mode = "always")
    {
        (_bypassActors ??= []).Add(new JsonObject { ["actor_id"] = id, ["actor_type"] = type, ["bypass_mode"] = mode });
        return this;
    }

    /// <summary>GitHub leaves bypass_actors out for a token without write access.</summary>
    public RulesetBuilder WithoutBypassActorsVisible()
    {
        _bypassActors = null;
        return this;
    }

    /// <summary>The summary the ruleset list returns for it.</summary>
    public JsonObject Summary(string sourceType = "Repository", string source = "acme/payments") => new()
    {
        ["id"] = _id,
        ["name"] = _name,
        ["target"] = "branch",
        ["source_type"] = sourceType,
        ["source"] = source,
        ["enforcement"] = _enforcement,
    };

    public JsonObject Build()
    {
        var ruleset = new JsonObject
        {
            ["id"] = _id,
            ["name"] = _name,
            ["target"] = "branch",
            ["source_type"] = "Repository",
            ["source"] = "acme/payments",
            ["enforcement"] = _enforcement,
            ["conditions"] = new JsonObject
            {
                ["ref_name"] = new JsonObject { ["include"] = _include.DeepClone(), ["exclude"] = new JsonArray() },
            },
            ["rules"] = _rules.DeepClone(),
            ["node_id"] = "RRS_" + s_faker.Random.AlphaNumeric(10),
            ["created_at"] = "2026-10-01T09:00:00Z",
            ["updated_at"] = "2026-10-02T09:00:00Z",
            ["current_user_can_bypass"] = "never",
            ["_links"] = new JsonObject { ["self"] = new JsonObject { ["href"] = "https://api.github.com/repos/acme/payments/rulesets/" + _id } },
        };
        if (_bypassActors is not null)
        {
            ruleset["bypass_actors"] = _bypassActors.DeepClone();
        }

        return ruleset;
    }
}
