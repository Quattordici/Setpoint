using Setpoint.Adapters.AzureDevOps.Tests.TestData;
using Setpoint.Adapters.AzureDevOps.Tests.TestSupport;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;
using Setpoint.Http.Tests.TestSupport;
using WireMock.Server;

namespace Setpoint.Adapters.AzureDevOps.Tests;

/// <summary>
/// Tests for the preflight and repo listing of <see cref="AzureDevOpsAdapter"/> against a stubbed Azure DevOps
/// Server collection: telling Server from Services, the minimum release and <c>--allow-unsupported-server</c>,
/// the policy type check, PAT sign-in, and listing repos within scope with the skip rules. Nothing here writes.
/// </summary>
public sealed class AzureDevOpsAdapterTests : IAsyncLifetime
{
    private const string TargetName = "ado-onprem";
    private const string TokenVariable = "SETPOINT_ADO_ONPREM_TOKEN";

    private readonly string _token = new Bogus.Randomizer().AlphaNumeric(52);
    private readonly Dictionary<string, string> _environment = [];
    private WireMockServer _server = null!;
    private AzureDevOpsApiStub _api = null!;
    private AzureDevOpsAdapter _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("ado-api", TestContext.Current.CancellationToken);
        _api = new AzureDevOpsApiStub(_server, _token);
        _environment[TokenVariable] = _token;
        _sut = CreateAdapter(allowUnsupportedServer: false);
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        WireMockServerFactory.Dispose(_server);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given an Azure DevOps Server 2022 collection and its PAT in SETPOINT_ADO_ONPREM_TOKEN,
    /// when it is described,
    /// then the preflight names the release, the pinned API version and the signed-in identity.
    /// </summary>
    [Fact(DisplayName = "👍 Describes a Server 2022 collection with its release and identity")]
    public async Task Describe_Server2022_ReturnsReleaseAndIdentity()
    {
        // Arrange
        _api.Server("7.0");

        // Act
        TargetInfo info = await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);

        // Assert
        info.ShouldBe(new TargetInfo("Azure DevOps Server 2022", "7.0", @"Setpoint Service (CORP\svc-setpoint)"));
    }

    /// <summary>
    /// Given collections released at each REST version from 2022 on,
    /// when they are described,
    /// then each is named after its Server release and still gets the pinned API version.
    /// </summary>
    [Theory(DisplayName = "👍 Detects the Server release from the REST version it has released")]
    [InlineData("7.1", "Azure DevOps Server 2022.1")]
    [InlineData("7.2", "Azure DevOps Server (REST 7.2)")]
    public async Task Describe_NewerServer_NamesReleaseAndPins70(string released, string expected)
    {
        // Arrange
        _api.Server(released);

        // Act
        TargetInfo info = await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);

        // Assert
        info.Detected.ShouldBe(expected);
        info.ApiVersion.ShouldBe("7.0");
    }

    /// <summary>
    /// Given a target that connectionData reports as hosted,
    /// when it is described,
    /// then it is Azure DevOps Services, and the policy area's versions aren't asked for.
    /// </summary>
    [Fact(DisplayName = "👍 Detects Azure DevOps Services from the connection data")]
    public async Task Describe_Hosted_IsServices()
    {
        // Arrange
        string projectId = Guid.NewGuid().ToString();
        _api.ConnectionData("Setpoint Bot", "setpoint@contoso.example", deploymentType: "hosted").Projects((projectId, "Payments")).PolicyTypes(projectId);

        // Act
        TargetInfo info = await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);

        // Assert
        info.Detected.ShouldBe("Azure DevOps Services");
        _server.LogEntries.ShouldNotContain(e => e.RequestMessage != null && e.RequestMessage.Method == "OPTIONS");
    }

    /// <summary>
    /// Given an Azure DevOps Server 2020 collection,
    /// when it is described without --allow-unsupported-server,
    /// then it is refused, naming the release and the flag, before any project is read.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses servers older than 2022")]
    public async Task Describe_Server2020_IsRefused()
    {
        // Arrange
        _api.ApiVersion = "6.0";
        _api.Server("6.0");

        // Act
        var exception = await Should.ThrowAsync<NotSupportedException>(() => _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("Azure DevOps Server 2020");
        exception.Message.ShouldContain("--allow-unsupported-server");
        _server.LogEntries.ShouldNotContain(e => e.RequestMessage != null && e.RequestMessage.Path.EndsWith("/_apis/projects", StringComparison.Ordinal));
    }

    /// <summary>
    /// Given an Azure DevOps Server 2020 collection,
    /// when it is described with --allow-unsupported-server,
    /// then it is accepted, marked unsupported, and later calls use the server's own REST version.
    /// </summary>
    [Fact(DisplayName = "🖐️ Accepts an older server with --allow-unsupported-server, at its own API version")]
    public async Task Describe_Server2020Allowed_UsesItsVersion()
    {
        // Arrange
        _sut.Dispose();
        _sut = CreateAdapter(allowUnsupportedServer: true);
        _api.ApiVersion = "6.0";
        _api.Server("6.0");

        // Act
        TargetInfo info = await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);

        // Assert
        info.Detected.ShouldBe("Azure DevOps Server 2020 (unsupported)");
        info.ApiVersion.ShouldBe("6.0");
    }

    /// <summary>
    /// Given a server that lacks the Status policy type,
    /// when it is described,
    /// then the preflight fails naming the missing type and its id.
    /// </summary>
    [Fact(DisplayName = "👎 Fails the preflight when a policy type Setpoint manages is missing")]
    public async Task Describe_MissingPolicyType_FailsNamingIt()
    {
        // Arrange
        string projectId = Guid.NewGuid().ToString();
        _api.ConnectionData("Setpoint Service", @"CORP\svc-setpoint").PolicyArea("7.0").Projects((projectId, "Payments"))
            .PolicyTypes(projectId, leaveOut: AzureDevOpsPolicyTypes.Status);

        // Act
        var exception = await Should.ThrowAsync<NotSupportedException>(() => _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain($"Status ({AzureDevOpsPolicyTypes.Status})");
    }

    /// <summary>
    /// Given a collection with no projects,
    /// when it is described,
    /// then the preflight passes, since there is nothing to check policy types against.
    /// </summary>
    [Fact(DisplayName = "🖐️ Passes the preflight on a collection with no projects")]
    public async Task Describe_NoProjects_Passes()
    {
        // Arrange
        _api.ConnectionData("Setpoint Service", @"CORP\svc-setpoint").PolicyArea("7.0").Projects();

        // Act
        TargetInfo info = await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);

        // Assert
        info.Detected.ShouldBe("Azure DevOps Server 2022");
    }

    /// <summary>
    /// Given a target whose tokenEnv names another variable,
    /// when it is described,
    /// then the PAT comes from that variable.
    /// </summary>
    [Fact(DisplayName = "👍 Reads the PAT from tokenEnv when the target names one")]
    public async Task Describe_TokenEnv_UsesThatVariable()
    {
        // Arrange
        _environment.Clear();
        _environment["ADO_READONLY_PAT"] = _token;
        _api.Server();

        // Act
        TargetInfo info = await _sut.DescribeAsync(Target(d => d with { TokenEnv = "ADO_READONLY_PAT" }), TestContext.Current.CancellationToken);

        // Assert
        info.Identity.ShouldBe(@"Setpoint Service (CORP\svc-setpoint)");
    }

    /// <summary>
    /// Given no PAT in the environment,
    /// when the target is described,
    /// then it fails naming the variable to set, before any request is sent.
    /// </summary>
    [Fact(DisplayName = "👎 Fails the preflight when the token variable isn't set")]
    public async Task Describe_NoToken_FailsNamingTheVariable()
    {
        // Arrange
        _environment.Clear();

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain(TokenVariable);
        _server.LogEntries.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a PAT Azure DevOps doesn't accept, answered with its sign-in page,
    /// when the target is described,
    /// then the preflight fails saying the token wasn't accepted, without leaking the token.
    /// </summary>
    [Fact(DisplayName = "👎 Fails the preflight when Azure DevOps answers with its sign-in page")]
    public async Task Describe_SignInPage_FailsAsRejectedToken()
    {
        // Arrange
        _api.SignInPage();

        // Act
        var exception = await Should.ThrowAsync<HttpApiException>(() => _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("didn't accept the token");
        exception.Message.ShouldNotContain(_token);
    }

    /// <summary>
    /// Given connection data that signs Setpoint in as the anonymous identity,
    /// when the target is described,
    /// then the preflight fails, because no identity was resolved.
    /// </summary>
    [Fact(DisplayName = "👎 Fails the preflight when Azure DevOps signs Setpoint in anonymously")]
    public async Task Describe_Anonymous_Fails()
    {
        // Arrange
        _api.ConnectionData("Anonymous", "Anonymous", id: AzureDevOpsEndpoint.AnonymousId);

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("anonymously");
    }

    /// <summary>
    /// Given an Azure DevOps target with GitHub's auth method,
    /// when it is described,
    /// then it fails naming the methods Azure DevOps has.
    /// </summary>
    [Theory(DisplayName = "👎 Refuses auth methods Azure DevOps doesn't have")]
    [InlineData(AuthMethod.App)]
    public async Task Describe_OtherAuth_IsNotSupported(AuthMethod auth)
    {
        // Act
        var exception = await Should.ThrowAsync<NotSupportedException>(() =>
            _sut.DescribeAsync(Target(d => d with { Auth = auth }), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("use pat");
    }

    /// <summary>
    /// Given a target with no scope, and repos in two projects,
    /// when repos are listed,
    /// then every repo in the collection is listed, by project and name.
    /// </summary>
    [Fact(DisplayName = "👍 Lists the whole collection when the target has no scope")]
    public async Task ListRepos_NoScope_ListsWholeCollection()
    {
        // Arrange
        _api.Server().Repositories(
            new AdoRepoBuilder().InProject("Payments").WithName("ledger").Build(),
            new AdoRepoBuilder().InProject("Identity").WithName("login").Build(),
            new AdoRepoBuilder().InProject("Payments").WithName("api").Build());
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        List<ListedRepo> listed = await ListAsync(target);

        // Assert
        listed.Select(r => r.Repo.ToString()).ShouldBe(["ado-onprem/Identity/login", "ado-onprem/Payments/api", "ado-onprem/Payments/ledger"]);
        listed.ShouldAllBe(r => r.Skipped == null);
    }

    /// <summary>
    /// Given repos in and out of the target's scope and exclude,
    /// when repos are listed,
    /// then only repos in scope and not excluded are returned.
    /// </summary>
    [Fact(DisplayName = "👍 Lists only repos in scope and outside exclude")]
    public async Task ListRepos_ScopeAndExclude_KeepsOnlyThoseInScope()
    {
        // Arrange
        _api.Server().Repositories(
            new AdoRepoBuilder().InProject("Payments").WithName("ledger").Build(),
            new AdoRepoBuilder().InProject("Payments").WithName("ledger-sandbox").Build(),
            new AdoRepoBuilder().InProject("Identity").WithName("login").Build());
        Target target = Target(d => d with { Scope = ["Payments/*"], Exclude = ["*/*-sandbox"] });
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        List<ListedRepo> listed = await ListAsync(target);

        // Assert
        listed.Select(r => r.Repo.ToString()).ShouldBe(["ado-onprem/Payments/ledger"]);
    }

    /// <summary>
    /// Given disabled, in-maintenance, forked and empty repos,
    /// when repos are listed,
    /// then each is skipped with its code and severity.
    /// </summary>
    [Fact(DisplayName = "👎 Skips disabled, in-maintenance, forked and empty repos with their codes")]
    public async Task ListRepos_SkippableRepos_AreSkippedWithCodes()
    {
        // Arrange
        _api.Server().Repositories(
            new AdoRepoBuilder().InProject("Payments").WithName("old").Disabled().Build(),
            new AdoRepoBuilder().InProject("Payments").WithName("moving").InMaintenance().Build(),
            new AdoRepoBuilder().InProject("Payments").WithName("forked").Fork().Build(),
            new AdoRepoBuilder().InProject("Payments").WithName("empty").Empty().Build());
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        Dictionary<string, Diagnostic?> skipped = (await ListAsync(target)).ToDictionary(r => r.Repo.Name, r => r.Skipped);

        // Assert
        skipped["old"].ShouldNotBeNull().Code.ShouldBe(DiagnosticCodes.ArchivedRepoSkipped);
        skipped["old"]!.Severity.ShouldBe(DiagnosticSeverity.Info);
        skipped["moving"].ShouldNotBeNull().Code.ShouldBe(DiagnosticCodes.ArchivedRepoSkipped);
        skipped["forked"].ShouldNotBeNull().Code.ShouldBe(DiagnosticCodes.ForkSkipped);
        skipped["empty"].ShouldNotBeNull().Code.ShouldBe(DiagnosticCodes.EmptyRepoSkipped);
        skipped["empty"]!.Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    /// <summary>
    /// Given a fork and a target with includeForks: true,
    /// when repos are listed,
    /// then the fork is managed.
    /// </summary>
    [Fact(DisplayName = "🖐️ Manages forks when the target sets includeForks")]
    public async Task ListRepos_IncludeForks_ManagesForks()
    {
        // Arrange
        _api.Server().Repositories(new AdoRepoBuilder().InProject("Payments").WithName("forked").Fork().Build());
        Target target = Target(d => d with { IncludeForks = true });
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        List<ListedRepo> listed = await ListAsync(target);

        // Assert
        listed.ShouldHaveSingleItem().Skipped.ShouldBeNull();
    }

    /// <summary>
    /// Given a described target with repos,
    /// when it is described and listed,
    /// then every request was a read: the adapter sends nothing but GET and OPTIONS.
    /// </summary>
    [Fact(DisplayName = "🖐️ Sends only reads while describing and listing")]
    public async Task DescribeAndList_SendOnlyReads()
    {
        // Arrange
        _api.Server().Repositories(new AdoRepoBuilder().Build());
        Target target = Target();

        // Act
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        await ListAsync(target);

        // Assert
        _server.LogEntries.Select(e => e.RequestMessage!.Method).Distinct().ShouldBeSubsetOf(["GET", "OPTIONS"]);
    }

    /// <summary>
    /// Given a Server 2022 collection with repos,
    /// when it is described and listed,
    /// then every REST call pins api-version=7.0, and only the two discovery calls go without one.
    /// </summary>
    [Fact(DisplayName = "👍 Pins api-version 7.0 on every REST call")]
    public async Task DescribeAndList_PinApiVersion()
    {
        // Arrange
        _api.Server().Repositories(new AdoRepoBuilder().Build());
        Target target = Target();

        // Act
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        await ListAsync(target);

        // Assert
        var requests = _server.LogEntries.Select(e => e.RequestMessage!).ToList();
        requests.Count.ShouldBe(5);
        requests.Where(r => r.Method == "GET" && !r.Path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
            .ShouldAllBe(r => r.Query != null && r.Query["api-version"].Single() == "7.0");
    }

    /// <summary>
    /// Given a target that hasn't been described,
    /// when its repos are listed,
    /// then listing fails, because there is no session for it.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses to list a target that hasn't been described")]
    public async Task ListRepos_NotDescribed_Throws()
    {
        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ListAsync(Target()));

        // Assert
        exception.Message.ShouldContain("hasn't been described");
    }

    private AzureDevOpsAdapter CreateAdapter(bool allowUnsupportedServer) => new(new AzureDevOpsAdapterOptions
    {
        GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
        Http = new HttpCoreOptions { MaxRetries = 1, BaseDelay = TimeSpan.FromMilliseconds(1) },
        AllowUnsupportedServer = allowUnsupportedServer,
    });

    private Target Target(Func<TargetDefinition, TargetDefinition>? change = null)
    {
        var definition = new TargetDefinition { Platform = Platform.AzureDevOps, Url = _api.CollectionUrl };
        return new Target(TargetName, change?.Invoke(definition) ?? definition);
    }

    private async Task<List<ListedRepo>> ListAsync(Target target)
    {
        var listed = new List<ListedRepo>();
        await foreach (ListedRepo repo in _sut.ListReposAsync(target, TestContext.Current.CancellationToken))
        {
            listed.Add(repo);
        }

        return listed;
    }
}
