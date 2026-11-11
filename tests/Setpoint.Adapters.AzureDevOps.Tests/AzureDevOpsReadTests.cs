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
/// Tests for the read side of <see cref="AzureDevOpsAdapter"/> against a stubbed Azure DevOps Server collection:
/// reading a repo's policies across pages, the default branch's Force push entries with their identities, sharing
/// a project's reads between its repos, and a read-only token that can't see security. Nothing here writes.
/// </summary>
public sealed class AzureDevOpsReadTests : IAsyncLifetime
{
    private const string ValidUsers = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-3";
    private const string Creator = "Microsoft.TeamFoundation.Identity;S-1-5-21-3623811015-3361044348-30300820-1013";

    private readonly string _token = new Bogus.Randomizer().AlphaNumeric(52);
    private readonly string _projectId = Guid.NewGuid().ToString();
    private WireMockServer _server = null!;
    private AzureDevOpsApiStub _api = null!;
    private AzureDevOpsAdapter _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("ado-api", TestContext.Current.CancellationToken);
        _api = new AzureDevOpsApiStub(_server, _token);
        _api.ConnectionData("Setpoint Service", @"CORP\svc-setpoint").PolicyArea("7.0").Projects((_projectId, "Payments")).PolicyTypes(_projectId);
        _sut = new AzureDevOpsAdapter(new AzureDevOpsAdapterOptions
        {
            GetEnvironmentVariable = name => name == "SETPOINT_ADO_ONPREM_TOKEN" ? _token : null,
            Http = new HttpCoreOptions { MaxRetries = 1, BaseDelay = TimeSpan.FromMilliseconds(1) },
        });
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        WireMockServerFactory.Dispose(_server);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given a repo whose policies come in two pages, and a default branch ACL with the Project Valid Users deny and its creator's Allow,
    /// when it is read,
    /// then every owned policy and the Force push entry are live objects, and the creator is named in SP2103.
    /// </summary>
    [Fact(DisplayName = "👍 Reads a repo's policies across pages and its Force push entries")]
    public async Task Read_PoliciesAndAcl_ReadsEverything()
    {
        // Arrange
        (RepoRef repo, string repoId) = await ListOneRepoAsync();
        _api.PolicyConfigurations(
                _projectId,
                [new PolicyConfigBuilder(repoId).WithId(17).Build()],
                [new PolicyConfigBuilder(repoId).WithId(18).BuildValidation(11).Build()])
            .BuildDefinitions(_projectId, (11, "payments-ci"))
            .ValidUsers("Payments", ValidUsers)
            .Acl(ForcePushAcl.BranchToken(_projectId, repoId, "main"), (ValidUsers, 0, 8), (Creator, 8, 0))
            .Acl(RepoAccessAcl.RepoToken(_projectId, repoId))
            .Identities((ValidUsers, "[Payments]\\Project Valid Users"), (Creator, "Mattias"));

        // Act
        LiveSnapshot live = await _sut.ReadAsync(repo, TestContext.Current.CancellationToken);

        // Assert
        live.Objects.Select(o => o.Key).ShouldBe(["ado:policy/17", "ado:policy/18", $"ado:acl/{repoId}/refs/heads/main"]);
        live.Objects[1].Rules["requiredChecks"]!.ToJsonString().ShouldBe("""[{"name":"payments-ci"}]""");
        live.Warnings.ShouldNotBeNull().ShouldHaveSingleItem().Diagnostic.Message.ShouldContain("Mattias has an explicit Allow");
    }

    /// <summary>
    /// Given a token that can't read security, as the read-only plan PAT can't,
    /// when a repo is read,
    /// then its policies are read and the Force push protection is reported as unknown.
    /// </summary>
    [Fact(DisplayName = "🖐️ Reports the protection as unknown when the token can't read security")]
    public async Task Read_AclUnauthorized_ProtectionUnknown()
    {
        // Arrange
        (RepoRef repo, string repoId) = await ListOneRepoAsync();
        _api.PolicyConfigurations(_projectId, [new PolicyConfigBuilder(repoId).Build()], [])
            .BuildDefinitions(_projectId)
            .ValidUsers("Payments", ValidUsers)
            .AclUnauthorized();

        // Act
        LiveSnapshot live = await _sut.ReadAsync(repo, TestContext.Current.CancellationToken);

        // Assert
        live.Objects.ShouldHaveSingleItem().MatchKey.ShouldBe("ado:minimumReviewers/refs/heads/main");
        live.UnknownRules.ShouldBe(["protectFromForcePushAndDelete", "access", "environments"]);
    }

    /// <summary>
    /// Given two repos in one project,
    /// when both are read,
    /// then the project's policies, pipelines and Project Valid Users group are fetched once.
    /// </summary>
    [Fact(DisplayName = "🖐️ Reads a project's policies and pipelines once for all its repos")]
    public async Task Read_TwoReposOneProject_ReadsProjectOnce()
    {
        // Arrange
        JsonObject[] listedRepos = [new AdoRepoBuilder().InProject("Payments").WithName("api").Build(), new AdoRepoBuilder().InProject("Payments").WithName("ledger").Build()];
        foreach (JsonObject listedRepo in listedRepos)
        {
            listedRepo["project"]!["id"] = _projectId;
        }

        _api.Repositories(listedRepos);
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        List<RepoRef> repos = [];
        await foreach (ListedRepo listed in _sut.ListReposAsync(target, TestContext.Current.CancellationToken))
        {
            repos.Add(listed.Repo);
        }

        _api.PolicyConfigurations(_projectId, [], []).BuildDefinitions(_projectId).ValidUsers("Payments", ValidUsers).AclUnauthorized();

        // Act
        foreach (RepoRef repo in repos)
        {
            await _sut.ReadAsync(repo, TestContext.Current.CancellationToken);
        }

        // Assert
        _server.LogEntries.Count(e => e.RequestMessage!.Path.EndsWith("/_apis/build/definitions", StringComparison.Ordinal)).ShouldBe(1);
        _server.LogEntries.Count(e => e.RequestMessage!.Path.EndsWith("/_apis/accesscontrollists/" + ForcePushAcl.NamespaceId, StringComparison.Ordinal)).ShouldBe(4, "the branch and repo tokens of each repo");
    }

    /// <summary>
    /// Given a read repo and a policy requiring a pipeline that doesn't exist,
    /// when the adapter maps it,
    /// then mapping fails with SP0206.
    /// </summary>
    [Fact(DisplayName = "👎 Fails mapping with SP0206 when a required pipeline doesn't exist")]
    public async Task MapObjects_MissingPipeline_FailsWithSP0206()
    {
        // Arrange
        (RepoRef repo, _) = await ListOneRepoAsync();
        _api.PolicyConfigurations(_projectId, [], []).BuildDefinitions(_projectId, (11, "payments-ci")).ValidUsers("Payments", ValidUsers).AclUnauthorized();
        LiveSnapshot live = await _sut.ReadAsync(repo, TestContext.Current.CancellationToken);
        var desired = new DesiredState(repo, new Dictionary<string, DesiredRule>
        {
            ["requiredChecks"] = new("requiredChecks", JsonNode.Parse("""[{"name":"payments-deploy"}]"""), new LayerRef(LayerKind.Baseline, null, SourceLocation.StartOf("baseline.yml")), false),
        });

        // Act
        var exception = Should.Throw<PolicyMappingException>(() => _sut.MapObjects(desired, live));

        // Assert
        exception.Code.ShouldBe(DiagnosticCodes.PipelineNotFound);
    }

    /// <summary>
    /// Given a repo that is read,
    /// when every request is checked,
    /// then all of them were reads.
    /// </summary>
    [Fact(DisplayName = "🖐️ Sends only reads while reading a repo")]
    public async Task Read_SendsOnlyReads()
    {
        // Arrange
        (RepoRef repo, string repoId) = await ListOneRepoAsync();
        _api.PolicyConfigurations(_projectId, [new PolicyConfigBuilder(repoId).Build()], [])
            .BuildDefinitions(_projectId)
            .ValidUsers("Payments", ValidUsers)
            .Acl(ForcePushAcl.BranchToken(_projectId, repoId, "main"), (ValidUsers, 0, 8))
            .Acl(RepoAccessAcl.RepoToken(_projectId, repoId))
            .Identities((ValidUsers, "[Payments]\\Project Valid Users"));

        // Act
        await _sut.ReadAsync(repo, TestContext.Current.CancellationToken);

        // Assert
        _server.LogEntries.Select(e => e.RequestMessage!.Method).Distinct().ShouldBeSubsetOf(["GET", "OPTIONS"]);
    }

    private async Task<(RepoRef Repo, string RepoId)> ListOneRepoAsync()
    {
        JsonObject listedRepo = new AdoRepoBuilder().InProject("Payments").WithName("payments-api").Build();
        listedRepo["project"]!["id"] = _projectId;
        _api.Repositories(listedRepo);
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        await foreach (ListedRepo listed in _sut.ListReposAsync(target, TestContext.Current.CancellationToken))
        {
            return (listed.Repo, listedRepo["id"]!.GetValue<string>());
        }

        throw new InvalidOperationException("No repo was listed.");
    }

    private Target Target() => new("ado-onprem", new TargetDefinition { Platform = Platform.AzureDevOps, Url = _api.CollectionUrl });
}
