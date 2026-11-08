using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Api;
using Setpoint.Adapters.GitHub.Rulesets;
using Setpoint.Adapters.GitHub.Tests.TestData;

namespace Setpoint.Adapters.GitHub.Tests.Rulesets;

/// <summary>
/// Tests for <see cref="RulesetMapper"/>: reading the setpoint ruleset into neutral rules, building its payload,
/// the round trip between the two, and reading other rulesets as unmanaged settings.
/// </summary>
public sealed class RulesetMapperTests
{
    /// <summary>
    /// Given a setpoint ruleset with every managed rule,
    /// when it is read,
    /// then each rule has its neutral value, as in the spec's mapping table.
    /// </summary>
    [Fact(DisplayName = "👍 Reads every managed rule from the setpoint ruleset")]
    public void Read_FullRuleset_MapsEveryRule()
    {
        // Arrange
        JsonObject ruleset = new RulesetBuilder()
            .WithPullRequest(count: 2, dismissStale: true, threadResolution: true, mergeMethods: ["squash", "merge"], codeOwners: true)
            .WithStatusChecks(true, "build", "lint")
            .WithForcePushAndDeleteProtection()
            .WithBypassActor("Team", 42, "pull_request")
            .WithBypassActor("OrganizationAdmin", 1)
            .Build();

        // Act
        RulesetRules read = RulesetMapper.Read(ruleset);

        // Assert
        read.BypassActorsKnown.ShouldBeTrue();
        Json(read.Rules, RulesetMapper.MinimumReviewers).ShouldBe("""{"count":2,"resetOnPush":true}""");
        Json(read.Rules, RulesetMapper.RequireCommentResolution).ShouldBe("true");
        Json(read.Rules, RulesetMapper.AllowedMergeTypes).ShouldBe("""["merge","squash"]""");
        Json(read.Rules, RulesetMapper.RequiredChecks).ShouldBe("""[{"name":"build"},{"name":"lint"}]""");
        Json(read.Rules, RulesetMapper.ProtectFromForcePushAndDelete).ShouldBe("true");
        Json(read.Rules, RulesetMapper.PullRequestOptions).ShouldBe("""{"requireCodeOwnerReview":true,"requireLastPushApproval":false}""");
        Json(read.Rules, RulesetMapper.RequiredChecksOptions).ShouldBe("""{"strict":true}""");
        Json(read.Rules, RulesetMapper.BypassActors).ShouldBe(
            """[{"actorType":"team","actorId":42,"bypassMode":"pullRequest"},{"actorType":"organizationAdmin","bypassMode":"always"}]""");
    }

    /// <summary>
    /// Given a pull request rule that needs no approvals and resets nothing,
    /// when it is read,
    /// then minimumReviewers and requireCommentResolution are absent, not zero or false.
    /// </summary>
    [Fact(DisplayName = "🖐️ Reads a pull request rule with no approvals as no minimumReviewers")]
    public void Read_ZeroApprovals_LeavesMinimumReviewersAbsent()
    {
        // Arrange
        JsonObject ruleset = new RulesetBuilder().WithPullRequest(count: 0, dismissStale: false).Build();

        // Act
        RulesetRules read = RulesetMapper.Read(ruleset);

        // Assert
        read.Rules.ShouldNotContainKey(RulesetMapper.MinimumReviewers);
        read.Rules.ShouldNotContainKey(RulesetMapper.RequireCommentResolution);
        read.Rules.ShouldContainKey(RulesetMapper.AllowedMergeTypes);
    }

    /// <summary>
    /// Given a ruleset that blocks force pushes but not deletion,
    /// when it is read,
    /// then the protection reads as false, so a policy that wants it sees a change.
    /// </summary>
    [Fact(DisplayName = "🖐️ Reads half of the force-push and delete protection as false")]
    public void Read_OnlyNonFastForward_ReadsProtectionAsFalse()
    {
        // Arrange
        JsonObject ruleset = new RulesetBuilder().WithRule("non_fast_forward").Build();

        // Act
        RulesetRules read = RulesetMapper.Read(ruleset);

        // Assert
        Json(read.Rules, RulesetMapper.ProtectFromForcePushAndDelete).ShouldBe("false");
    }

    /// <summary>
    /// Given a setpoint ruleset that is disabled, or targets other branches,
    /// when it is read,
    /// then it carries no rules, because none of them take effect on the default branch.
    /// </summary>
    [Theory(DisplayName = "👎 Reads a ruleset that doesn't apply to the default branch as carrying no rules")]
    [InlineData(true, "~DEFAULT_BRANCH")]
    [InlineData(false, "refs/heads/release/*")]
    public void Read_NotApplyingToDefaultBranch_CarriesNoRules(bool disabled, string include)
    {
        // Arrange
        RulesetBuilder builder = new RulesetBuilder().Including(include).WithPullRequest().WithForcePushAndDeleteProtection();
        if (disabled)
        {
            builder.Disabled();
        }

        // Act
        RulesetRules read = RulesetMapper.Read(builder.Build());

        // Assert
        read.Rules.Keys.ShouldBe([RulesetMapper.BypassActors]);
    }

    /// <summary>
    /// Given a ruleset read with a token that can't see bypass actors,
    /// when it is read,
    /// then bypass actors are reported as not known rather than as none.
    /// </summary>
    [Fact(DisplayName = "👎 Reports hidden bypass actors as unknown")]
    public void Read_BypassActorsHidden_ReportsUnknown()
    {
        // Arrange
        JsonObject ruleset = new RulesetBuilder().WithPullRequest().WithoutBypassActorsVisible().Build();

        // Act
        RulesetRules read = RulesetMapper.Read(ruleset);

        // Assert
        read.BypassActorsKnown.ShouldBeFalse();
        read.Rules.ShouldNotContainKey(RulesetMapper.BypassActors);
    }

    /// <summary>
    /// Given a set of neutral rule values,
    /// when a payload is built from them and the payload is read back,
    /// then the same values come back, so a written ruleset is in sync on the next plan.
    /// </summary>
    [Theory(DisplayName = "👍 Reads back the values a payload was built from")]
    [MemberData(nameof(RoundTrips))]
    public void Build_ThenRead_GivesBackTheValues(Dictionary<string, string> values)
    {
        // Arrange
        Dictionary<string, JsonNode?> rules = values.ToDictionary(v => v.Key, v => JsonNode.Parse(v.Value));

        // Act
        JsonObject payload = RulesetMapper.Build(rules, live: null).ShouldNotBeNull();
        RulesetRules read = RulesetMapper.Read(payload);

        // Assert
        foreach ((string rule, JsonNode? value) in rules)
        {
            JsonNode.DeepEquals(read.Rules.GetValueOrDefault(rule), value).ShouldBeTrue($"{rule} came back as {read.Rules.GetValueOrDefault(rule)?.ToJsonString()}");
        }
    }

    public static TheoryData<Dictionary<string, string>> RoundTrips() => new()
    {
        new Dictionary<string, string>
        {
            [RulesetMapper.MinimumReviewers] = """{"count":2,"resetOnPush":true}""",
            [RulesetMapper.AllowedMergeTypes] = """["merge","squash"]""",
            [RulesetMapper.RequireCommentResolution] = "true",
            [RulesetMapper.PullRequestOptions] = """{"requireCodeOwnerReview":false,"requireLastPushApproval":true}""",
        },
        new Dictionary<string, string>
        {
            [RulesetMapper.RequiredChecks] = """[{"name":"build"},{"name":"e2e"}]""",
            [RulesetMapper.RequiredChecksOptions] = """{"integrationIds":{"build":15368},"strict":true}""",
            [RulesetMapper.ProtectFromForcePushAndDelete] = "true",
        },
        new Dictionary<string, string>
        {
            [RulesetMapper.ProtectFromForcePushAndDelete] = "true",
            [RulesetMapper.BypassActors] = """[{"actorType":"integration","actorId":15368,"bypassMode":"always"},{"actorType":"organizationAdmin","bypassMode":"pullRequest"}]""",
        },
    };

    /// <summary>
    /// Given values that ask for no rule at all,
    /// when a payload is built,
    /// then there is none, because the ruleset should not exist.
    /// </summary>
    [Fact(DisplayName = "🖐️ Builds no payload when nothing is asked for")]
    public void Build_NothingAskedFor_ReturnsNull()
    {
        // Arrange
        var values = new Dictionary<string, JsonNode?>
        {
            [RulesetMapper.MinimumReviewers] = null,
            [RulesetMapper.RequiredChecks] = new JsonArray(),
            [RulesetMapper.ProtectFromForcePushAndDelete] = null,
            [RulesetMapper.PullRequestOptions] = JsonNode.Parse("""{"requireCodeOwnerReview":false,"requireLastPushApproval":false}"""),
        };

        // Act
        JsonObject? payload = RulesetMapper.Build(values, live: null);

        // Assert
        payload.ShouldBeNull();
    }

    /// <summary>
    /// Given a live setpoint ruleset with a rule type Setpoint doesn't manage,
    /// when a payload is built,
    /// then the payload keeps that rule, targets the default branch and is active.
    /// </summary>
    [Fact(DisplayName = "👍 Keeps rule types Setpoint doesn't manage")]
    public void Build_LiveHasOtherRuleType_KeepsIt()
    {
        // Arrange
        JsonObject live = new RulesetBuilder().WithRule("required_signatures").WithRule("non_fast_forward").Build();
        var values = new Dictionary<string, JsonNode?> { [RulesetMapper.ProtectFromForcePushAndDelete] = true };

        // Act
        JsonObject payload = RulesetMapper.Build(values, live).ShouldNotBeNull();

        // Assert
        payload["rules"]!.AsArray().Select(r => r!["type"]!.GetValue<string>()).ShouldBe(["non_fast_forward", "deletion", "required_signatures"]);
        payload["enforcement"]!.GetValue<string>().ShouldBe("active");
        payload["conditions"]!.ToJsonString().ShouldBe("""{"ref_name":{"include":["~DEFAULT_BRANCH"],"exclude":[]}}""");
    }

    /// <summary>
    /// Given values without bypass actors, because the token couldn't see them,
    /// when a payload is built,
    /// then it leaves bypass_actors out, so writing it keeps the ones GitHub has.
    /// </summary>
    [Fact(DisplayName = "🖐️ Leaves bypass actors out of the payload when they aren't known")]
    public void Build_NoBypassActors_LeavesThemOut()
    {
        // Arrange
        var values = new Dictionary<string, JsonNode?> { [RulesetMapper.ProtectFromForcePushAndDelete] = true };

        // Act
        JsonObject payload = RulesetMapper.Build(values, live: null).ShouldNotBeNull();

        // Assert
        payload.ContainsKey("bypass_actors").ShouldBeFalse();
    }

    /// <summary>
    /// Given an active rule from another ruleset,
    /// when it is read as unmanaged,
    /// then it maps to the neutral rule it bears on, or to its own name when it has no neutral meaning.
    /// </summary>
    [Theory(DisplayName = "👍 Reads other rulesets' rules as neutral unmanaged settings")]
    [InlineData("""{"type":"pull_request","parameters":{"required_approving_review_count":3,"dismiss_stale_reviews_on_push":false,"allowed_merge_methods":["merge","squash","rebase"]}}""", "minimumReviewers", """{"count":3,"resetOnPush":false}""")]
    [InlineData("""{"type":"pull_request","parameters":{"required_approving_review_count":0,"allowed_merge_methods":["squash"]}}""", "allowedMergeTypes", """["squash"]""")]
    [InlineData("""{"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"sec-scan"}]}}""", "requiredChecks", """[{"name":"sec-scan"}]""")]
    [InlineData("""{"type":"deletion"}""", "protectFromForcePushAndDelete", "true")]
    [InlineData("""{"type":"required_signatures"}""", "github.required_signatures", "true")]
    public void ReadUnmanaged_OtherRule_MapsToNeutralRule(string json, string expectedRule, string expectedValue)
    {
        // Arrange
        GitHubBranchRule rule = System.Text.Json.JsonSerializer.Deserialize(json, GitHubApiJsonContext.Default.GitHubBranchRule)!;

        // Act
        (string Rule, JsonNode? Value)[] read = [.. RulesetMapper.ReadUnmanaged(rule)];

        // Assert
        read.ShouldHaveSingleItem().Rule.ShouldBe(expectedRule);
        read[0].Value!.ToJsonString().ShouldBe(expectedValue);
    }

    private static string? Json(IReadOnlyDictionary<string, JsonNode?> rules, string rule) => rules.GetValueOrDefault(rule)?.ToJsonString();
}
