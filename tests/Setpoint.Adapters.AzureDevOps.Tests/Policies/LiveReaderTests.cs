using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Policies;
using Setpoint.Adapters.AzureDevOps.Tests.TestData;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.AzureDevOps.Tests.Policies;

/// <summary>
/// Tests for <see cref="LiveReader"/>: which policy configurations Setpoint owns on a repo's default branch, how
/// each managed type reads into neutral and provider rules, what counts as unmanaged or ambiguous, and how the
/// default branch's Force push entries read.
/// </summary>
public sealed class LiveReaderTests
{
    private readonly RepoContextBuilder _context = new();

    /// <summary>
    /// Given a minimum reviewers policy scoped to exactly this repo's default branch,
    /// when the repo is read,
    /// then it is the one live object, carrying the neutral rule and the provider options.
    /// </summary>
    [Fact(DisplayName = "👍 Reads a minimum reviewers policy into the neutral rule and its options")]
    public void Read_MinimumReviewers_ReadsRuleAndOptions()
    {
        // Arrange
        JsonObject config = new PolicyConfigBuilder(_context.RepoId).WithId(17).With("creatorVoteCounts", true).Build();

        // Act
        LiveSnapshot live = Read([config]);

        // Assert
        LiveObject policy = live.Objects.Where(o => o.Key.StartsWith("ado:policy/", StringComparison.Ordinal)).ShouldHaveSingleItem();
        policy.Key.ShouldBe("ado:policy/17");
        policy.MatchKey.ShouldBe("ado:minimumReviewers/refs/heads/main");
        policy.Title.ShouldBe("Minimum number of reviewers #17");
        Json(policy.Rules["minimumReviewers"]).ShouldBe("""{"resetOnPush":true,"count":2}""");
        Json(policy.Rules["azuredevops.minimumReviewers"]).ShouldBe("""{"allowDownvotes":false,"blockLastPusherVote":false,"creatorVoteCounts":true}""");
    }

    /// <summary>
    /// Given an owned policy that is disabled, and one that is optional,
    /// when the repo is read,
    /// then each is a live object that carries no rules, since neither holds a pull request back.
    /// </summary>
    [Theory(DisplayName = "🖐️ Reads a disabled or optional policy as carrying no rules")]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_DisabledOrOptional_CarriesNoRules(bool disabled)
    {
        // Arrange
        var builder = new PolicyConfigBuilder(_context.RepoId);
        JsonObject config = (disabled ? builder.Disabled() : builder.Optional()).Build();

        // Act
        LiveSnapshot live = Read([config]);

        // Assert
        live.Objects.Where(o => o.Key.StartsWith("ado:policy/", StringComparison.Ordinal)).ShouldHaveSingleItem().Rules.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a build validation policy for a known pipeline and one for a pipeline that no longer exists,
    /// when the repo is read,
    /// then each carries its pipeline by name, the missing one by its id, with its options.
    /// </summary>
    [Fact(DisplayName = "👍 Reads build validation as the pipeline's name, with its options")]
    public void Read_BuildValidation_ReadsPipelineName()
    {
        // Arrange
        JsonObject known = new PolicyConfigBuilder(_context.RepoId).BuildValidation(11, validDuration: 0).With("filenamePatterns", new JsonArray("/src/*")).Build();
        JsonObject missing = new PolicyConfigBuilder(_context.RepoId).BuildValidation(99).Build();

        // Act
        LiveSnapshot live = Read([known, missing]);

        // Assert
        LiveObject first = live.Objects.Single(o => o.MatchKey == "ado:build/refs/heads/main/11");
        Json(first.Rules["requiredChecks"]).ShouldBe("""[{"name":"payments-ci"}]""");
        Json(first.Rules["azuredevops.requiredChecks"]).ShouldBe("""{"validDuration":0,"pathFilters":["/src/*"]}""");
        Json(live.Objects.Single(o => o.MatchKey == "ado:build/refs/heads/main/99").Rules["requiredChecks"]).ShouldBe("""[{"name":"#99"}]""");
    }

    /// <summary>
    /// Given a status check, comment requirements and merge strategy policy on the branch,
    /// when the repo is read,
    /// then each carries its rule: the status check as a provider option, the others as neutral rules.
    /// </summary>
    [Fact(DisplayName = "👍 Reads status checks, comment requirements and merge strategy")]
    public void Read_OtherManagedTypes_ReadTheirRules()
    {
        // Arrange
        JsonObject[] configs =
        [
            new PolicyConfigBuilder(_context.RepoId).Status("sonar", "quality").Build(),
            new PolicyConfigBuilder(_context.RepoId).CommentRequirements().Build(),
            new PolicyConfigBuilder(_context.RepoId).MergeStrategy(noFastForward: false, squash: true, rebase: true, rebaseMerge: true).Build(),
        ];

        // Act
        LiveSnapshot live = Read(configs);

        // Assert
        Json(live.Objects.Single(o => o.MatchKey == "ado:status/refs/heads/main/quality/sonar").Rules["azuredevops.requiredChecks"])
            .ShouldBe("""{"statusChecks":[{"name":"sonar","genre":"quality"}]}""");
        Json(live.Objects.Single(o => o.MatchKey == "ado:commentRequirements/refs/heads/main").Rules["requireCommentResolution"]).ShouldBe("true");
        LiveObject merge = live.Objects.Single(o => o.MatchKey == "ado:mergeStrategy/refs/heads/main");
        Json(merge.Rules["allowedMergeTypes"]).ShouldBe("""["squash","rebase"]""");
        Json(merge.Rules["azuredevops.allowedMergeTypes"]).ShouldBe("""{"allowRebaseMerge":true}""");
    }

    /// <summary>
    /// Given a merge strategy policy from before the four allow flags, with only useSquashMerge,
    /// when the repo is read,
    /// then true reads as squash only and false as a merge commit only.
    /// </summary>
    [Theory(DisplayName = "🖐️ Reads the legacy useSquashMerge setting")]
    [InlineData(true, """["squash"]""")]
    [InlineData(false, """["merge"]""")]
    public void Read_LegacyMergeStrategy_ReadsUseSquashMerge(bool useSquashMerge, string expected)
    {
        // Arrange
        JsonObject config = new PolicyConfigBuilder(_context.RepoId)
            .OfType(AzureDevOpsPolicyTypes.MergeStrategy, "Require a merge strategy")
            .With("useSquashMerge", useSquashMerge)
            .Build();

        // Act
        LiveSnapshot live = Read([config]);

        // Assert
        Json(live.Objects.Single(o => o.MatchKey == "ado:mergeStrategy/refs/heads/main").Rules["allowedMergeTypes"]).ShouldBe(expected);
    }

    /// <summary>
    /// Given managed policy types scoped project-wide, to a branch prefix, or shared with another repo,
    /// when the repo is read,
    /// then none is owned, and each is reported as unmanaged under its neutral rule with where it comes from.
    /// </summary>
    [Fact(DisplayName = "👍 Reports policies with a wider scope as unmanaged")]
    public void Read_WiderScopes_AreUnmanaged()
    {
        // Arrange
        JsonObject[] configs =
        [
            new PolicyConfigBuilder(_context.RepoId).WithId(1).ProjectWide().Build(),
            new PolicyConfigBuilder(_context.RepoId).WithId(2).CommentRequirements().ScopedTo(_context.RepoId, "refs/heads/ma", "Prefix").Build(),
            new PolicyConfigBuilder(_context.RepoId).WithId(3).MergeStrategy(true, false, false).AlsoScopedTo(Guid.NewGuid().ToString(), "refs/heads/main").Build(),
        ];

        // Act
        LiveSnapshot live = Read(configs);

        // Assert
        live.Objects.ShouldNotContain(o => o.Key.StartsWith("ado:policy/", StringComparison.Ordinal));
        live.Unmanaged.Select(u => $"{u.Rule} | {u.Source}").ShouldBe(
        [
            "minimumReviewers | policy #1 'Minimum number of reviewers' (project-wide)",
            "requireCommentResolution | policy #2 'Comment requirements' (branches refs/heads/ma*)",
            "allowedMergeTypes | policy #3 'Require a merge strategy' (shared by 2 scopes)",
        ]);
    }

    /// <summary>
    /// Given policies on another branch and on another repo,
    /// when the repo is read,
    /// then they are left out entirely, because they don't apply to its default branch.
    /// </summary>
    [Fact(DisplayName = "🖐️ Leaves out policies on other branches and other repos")]
    public void Read_OtherBranchOrRepo_LeftOut()
    {
        // Arrange
        JsonObject[] configs =
        [
            new PolicyConfigBuilder(_context.RepoId, "refs/heads/release").Build(),
            new PolicyConfigBuilder(Guid.NewGuid().ToString()).Build(),
        ];

        // Act
        LiveSnapshot live = Read(configs);

        // Assert
        live.Objects.ShouldNotContain(o => o.Key.StartsWith("ado:policy/", StringComparison.Ordinal));
        live.Unmanaged.ShouldNotContain(u => u.Source.StartsWith("policy", StringComparison.Ordinal));
    }

    /// <summary>
    /// Given a policy type Setpoint doesn't manage on the branch,
    /// when the repo is read,
    /// then it is reported as an unmanaged provider setting named after its type.
    /// </summary>
    [Fact(DisplayName = "🖐️ Reports policy types Setpoint doesn't manage as unmanaged")]
    public void Read_OtherType_IsUnmanaged()
    {
        // Arrange
        JsonObject config = new PolicyConfigBuilder(_context.RepoId).WithId(5).OfType("40e92b44-2fe1-4dd6-b3d8-74a9c21d0c6e", "Work item linking").Build();

        // Act
        LiveSnapshot live = Read([config]);

        // Assert
        UnmanagedSetting setting = live.Unmanaged.ShouldHaveSingleItem();
        setting.Rule.ShouldBe("azuredevops.workItemLinking");
        setting.Source.ShouldBe("policy #5 'Work item linking' (this branch)");
    }

    /// <summary>
    /// Given two minimum reviewers policies both scoped exactly to the default branch,
    /// when the repo is read,
    /// then both rules they govern are ambiguous, naming both policies, and neither is a live object.
    /// </summary>
    [Fact(DisplayName = "👎 Reports two matching policies as ambiguous and owns neither")]
    public void Read_TwoMatchingPolicies_AreAmbiguous()
    {
        // Arrange
        JsonObject[] configs = [new PolicyConfigBuilder(_context.RepoId).WithId(31).Build(), new PolicyConfigBuilder(_context.RepoId).WithId(32).Build()];

        // Act
        LiveSnapshot live = Read(configs);

        // Assert
        live.Ambiguous.Select(a => a.Rule).ShouldBe(["minimumReviewers", "azuredevops.minimumReviewers"]);
        live.Ambiguous[0].ObjectKeys.ShouldBe(["ado:policy/31", "ado:policy/32"]);
        live.Ambiguous[0].Detail.ShouldBe("type + branch main");
        live.Objects.ShouldNotContain(o => o.Key.StartsWith("ado:policy/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Given the default branch's ACL with a Force push deny for Project Valid Users,
    /// when the repo is read,
    /// then the entry is a live object carrying protectFromForcePushAndDelete: true.
    /// </summary>
    [Fact(DisplayName = "👍 Reads the Force push deny for Project Valid Users as the protection")]
    public void Read_ValidUsersDeny_IsProtection()
    {
        // Arrange
        JsonObject acl = Acl((_context.ValidUsers!, 0, 8 | 4));

        // Act
        LiveSnapshot live = Read([], acl);

        // Assert
        LiveObject entry = live.Objects.ShouldHaveSingleItem();
        entry.MatchKey.ShouldBe("ado:forcePush/refs/heads/main");
        entry.Title.ShouldBe("Force push permission on main");
        Json(entry.Rules["protectFromForcePushAndDelete"]).ShouldBe("true");
        live.UnknownRules.ShouldBeEmpty();
    }

    /// <summary>
    /// Given another group's Force push deny and a user's explicit Allow on the default branch,
    /// when the repo is read,
    /// then the deny is unmanaged and the Allow raises SP2103 naming the user.
    /// </summary>
    [Fact(DisplayName = "👎 Warns, naming the identity, when someone is explicitly allowed to force push")]
    public void Read_ExplicitAllow_WarnsNamingIdentity()
    {
        // Arrange
        JsonObject acl = Acl(("group-readers", 0, 8), ("user-creator", 8 | 2, 0));
        var names = new Dictionary<string, string> { ["group-readers"] = "[Payments]\\Readers", ["user-creator"] = "Mattias" };

        // Act
        LiveSnapshot live = Read([], acl, d => names[d]);

        // Assert
        live.Unmanaged.ShouldHaveSingleItem().Source.ShouldBe("Force push deny for [Payments]\\Readers");
        RuleWarning warning = live.Warnings.ShouldNotBeNull().ShouldHaveSingleItem();
        warning.Rule.ShouldBe("protectFromForcePushAndDelete");
        warning.Diagnostic.Code.ShouldBe(DiagnosticCodes.ForcePushAllowed);
        warning.Diagnostic.Message.ShouldContain("Mattias has an explicit Allow for Force push on main");
    }

    /// <summary>
    /// Given an ACL the token can't read, or a Project Valid Users group it can't look up,
    /// when the repo is read,
    /// then the protection is unknown rather than absent.
    /// </summary>
    [Theory(DisplayName = "🖐️ Reports the protection as unknown when the token can't read security")]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_SecurityUnreadable_IsUnknown(bool aclReadable)
    {
        // Arrange
        if (aclReadable)
        {
            _context.WithoutValidUsers();
        }

        // Act
        LiveSnapshot live = LiveReader.Read(_context.Build(), [], aclReadable ? [] : null, d => d);

        // Assert
        live.UnknownRules.ShouldBe(["protectFromForcePushAndDelete"]);
    }

    /// <summary>
    /// Given branch names with one and with several parts,
    /// when their security token is made,
    /// then each part is hex-encoded as UTF-16LE under refs/heads.
    /// </summary>
    [Theory(DisplayName = "🖐️ Encodes the branch token as UTF-16LE hex per name part")]
    [InlineData("main", "refs/heads/6d00610069006e00")]
    [InlineData("release/v1", "refs/heads/720065006c006500610073006500/76003100")]
    public void BranchToken_Branch_IsEncoded(string branch, string expectedSuffix)
    {
        // Act
        string token = ForcePushAcl.BranchToken("p1", "r1", branch);

        // Assert
        token.ShouldBe($"repoV2/p1/r1/{expectedSuffix}");
    }

    private LiveSnapshot Read(IReadOnlyList<JsonObject> configs, JsonObject? acl = null, Func<string, string>? names = null) =>
        LiveReader.Read(_context.Build(), configs, acl ?? [], names ?? (d => d));

    private static JsonObject Acl(params (string Descriptor, int Allow, int Deny)[] entries)
    {
        var aces = new JsonObject();
        foreach ((string descriptor, int allow, int deny) in entries)
        {
            aces[descriptor] = new JsonObject { ["descriptor"] = descriptor, ["allow"] = allow, ["deny"] = deny };
        }

        return new JsonObject { ["inheritPermissions"] = true, ["token"] = "t", ["acesDictionary"] = aces };
    }

    private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";
}
