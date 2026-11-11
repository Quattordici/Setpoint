using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Policies;
using Setpoint.Adapters.AzureDevOps.Tests.TestData;
using Setpoint.Adapters.AzureDevOps.Tests.TestSupport;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;
using Setpoint.Http.Tests.TestSupport;
using WireMock.Server;

namespace Setpoint.Adapters.AzureDevOps.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsAdapter.ApplyAsync"/> against a stubbed Azure DevOps Server collection: policy
/// configurations created, replaced by id and deleted with the planned payload sent as-is, the Force push entry set
/// on the default branch, 403s that name the missing scope and permission, a failed write stopping the repo, and
/// planned objects about anything but this repo's default branch refused without a request.
/// </summary>
/// <remarks>
/// The answers are built from the REST reference. Fixtures recorded on real servers replace them in #75.
/// </remarks>
public sealed class AzureDevOpsApplyTests : IAsyncLifetime
{
    private const string ValidUsers = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-3";

    private readonly string _token = new Bogus.Randomizer().AlphaNumeric(52);
    private readonly string _projectId = Guid.NewGuid().ToString();
    private WireMockServer _server = null!;
    private AzureDevOpsApiStub _api = null!;
    private AzureDevOpsAdapter _sut = null!;
    private RepoRef _repo = null!;
    private string _repoId = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("ado-apply", TestContext.Current.CancellationToken);
        _api = new AzureDevOpsApiStub(_server, _token);
        _api.ConnectionData("Setpoint Service", @"CORP\svc-setpoint").PolicyArea("7.0").Projects((_projectId, "Payments")).PolicyTypes(_projectId);
        _sut = new AzureDevOpsAdapter(new AzureDevOpsAdapterOptions
        {
            GetEnvironmentVariable = name => name == "SETPOINT_ADO_ONPREM_TOKEN" ? _token : null,
            Http = new HttpCoreOptions { MaxRetries = 1, BaseDelay = TimeSpan.FromMilliseconds(1) },
        });

        JsonObject listedRepo = new AdoRepoBuilder().InProject("Payments").WithName("payments-api").Build();
        listedRepo["project"]!["id"] = _projectId;
        _repoId = listedRepo["id"]!.GetValue<string>();
        _api.Repositories(listedRepo);

        var target = new Target("ado-onprem", new TargetDefinition { Platform = Platform.AzureDevOps, Url = _api.CollectionUrl });
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        await foreach (ListedRepo listed in _sut.ListReposAsync(target, TestContext.Current.CancellationToken))
        {
            _repo = listed.Repo;
        }
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        WireMockServerFactory.Dispose(_server);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given a planned create of a minimum reviewers policy on the repo's default branch,
    /// when it is applied,
    /// then the planned payload is POSTed to the project's policy configurations exactly as the plan holds it.
    /// </summary>
    [Fact(DisplayName = "👍 Creates a policy by sending the planned payload as-is")]
    public async Task Apply_CreatePolicy_PostsPlannedPayload()
    {
        // Arrange
        _api.PolicyWrites(_projectId);
        JsonObject payload = PolicyPayload(_repoId);
        var planned = new PlanObject("ado:new/minimumReviewers", ObjectOperation.Create, Fingerprint.NothingMatches(ReviewersMatchKey), ReviewersMatchKey, payload);

        // Act
        ApplyResult result = await ApplyAsync(planned);

        // Assert
        result.Succeeded.ShouldBeTrue();
        var write = Writes().ShouldHaveSingleItem();
        write.Method.ShouldBe("POST");
        write.Path.ShouldBe($"{AzureDevOpsApiStub.Collection}/{_projectId}/_apis/policy/configurations");
        write.Query!["api-version"].ToString().ShouldBe("7.0");
        JsonNode.DeepEquals(JsonNode.Parse(write.Body!), payload).ShouldBeTrue();
    }

    /// <summary>
    /// Given a planned update of the live policy 17,
    /// when it is applied,
    /// then the payload is PUT to policy 17.
    /// </summary>
    [Fact(DisplayName = "👍 Updates a policy by its id")]
    public async Task Apply_UpdatePolicy_PutsToPolicyId()
    {
        // Arrange
        _api.PolicyWrites(_projectId);
        JsonObject payload = PolicyPayload(_repoId);
        var planned = new PlanObject("ado:policy/17", ObjectOperation.Update, "sha256:abc", ReviewersMatchKey, payload);

        // Act
        ApplyResult result = await ApplyAsync(planned);

        // Assert
        result.Succeeded.ShouldBeTrue();
        var write = Writes().ShouldHaveSingleItem();
        write.Method.ShouldBe("PUT");
        write.Path.ShouldBe($"{AzureDevOpsApiStub.Collection}/{_projectId}/_apis/policy/configurations/17");
        JsonNode.DeepEquals(JsonNode.Parse(write.Body!), payload).ShouldBeTrue();
    }

    /// <summary>
    /// Given a planned delete of the build validation policy 18,
    /// when it is applied,
    /// then policy 18 is deleted.
    /// </summary>
    [Fact(DisplayName = "👍 Deletes a policy the policy no longer wants")]
    public async Task Apply_DeletePolicy_DeletesById()
    {
        // Arrange
        _api.PolicyWrites(_projectId);
        var planned = new PlanObject("ado:policy/18", ObjectOperation.Delete, "sha256:def", "ado:build/refs/heads/main/11");

        // Act
        ApplyResult result = await ApplyAsync(planned);

        // Assert
        result.Succeeded.ShouldBeTrue();
        var write = Writes().ShouldHaveSingleItem();
        write.Method.ShouldBe("DELETE");
        write.Path.ShouldBe($"{AzureDevOpsApiStub.Collection}/{_projectId}/_apis/policy/configurations/18");
    }

    /// <summary>
    /// Given a planned Force push deny for Project Valid Users on the default branch,
    /// when it is applied,
    /// then the entry is set in the Git Repositories namespace with the planned payload, which replaces only that entry.
    /// </summary>
    [Fact(DisplayName = "👍 Sets the Force push entry on the default branch")]
    public async Task Apply_ForcePush_SetsEntry()
    {
        // Arrange
        _api.AclWrites();
        JsonObject payload = ForcePushAcl.Payload(BranchToken, new AccessControlEntry(ValidUsers, 0, 8));
        var planned = new PlanObject($"ado:acl/{_repoId}/refs/heads/main", ObjectOperation.Update, "sha256:abc", ForcePushAcl.MatchKey("refs/heads/main"), payload);

        // Act
        ApplyResult result = await ApplyAsync(planned);

        // Assert
        result.Succeeded.ShouldBeTrue();
        var write = Writes().ShouldHaveSingleItem();
        write.Path.ShouldBe($"{AzureDevOpsApiStub.Collection}/_apis/accesscontrolentries/{ForcePushAcl.NamespaceId}");
        JsonNode sent = JsonNode.Parse(write.Body!)!;
        JsonNode.DeepEquals(sent, payload).ShouldBeTrue();
        sent["merge"]!.GetValue<bool>().ShouldBeFalse();
    }

    /// <summary>
    /// Given a token without Edit policies, which Azure DevOps answers with 403,
    /// when a planned policy update is applied,
    /// then the repo fails with a message naming vso.code_write and Edit policies.
    /// </summary>
    [Fact(DisplayName = "👎 A 403 on a policy names vso.code_write and Edit policies")]
    public async Task Apply_PolicyForbidden_NamesScopeAndPermission()
    {
        // Arrange
        _api.PolicyWrites(_projectId, 403, """{"message":"TF401027: You need the Git 'EditPolicies' permission to perform this action."}""");
        var planned = new PlanObject("ado:policy/17", ObjectOperation.Update, "sha256:abc", ReviewersMatchKey, PolicyPayload(_repoId));

        // Act
        ApplyResult result = await ApplyAsync(planned);

        // Assert
        result.Succeeded.ShouldBeFalse();
        string error = result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull();
        error.ShouldContain("403: TF401027");
        error.ShouldContain("vso.code_write");
        error.ShouldContain("Edit policies");
    }

    /// <summary>
    /// Given a token without Manage permissions, which Azure DevOps answers with 403,
    /// when the Force push entry is applied,
    /// then the repo fails with a message naming vso.security_manage and Manage permissions.
    /// </summary>
    [Fact(DisplayName = "👎 A 403 on the Force push entry names vso.security_manage and Manage permissions")]
    public async Task Apply_AclForbidden_NamesScopeAndPermission()
    {
        // Arrange
        _api.AclWrites(403, """{"message":"TF400813: The user is not authorized to access this resource."}""");
        JsonObject payload = ForcePushAcl.Payload(BranchToken, new AccessControlEntry(ValidUsers, 0, 8));
        var planned = new PlanObject($"ado:new/acl/{_repoId}/refs/heads/main", ObjectOperation.Create, "sha256:abc", ForcePushAcl.MatchKey("refs/heads/main"), payload);

        // Act
        ApplyResult result = await ApplyAsync(planned);

        // Assert
        string error = result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull();
        error.ShouldContain("vso.security_manage");
        error.ShouldContain("Manage permissions");
    }

    /// <summary>
    /// Given a policy that was deleted since the plan, which Azure DevOps answers with 404,
    /// when its planned update is applied,
    /// then the error says to plan again.
    /// </summary>
    [Fact(DisplayName = "👎 A policy that no longer exists says to plan again")]
    public async Task Apply_PolicyGone_SaysPlanAgain()
    {
        // Arrange
        _api.PolicyWrites(_projectId, 404, """{"message":"The policy configuration with ID 17 does not exist."}""");
        var planned = new PlanObject("ado:policy/17", ObjectOperation.Update, "sha256:abc", ReviewersMatchKey, PolicyPayload(_repoId));

        // Act
        ApplyResult result = await ApplyAsync(planned);

        // Assert
        result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull().ShouldContain("Plan again");
    }

    /// <summary>
    /// Given a write that fails, followed by another planned object,
    /// when they are applied,
    /// then the second is not attempted, because a failed write stops the repo.
    /// </summary>
    [Fact(DisplayName = "👎 A failed write stops the rest of the repo")]
    public async Task Apply_FailureThenMore_StopsTheRepo()
    {
        // Arrange
        _api.PolicyWrites(_projectId, 403, """{"message":"denied"}""");
        _api.AclWrites();
        var failing = new PlanObject("ado:policy/17", ObjectOperation.Update, "sha256:abc", ReviewersMatchKey, PolicyPayload(_repoId));
        var next = new PlanObject(
            $"ado:acl/{_repoId}/refs/heads/main",
            ObjectOperation.Update,
            "sha256:def",
            ForcePushAcl.MatchKey("refs/heads/main"),
            ForcePushAcl.Payload(BranchToken, new AccessControlEntry(ValidUsers, 0, 8)));

        // Act
        ApplyResult result = await ApplyAsync(failing, next);

        // Assert
        result.Objects[1].Succeeded.ShouldBeFalse();
        result.Objects[1].Error.ShouldNotBeNull().ShouldContain("Not attempted");
        Writes().ShouldHaveSingleItem().Method.ShouldBe("PUT");
    }

    /// <summary>
    /// Given a planned policy scoped to another repo, as a hand-edited plan file could hold,
    /// when it is applied,
    /// then it is refused without any request.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a policy scoped to another repo")]
    public async Task Apply_PolicyForOtherRepo_IsRefusedWithoutWriting()
    {
        // Arrange
        _api.PolicyWrites(_projectId);
        var planned = new PlanObject("ado:policy/17", ObjectOperation.Update, "sha256:abc", ReviewersMatchKey, PolicyPayload(Guid.NewGuid().ToString()));

        // Act
        ApplyResult result = await ApplyAsync(planned);

        // Assert
        result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull().ShouldContain("isn't scoped to exactly payments-api's default branch");
        Writes().ShouldBeEmpty();
    }

    /// <summary>
    /// Given a planned Force push entry on another branch's token,
    /// when it is applied,
    /// then it is refused without any request.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a Force push entry on another token")]
    public async Task Apply_AclForOtherToken_IsRefusedWithoutWriting()
    {
        // Arrange
        _api.AclWrites();
        JsonObject payload = ForcePushAcl.Payload(ForcePushAcl.BranchToken(_projectId, _repoId, "release"), new AccessControlEntry(ValidUsers, 0, 8));
        var planned = new PlanObject($"ado:acl/{_repoId}/refs/heads/main", ObjectOperation.Update, "sha256:abc", ForcePushAcl.MatchKey("refs/heads/main"), payload);

        // Act
        ApplyResult result = await ApplyAsync(planned);

        // Assert
        result.Succeeded.ShouldBeFalse();
        Writes().ShouldBeEmpty();
    }

    private const string ReviewersMatchKey = "ado:minimumReviewers/refs/heads/main";

    private string BranchToken => ForcePushAcl.BranchToken(_projectId, _repoId, "main");

    private Task<ApplyResult> ApplyAsync(params PlanObject[] objects) =>
        _sut.ApplyAsync(_repo, new RepoChangeSet(objects), TestContext.Current.CancellationToken);

    private List<WireMock.IRequestMessage> Writes() =>
        [.. _server.LogEntries.Select(e => e.RequestMessage).OfType<WireMock.IRequestMessage>().Where(r => r.Method is not ("GET" or "OPTIONS"))];

    private static JsonObject PolicyPayload(string repoId)
    {
        JsonObject config = new PolicyConfigBuilder(repoId).Build();
        return new JsonObject
        {
            ["isEnabled"] = true,
            ["isBlocking"] = true,
            ["type"] = new JsonObject { ["id"] = AzureDevOpsPolicyTypes.MinimumReviewers },
            ["settings"] = config["settings"]!.DeepClone(),
        };
    }
}
