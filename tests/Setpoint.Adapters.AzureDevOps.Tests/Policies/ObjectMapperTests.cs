using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Policies;
using Setpoint.Adapters.AzureDevOps.Tests.TestData;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Merging;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.AzureDevOps.Tests.Policies;

/// <summary>
/// Tests for <see cref="ObjectMapper"/>, through the core diff: the payloads it builds for each managed policy type
/// and the Force push entry, that a live object written from its own payload reads back in sync, that settings the
/// policy doesn't manage are kept, and how required checks resolve to pipelines.
/// </summary>
public sealed class ObjectMapperTests
{
    private static readonly LayerRef s_baseline = new(LayerKind.Baseline, null, SourceLocation.StartOf("baseline.yml"));

    private readonly RepoContextBuilder _context = new();

    /// <summary>
    /// Given a policy declaring every neutral rule and a repo with nothing live,
    /// when it is mapped and diffed,
    /// then each becomes a create: one policy per type, one build validation per pipeline, and the Force push entry.
    /// </summary>
    [Fact(DisplayName = "👍 Creates one object per managed policy, per pipeline, and the Force push entry")]
    public void Map_NothingLive_CreatesEverything()
    {
        // Arrange
        DesiredState desired = Desired(
            ("minimumReviewers", """{"count":2,"resetOnPush":true}"""),
            ("requiredChecks", """[{"name":"payments-ci"},{"name":"Payments-Security-Scan"}]"""),
            ("requireCommentResolution", "true"),
            ("allowedMergeTypes", """["squash"]"""),
            ("protectFromForcePushAndDelete", "true"));

        // Act
        RepoDiff diff = Diff(desired, LiveSnapshot.Empty);

        // Assert
        diff.Objects.Select(o => $"{o.Operation} {o.Key}").ShouldBe(
        [
            "Create ado:new/minimumReviewers",
            "Create ado:new/commentRequirements",
            "Create ado:new/mergeStrategy",
            "Create ado:new/build/payments-security-scan",
            "Create ado:new/build/payments-ci",
            $"Create ado:new/acl/{_context.RepoId}/refs/heads/main",
        ]);
        JsonNode reviewers = diff.Objects[0].Payload!;
        reviewers["type"]!["id"]!.GetValue<string>().ShouldBe(AzureDevOpsPolicyTypes.MinimumReviewers);
        reviewers["settings"]!["minimumApproverCount"]!.GetValue<int>().ShouldBe(2);
        reviewers["settings"]!["scope"]!.ToJsonString().ShouldBe($$"""[{"repositoryId":"{{_context.RepoId}}","refName":"refs/heads/main","matchKind":"Exact"}]""");
        diff.Objects[2].Payload!["settings"]!["allowSquash"]!.GetValue<bool>().ShouldBeTrue();
        diff.Objects[3].Payload!["settings"]!["buildDefinitionId"]!.GetValue<int>().ShouldBe(12);
        diff.Objects[5].Payload!.ToJsonString().ShouldBe(
            $$"""{"token":"{{ForcePushAcl.BranchToken(_context.ProjectId, _context.RepoId, "main")}}","merge":false,"accessControlEntries":[{"descriptor":"{{_context.ValidUsers}}","allow":0,"deny":8}]}""");
    }

    /// <summary>
    /// Given a policy and the live objects its own payloads would produce,
    /// when it is mapped and diffed again,
    /// then the repo is in sync.
    /// </summary>
    [Fact(DisplayName = "👍 Is in sync once the planned payloads are live")]
    public void Map_PayloadsLive_InSync()
    {
        // Arrange
        DesiredState desired = Desired(
            ("minimumReviewers", """{"count":2,"resetOnPush":false}"""),
            ("azuredevops.minimumReviewers", """{"allowDownvotes":false,"blockLastPusherVote":true,"creatorVoteCounts":false}"""),
            ("requiredChecks", """[{"name":"payments-ci"}]"""),
            ("azuredevops.requiredChecks", """{"validDuration":0,"statusChecks":[{"name":"sonar","genre":"quality"}]}"""),
            ("allowedMergeTypes", """["merge","rebase"]"""),
            ("azuredevops.allowedMergeTypes", """{"allowRebaseMerge":true}"""),
            ("protectFromForcePushAndDelete", "true"));
        RepoDiff first = Diff(desired, LiveSnapshot.Empty);
        LiveSnapshot live = LiveFrom(first);

        // Act
        RepoDiff second = Diff(desired, live);

        // Assert
        second.Objects.ShouldBeEmpty();
        second.Changes.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a live minimum reviewers policy with settings the policy doesn't manage,
    /// when a higher count is mapped,
    /// then it is an update by the live key that changes only the count and keeps the other settings.
    /// </summary>
    [Fact(DisplayName = "👍 Updates a live policy and keeps the settings the policy doesn't manage")]
    public void Map_LivePolicy_UpdatesAndKeepsOtherSettings()
    {
        // Arrange
        RepoContext context = _context.Build();
        JsonObject config = new PolicyConfigBuilder(context.Repo.Id).WithId(17).With("minimumApproverCount", 1).With("resetOnSourcePush", false).Build();
        LiveSnapshot live = LiveReader.Read(context, [config], [], d => d);

        // Act
        RepoDiff diff = Diff(Desired(("minimumReviewers", """{"count":2,"resetOnPush":false}""")), live, context);

        // Assert
        PlanObject update = diff.Objects.ShouldHaveSingleItem();
        update.Operation.ShouldBe(ObjectOperation.Update);
        update.Key.ShouldBe("ado:policy/17");
        update.Payload!["settings"]!["requireVoteOnLastIteration"]!.GetValue<bool>().ShouldBeTrue();
        diff.Changes.ShouldHaveSingleItem().Rule.ShouldBe("minimumReviewers.count");
    }

    /// <summary>
    /// Given live build validation for a pipeline the policy no longer lists,
    /// when the required checks are mapped,
    /// then that policy is deleted and the listed pipeline's is created.
    /// </summary>
    [Fact(DisplayName = "👍 Deletes build validation for pipelines the policy no longer lists")]
    public void Map_UnlistedPipeline_IsDeleted()
    {
        // Arrange
        RepoContext context = _context.Build();
        JsonObject old = new PolicyConfigBuilder(context.Repo.Id).WithId(40).BuildValidation(12).Build();
        LiveSnapshot live = LiveReader.Read(context, [old], [], d => d);

        // Act
        RepoDiff diff = Diff(Desired(("requiredChecks", """[{"name":"payments-ci"}]""")), live, context);

        // Assert
        diff.Objects.Select(o => $"{o.Operation} {o.Key}").ShouldBe(["Create ado:new/build/payments-ci", "Delete ado:policy/40"]);
    }

    /// <summary>
    /// Given a policy that names groups in access, which this build can't map on Azure DevOps,
    /// when the policy is mapped,
    /// then mapping fails with SP0207 instead of planning nothing, which would read as in sync.
    /// </summary>
    [Fact(DisplayName = "👎 Fails with SP0207 for access until groups are mapped")]
    public void Map_Access_FailsWithSP0207()
    {
        // Act
        var exception = Should.Throw<PolicyMappingException>(() => Diff(Desired(("access", """{"Contributors":"write"}""")), LiveSnapshot.Empty));

        // Assert
        exception.Code.ShouldBe(DiagnosticCodes.PrincipalNotFound);
    }

    /// <summary>
    /// Given a required check naming no pipeline, and one naming two pipelines in different folders,
    /// when the policy is mapped,
    /// then mapping fails with SP0206 naming the check.
    /// </summary>
    [Theory(DisplayName = "👎 Fails with SP0206 when a required check names no pipeline or several")]
    [InlineData("payments-deploy", "no pipeline is named 'payments-deploy'")]
    [InlineData("nightly", "2 pipelines are named 'nightly'")]
    public void Map_UnresolvablePipeline_FailsWithSP0206(string name, string expected)
    {
        // Arrange
        _context.WithPipeline(21, "nightly", "\\ops").WithPipeline(22, "nightly", "\\payments");

        // Act
        var exception = Should.Throw<PolicyMappingException>(() => Diff(Desired(("requiredChecks", $$"""[{"name":"{{name}}"}]""")), LiveSnapshot.Empty));

        // Assert
        exception.Code.ShouldBe(DiagnosticCodes.PipelineNotFound);
        exception.Message.ShouldContain(expected);
    }

    /// <summary>
    /// Given a live Force push deny for Project Valid Users with other bits set,
    /// when the protection is turned off,
    /// then the entry is updated with only the Force push bit cleared, never deleted.
    /// </summary>
    [Fact(DisplayName = "🖐️ Turning the protection off clears only the Force push bit")]
    public void Map_ProtectionOff_ClearsOnlyTheBit()
    {
        // Arrange
        RepoContext context = _context.Build();
        var aces = new JsonObject { [context.ValidUsersDescriptor!] = new JsonObject { ["descriptor"] = context.ValidUsersDescriptor, ["allow"] = 0, ["deny"] = 8 | 4 } };
        LiveSnapshot live = LiveReader.Read(context, [], new JsonObject { ["acesDictionary"] = aces }, d => d);

        // Act
        RepoDiff diff = Diff(Desired(("protectFromForcePushAndDelete", null)), live, context);

        // Assert
        PlanObject update = diff.Objects.ShouldHaveSingleItem();
        update.Operation.ShouldBe(ObjectOperation.Update);
        update.Payload!["accessControlEntries"]![0]!["deny"]!.GetValue<int>().ShouldBe(4);
        diff.Changes.ShouldHaveSingleItem().Action.ShouldBe(ChangeAction.Remove);
    }

    /// <summary>
    /// Given two matching live minimum reviewers policies,
    /// when a count is declared,
    /// then nothing is written for the rule and SP2201 warns.
    /// </summary>
    [Fact(DisplayName = "👎 Writes nothing for an ambiguous rule")]
    public void Map_Ambiguous_WritesNothing()
    {
        // Arrange
        RepoContext context = _context.Build();
        LiveSnapshot live = LiveReader.Read(context, [new PolicyConfigBuilder(context.Repo.Id).Build(), new PolicyConfigBuilder(context.Repo.Id).Build()], [], d => d);

        // Act
        RepoDiff diff = Diff(Desired(("minimumReviewers", """{"count":3,"resetOnPush":false}""")), live, context);

        // Assert
        diff.Objects.ShouldBeEmpty();
        diff.Diagnostics.ShouldContain(d => d.Code == DiagnosticCodes.AmbiguousMatch);
    }

    /// <summary>
    /// Given only the provider options for minimum reviewers, and no live policy,
    /// when they are mapped,
    /// then nothing is created, since the options only adjust a policy that exists.
    /// </summary>
    [Fact(DisplayName = "🖐️ Creates nothing from provider options alone")]
    public void Map_OptionsOnly_CreatesNothing()
    {
        // Act
        RepoDiff diff = Diff(Desired(("azuredevops.minimumReviewers", """{"allowDownvotes":true,"blockLastPusherVote":false,"creatorVoteCounts":false}""")), LiveSnapshot.Empty);

        // Assert
        diff.Objects.ShouldBeEmpty();
    }

    private RepoDiff Diff(DesiredState desired, LiveSnapshot live, RepoContext? context = null) =>
        RepoDiffer.Diff(desired, live, ObjectMapper.Map(desired, live, context ?? _context.Build()));

    // The live state once every planned object is written: each payload becomes a live object the reader reads.
    private LiveSnapshot LiveFrom(RepoDiff diff)
    {
        RepoContext context = _context.Build();
        var configs = new List<JsonObject>();
        var aces = new JsonObject();
        int id = 100;
        foreach (PlanObject planned in diff.Objects)
        {
            var payload = (JsonObject)planned.Payload!.DeepClone();
            if (ForcePushAcl.FromPayload(payload) is { } entry)
            {
                aces[entry.Descriptor] = new JsonObject { ["descriptor"] = entry.Descriptor, ["allow"] = entry.Allow, ["deny"] = entry.Deny };
                continue;
            }

            payload["id"] = id++;
            configs.Add(payload);
        }

        return LiveReader.Read(context, configs, new JsonObject { ["acesDictionary"] = aces }, d => d);
    }

    private DesiredState Desired(params (string Rule, string? Json)[] rules) => new(
        new RepoRef("ado-onprem", "Payments", "payments-api"),
        rules.ToDictionary(
            r => r.Rule,
            r => new DesiredRule(r.Rule, r.Json is null ? null : CanonicalJson.ForRule(JsonNode.Parse(r.Json)), s_baseline, false),
            StringComparer.Ordinal));
}
