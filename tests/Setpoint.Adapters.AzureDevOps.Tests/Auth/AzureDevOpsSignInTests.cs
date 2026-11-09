using Azure.Core;
using NSubstitute;
using Setpoint.Adapters.AzureDevOps.Tests.TestSupport;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;
using Setpoint.Http.Tests.TestSupport;
using WireMock.Server;

namespace Setpoint.Adapters.AzureDevOps.Tests.Auth;

/// <summary>
/// Tests for the Azure DevOps sign-in methods beyond PAT: the pipeline's job token, Windows auth on Server only,
/// and Entra ID on Services only. Each must resolve the identity the plan header shows.
/// </summary>
public sealed class AzureDevOpsSignInTests : IAsyncLifetime
{
    private readonly string _token = new Bogus.Randomizer().AlphaNumeric(64);
    private readonly Dictionary<string, string> _environment = [];
    private WireMockServer _server = null!;

    public async ValueTask InitializeAsync() =>
        _server = await WireMockServerFactory.CreateAsync("ado-api", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        WireMockServerFactory.Dispose(_server);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given auth: accessToken and the job token in SYSTEM_ACCESSTOKEN,
    /// when the target is described,
    /// then every call carries it as a bearer token and the identity is resolved.
    /// </summary>
    [Fact(DisplayName = "👍 Signs in with the pipeline's job token from SYSTEM_ACCESSTOKEN")]
    public async Task Describe_AccessToken_SendsBearer()
    {
        // Arrange
        _environment["SYSTEM_ACCESSTOKEN"] = _token;
        AzureDevOpsApiStub api = AzureDevOpsApiStub.Bearer(_server, _token);
        api.Server();
        using AzureDevOpsAdapter sut = Adapter();

        // Act
        TargetInfo info = await sut.DescribeAsync(Target(api, AuthMethod.AccessToken), TestContext.Current.CancellationToken);

        // Assert
        info.Identity.ShouldBe(@"Setpoint Service (CORP\svc-setpoint)");
    }

    /// <summary>
    /// Given auth: accessToken without SYSTEM_ACCESSTOKEN, as when the pipeline step doesn't map it,
    /// when the target is described,
    /// then it fails naming the variable and how to map it, before any request.
    /// </summary>
    [Fact(DisplayName = "👎 Says how to map SYSTEM_ACCESSTOKEN when the job token is missing")]
    public async Task Describe_AccessTokenMissing_SaysHowToMapIt()
    {
        // Arrange
        using AzureDevOpsAdapter sut = Adapter();

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.DescribeAsync(Target(new AzureDevOpsApiStub(_server, _token), AuthMethod.AccessToken), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("SYSTEM_ACCESSTOKEN: $(System.AccessToken)");
        _server.LogEntries.ShouldBeEmpty();
    }

    /// <summary>
    /// Given auth: integrated on a Server target,
    /// when it is described,
    /// then requests go through the Windows auth handler and carry no token of Setpoint's own.
    /// </summary>
    [Fact(DisplayName = "👍 Signs in to Server with Windows auth and sends no token")]
    public async Task Describe_Integrated_UsesWindowsHandler()
    {
        // Arrange
        var api = new AzureDevOpsApiStub(_server, _token) { RequiredHeader = ("X-Test-Windows-Auth", "negotiated") };
        api.Server();
        using AzureDevOpsAdapter sut = Adapter(createIntegratedHandler: () => new FakeWindowsAuthHandler());

        // Act
        TargetInfo info = await sut.DescribeAsync(Target(api, AuthMethod.Integrated), TestContext.Current.CancellationToken);

        // Assert
        info.Identity.ShouldBe(@"Setpoint Service (CORP\svc-setpoint)");
        _server.LogEntries.ShouldAllBe(e => !e.RequestMessage!.Headers!.ContainsKey("Authorization"));
    }

    /// <summary>
    /// Given auth: integrated on Services, or auth: entra on Server,
    /// when the target is described,
    /// then it is refused before any request, naming where the method works.
    /// </summary>
    [Theory(DisplayName = "👎 Refuses Windows auth on Services and Entra ID on Server")]
    [InlineData(AuthMethod.Integrated, "https://dev.azure.com/contoso", "Azure DevOps Server only")]
    [InlineData(AuthMethod.Entra, null, "Azure DevOps Services only")]
    public async Task Describe_WrongDeployment_IsRefused(AuthMethod auth, string? url, string expected)
    {
        // Arrange
        using AzureDevOpsAdapter sut = Adapter();
        Target target = Target(new AzureDevOpsApiStub(_server, _token), auth);
        if (url is not null)
        {
            target = target with { Definition = target.Definition with { Url = new Uri(url) } };
        }

        // Act
        var exception = await Should.ThrowAsync<NotSupportedException>(() => sut.DescribeAsync(target, TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain(expected);
        _server.LogEntries.ShouldBeEmpty();
    }

    private AzureDevOpsAdapter Adapter(Func<HttpMessageHandler>? createIntegratedHandler = null) => new(new AzureDevOpsAdapterOptions
    {
        GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
        Http = new HttpCoreOptions { MaxRetries = 1, BaseDelay = TimeSpan.FromMilliseconds(1) },
        CreateIntegratedHandler = createIntegratedHandler,
        CreateEntraCredential = () => Substitute.For<TokenCredential>(),
    });

    private static Target Target(AzureDevOpsApiStub api, AuthMethod auth) =>
        new("ado-onprem", new TargetDefinition { Platform = Platform.AzureDevOps, Url = api.CollectionUrl, Auth = auth });

    // Stands in for the Negotiate handshake: marks each request the way only the Windows auth handler would.
    private sealed class FakeWindowsAuthHandler : DelegatingHandler
    {
        public FakeWindowsAuthHandler()
            : base(new SocketsHttpHandler())
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Test-Windows-Auth", "negotiated");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
