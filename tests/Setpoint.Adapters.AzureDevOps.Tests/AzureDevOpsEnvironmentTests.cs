using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Policies;
using Setpoint.Adapters.AzureDevOps.Tests.TestData;
using Setpoint.Adapters.AzureDevOps.Tests.TestSupport;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Merging;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;
using Setpoint.Http.Tests.TestSupport;
using WireMock.Server;

namespace Setpoint.Adapters.AzureDevOps.Tests;

/// <summary>
/// Tests for the <c>environments</c> rule on <see cref="AzureDevOpsAdapter"/> against a stubbed Azure DevOps Server
/// collection: the project's environments and their Approval checks read, a missing environment created with its
/// check, approvers replaced with the settings Setpoint doesn't manage kept, <c>enabled: false</c> removing the check,
/// other and ambiguous checks left alone, fields Azure DevOps doesn't have warned about, and writing at apply.
/// </summary>
public sealed class AzureDevOpsEnvironmentTests : IAsyncLifetime
{
    private const string Releasers = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-5";
    private const string ReleasersId = "5a4f0b2c-9d1e-4c33-8f7a-0b6e2d1c4a10";
    private const string Owner = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-6";
    private const string OwnerId = "6b5a1c3d-0e2f-4d44-9a8b-1c7f3e2d5b21";
    private const long ProductionId = 12;

    private static readonly LayerRef s_baseline = new(LayerKind.Baseline, null, SourceLocation.StartOf("baseline.yml"));

    private readonly string _token = new Bogus.Randomizer().AlphaNumeric(52);
    private readonly string _projectId = Guid.NewGuid().ToString();
    private WireMockServer _server = null!;
    private AzureDevOpsApiStub _api = null!;
    private AzureDevOpsAdapter _sut = null!;
    private RepoRef _repo = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("ado-environments", TestContext.Current.CancellationToken);
        _api = new AzureDevOpsApiStub(_server, _token);
        _api.ConnectionData("Setpoint Service", @"CORP\svc-setpoint").PolicyArea("7.0").Projects((_projectId, "Payments")).PolicyTypes(_projectId);
        _sut = new AzureDevOpsAdapter(new AzureDevOpsAdapterOptions
        {
            GetEnvironmentVariable = name => name == "SETPOINT_ADO_ONPREM_TOKEN" ? _token : null,
            Http = new HttpCoreOptions { MaxRetries = 1, BaseDelay = TimeSpan.FromMilliseconds(1) },
        });

        JsonObject listedRepo = new AdoRepoBuilder().InProject("Payments").WithName("payments-api").Build();
        listedRepo["project"]!["id"] = _projectId;
        string repoId = listedRepo["id"]!.GetValue<string>();
        _api.Repositories(listedRepo)
            .PolicyConfigurations(_projectId, [], [])
            .BuildDefinitions(_projectId)
            .ValidUsers("Payments", Releasers)
            .Acl(ForcePushAcl.BranchToken(_projectId, repoId, "main"))
            .Acl(RepoAccessAcl.RepoToken(_projectId, repoId))
            .IdentitySearchWithIds(@"[Payments]\Release Managers", (Releasers, @"[Payments]\Release Managers", ReleasersId))
            .IdentitySearchWithIds(@"[Payments]\Payments Owners", (Owner, @"[Payments]\Payments Owners", OwnerId));

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
    /// Given a project without a production environment,
    /// when the policy wants Release Managers to approve deployments to it,
    /// then the environment is created with an Approval check that needs that group, shared by the project.
    /// </summary>
    [Fact(DisplayName = "👍 Creates a missing environment with its Approval check")]
    public async Task Diff_MissingEnvironment_IsCreatedWithCheck()
    {
        // Arrange
        _api.Environments(_projectId);

        // Act
        (RepoDiff diff, IReadOnlyList<DesiredObject> objects) = await DiffAsync("""{"production":{"enabled":true,"reviewers":["Release Managers"]}}""");

        // Assert
        PlanObject created = diff.Objects.ShouldHaveSingleItem();
        created.Key.ShouldBe("ado:new/environment/production");
        created.Operation.ShouldBe(ObjectOperation.Create);
        created.Payload!["environment"]!.GetValue<string>().ShouldBe("production");
        created.Payload["checkId"].ShouldBeNull();
        JsonNode check = created.Payload["check"].ShouldNotBeNull();
        check["type"]!["name"]!.GetValue<string>().ShouldBe("Approval");
        check["settings"]!["approvers"]!.AsArray().ShouldHaveSingleItem()!["id"]!.GetValue<string>().ShouldBe(ReleasersId);
        check["settings"]!["minRequiredApprovers"]!.GetValue<int>().ShouldBe(1);
        check["timeout"]!.GetValue<long>().ShouldBe(EnvironmentApproval.DefaultTimeoutMinutes);
        objects.Single(o => EnvironmentApproval.IsMatchKey(o.MatchKey)).SharedBy.ShouldBe("project Payments");
    }

    /// <summary>
    /// Given production with an Approval check that needs Release Managers,
    /// when the policy wants the same,
    /// then nothing changes.
    /// </summary>
    [Fact(DisplayName = "👍 An environment with its approvers is in sync")]
    public async Task Diff_InSync_HasNoObjects()
    {
        // Arrange
        _api.Environments(_projectId, (ProductionId, "Production"))
            .EnvironmentChecks(_projectId, ProductionId, AzureDevOpsApiStub.ApprovalCheck(31, ProductionId, (ReleasersId, "Release Managers")));

        // Act
        (RepoDiff diff, _) = await DiffAsync("""{"production":{"enabled":true,"reviewers":["Release Managers"]}}""");

        // Assert
        diff.Objects.ShouldBeEmpty();
    }

    /// <summary>
    /// Given production with an Approval check that needs Payments Owners, with instructions and a one-day timeout,
    /// when the policy wants Release Managers,
    /// then check 31 is replaced with the new approver, and its instructions and timeout are kept.
    /// </summary>
    [Fact(DisplayName = "👍 Replaces the approvers and keeps the check's other settings")]
    public async Task Diff_OtherApprovers_UpdatesCheck()
    {
        // Arrange
        _api.Environments(_projectId, (ProductionId, "Production"))
            .EnvironmentChecks(_projectId, ProductionId, AzureDevOpsApiStub.ApprovalCheck(31, ProductionId, (OwnerId, "Payments Owners")));

        // Act
        (RepoDiff diff, _) = await DiffAsync("""{"production":{"enabled":true,"reviewers":["Release Managers"]}}""");

        // Assert
        PlanObject updated = diff.Objects.ShouldHaveSingleItem();
        updated.Key.ShouldBe($"ado:environment/{ProductionId}");
        updated.Operation.ShouldBe(ObjectOperation.Update);
        updated.Payload!["checkId"]!.GetValue<long>().ShouldBe(31);
        JsonNode check = updated.Payload["check"]!;
        check["settings"]!["approvers"]!.AsArray().ShouldHaveSingleItem()!["id"]!.GetValue<string>().ShouldBe(ReleasersId);
        check["settings"]!["instructions"]!.GetValue<string>().ShouldBe("Check the release notes.");
        check["timeout"]!.GetValue<long>().ShouldBe(1440);
        diff.Changes.ShouldHaveSingleItem().Rule.ShouldBe("environments.production");
    }

    /// <summary>
    /// Given production with an Approval check,
    /// when the policy disables production,
    /// then the check is removed and the environment kept.
    /// </summary>
    [Fact(DisplayName = "👍 enabled: false removes the Approval check")]
    public async Task Diff_Disabled_RemovesCheck()
    {
        // Arrange
        _api.Environments(_projectId, (ProductionId, "Production"))
            .EnvironmentChecks(_projectId, ProductionId, AzureDevOpsApiStub.ApprovalCheck(31, ProductionId, (ReleasersId, "Release Managers")));

        // Act
        (RepoDiff diff, _) = await DiffAsync("""{"production":{"enabled":false}}""");

        // Assert
        PlanObject cleared = diff.Objects.ShouldHaveSingleItem();
        cleared.Operation.ShouldBe(ObjectOperation.Update);
        cleared.Payload!["check"].ShouldBeNull();
        cleared.Payload["checkId"]!.GetValue<long>().ShouldBe(31);
    }

    /// <summary>
    /// Given production with a Business hours check besides its Approval check, and staging with two Approval checks,
    /// when the policy names both,
    /// then the Business hours check is unmanaged, and staging is left alone with SP2201.
    /// </summary>
    [Fact(DisplayName = "👎 Other checks are unmanaged and two Approval checks are ambiguous")]
    public async Task Diff_OtherAndAmbiguousChecks_AreLeftAlone()
    {
        // Arrange
        var businessHours = new JsonObject { ["id"] = 40, ["type"] = new JsonObject { ["id"] = "fe1de3ee-a436-41b4-bb20-f6eb4cb879a7", ["name"] = "Task Check" }, ["timeout"] = 60 };
        _api.Environments(_projectId, (ProductionId, "Production"), (13, "staging"))
            .EnvironmentChecks(_projectId, ProductionId, AzureDevOpsApiStub.ApprovalCheck(31, ProductionId, (ReleasersId, "Release Managers")), businessHours)
            .EnvironmentChecks(_projectId, 13, AzureDevOpsApiStub.ApprovalCheck(32, 13, (ReleasersId, "Release Managers")), AzureDevOpsApiStub.ApprovalCheck(33, 13, (OwnerId, "Payments Owners")));

        // Act
        (RepoDiff diff, _) = await DiffAsync("""{"production":{"enabled":true,"reviewers":["Release Managers"]},"staging":{"enabled":true,"reviewers":["Payments Owners"]}}""");

        // Assert
        diff.Objects.ShouldBeEmpty();
        diff.Unmanaged.ShouldContain(u => u.Source == "check 'Task Check' on environment Production");
        diff.Diagnostics.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            d => d.Code.ShouldBe(DiagnosticCodes.AmbiguousMatch),
            d => d.Message.ShouldContain("staging has 2 Approval checks"));
    }

    /// <summary>
    /// Given a policy that sets waitMinutes and branches: default on production,
    /// when it is planned for Azure DevOps,
    /// then each raises SP3002, because environments here have neither, and the approvers are still planned.
    /// </summary>
    [Fact(DisplayName = "👎 waitMinutes and branches: default are ignored with SP3002")]
    public async Task Diff_WaitAndBranches_WarnAndAreIgnored()
    {
        // Arrange
        _api.Environments(_projectId);

        // Act
        (RepoDiff diff, _) = await DiffAsync("""{"production":{"enabled":true,"reviewers":["Release Managers"],"waitMinutes":15,"branches":"default"}}""");

        // Assert
        diff.Diagnostics.Select(d => d.Code).ShouldBe([DiagnosticCodes.RuleUnavailable, DiagnosticCodes.RuleUnavailable]);
        diff.Objects.ShouldHaveSingleItem().Operation.ShouldBe(ObjectOperation.Create);
    }

    /// <summary>
    /// Given a planned create of production, which doesn't exist,
    /// when it is applied,
    /// then the environment is created, and its Approval check is added on the new environment's id.
    /// </summary>
    [Fact(DisplayName = "👍 Applies a new environment and then its check")]
    public async Task Apply_NewEnvironment_CreatesItThenTheCheck()
    {
        // Arrange
        _api.Environments(_projectId).EnvironmentWrites(_projectId, createdId: 77);
        (RepoDiff diff, _) = await DiffAsync("""{"production":{"enabled":true,"reviewers":["Release Managers"]}}""");

        // Act
        ApplyResult result = await _sut.ApplyAsync(_repo, new RepoChangeSet(diff.Objects), TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.ShouldBeTrue();
        WireMock.IRequestMessage[] writes = [.. Writes()];
        writes.Select(w => $"{w.Method} {w.Path}").ShouldBe(
        [
            $"POST {AzureDevOpsApiStub.Collection}/{_projectId}/_apis/distributedtask/environments",
            $"POST {AzureDevOpsApiStub.Collection}/{_projectId}/_apis/pipelines/checks/configurations",
        ]);
        JsonNode.Parse(writes[1].Body!)!["resource"]!["id"]!.GetValue<string>().ShouldBe("77");
    }

    /// <summary>
    /// Given a planned removal of check 31 on production,
    /// when it is applied,
    /// then check 31 is deleted after confirming it is an Approval check there, and the environment is kept.
    /// </summary>
    [Fact(DisplayName = "👍 Applies a removed check with DELETE")]
    public async Task Apply_RemovedCheck_DeletesIt()
    {
        // Arrange
        _api.Environments(_projectId, (ProductionId, "Production"))
            .EnvironmentChecks(_projectId, ProductionId, AzureDevOpsApiStub.ApprovalCheck(31, ProductionId, (ReleasersId, "Release Managers")))
            .EnvironmentWrites(_projectId);
        (RepoDiff diff, _) = await DiffAsync("""{"production":{"enabled":false}}""");

        // Act
        ApplyResult result = await _sut.ApplyAsync(_repo, new RepoChangeSet(diff.Objects), TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.ShouldBeTrue();
        Writes().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            w => w.Method.ShouldBe("DELETE"),
            w => w.Path.ShouldBe($"{AzureDevOpsApiStub.Collection}/{_projectId}/_apis/pipelines/checks/configurations/31"));
    }

    /// <summary>
    /// Given a token without Environment (read &amp; manage), which Azure DevOps answers with 403,
    /// when a new environment is applied,
    /// then the error names vso.environment_manage.
    /// </summary>
    [Fact(DisplayName = "👎 A 403 on an environment names vso.environment_manage")]
    public async Task Apply_Forbidden_NamesScope()
    {
        // Arrange
        _api.Environments(_projectId).EnvironmentWrites(_projectId, 403, """{"message":"TF400813: The user is not authorized to access this resource."}""");
        (RepoDiff diff, _) = await DiffAsync("""{"production":{"enabled":true,"reviewers":["Release Managers"]}}""");

        // Act
        ApplyResult result = await _sut.ApplyAsync(_repo, new RepoChangeSet(diff.Objects), TestContext.Current.CancellationToken);

        // Assert
        result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull().ShouldContain("vso.environment_manage");
    }

    /// <summary>
    /// Given a planned environment whose name isn't the one its match key names, as a hand-edited plan file could hold,
    /// when it is applied,
    /// then it is refused without a request.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses an environment its match key doesn't name")]
    public async Task Apply_MismatchedEnvironment_IsRefused()
    {
        // Arrange
        _api.EnvironmentWrites(_projectId);
        var payload = new JsonObject { ["environment"] = "staging", ["checkId"] = null, ["check"] = null };
        var planned = new PlanObject("ado:new/environment/production", ObjectOperation.Create, "sha256:abc", EnvironmentApproval.MatchKey(_projectId, "production"), payload);

        // Act
        ApplyResult result = await _sut.ApplyAsync(_repo, new RepoChangeSet([planned]), TestContext.Current.CancellationToken);

        // Assert
        result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull().ShouldContain("Nothing was written");
        Writes().ShouldBeEmpty();
    }

    private async Task<(RepoDiff Diff, IReadOnlyList<DesiredObject> Objects)> DiffAsync(string environments)
    {
        var desired = new DesiredState(_repo, new Dictionary<string, DesiredRule>(StringComparer.Ordinal)
        {
            ["environments"] = new("environments", CanonicalJson.ForRule(JsonNode.Parse(environments)), s_baseline, false),
        });
        LiveSnapshot read = await _sut.ReadAsync(_repo, TestContext.Current.CancellationToken);
        LiveSnapshot live = await _sut.PrepareAsync(desired, read, TestContext.Current.CancellationToken);
        IReadOnlyList<DesiredObject> objects = _sut.MapObjects(desired, live);
        return (RepoDiffer.Diff(desired, live, objects), objects);
    }

    private List<WireMock.IRequestMessage> Writes() =>
        [.. _server.LogEntries.Select(e => e.RequestMessage).OfType<WireMock.IRequestMessage>().Where(r => r.Method is not ("GET" or "OPTIONS"))];
}
