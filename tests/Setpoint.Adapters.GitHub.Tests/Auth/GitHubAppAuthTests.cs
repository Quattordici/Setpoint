using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Setpoint.Adapters.GitHub.Tests.TestData;
using Setpoint.Adapters.GitHub.Tests.TestSupport;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;
using Setpoint.Http.Tests.TestSupport;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Setpoint.Adapters.GitHub.Tests.Auth;

/// <summary>
/// Tests for <c>auth: app</c> on GitHub targets against a stubbed API: the key from SETPOINT_&lt;TARGET&gt;_APP_KEY, finding
/// the installation for the scope's owner, minting and refreshing installation tokens, listing through
/// <c>/installation/repositories</c>, and keeping the key and tokens out of the logs.
/// </summary>
public sealed class GitHubAppAuthTests : IAsyncLifetime
{
    private const string Root = "/api/v3";
    private const long InstallationId = 77;

    private readonly RSA _key = RSA.Create(2048);
    private readonly long _appId = new Bogus.Randomizer().Long(1, 9_999_999);
    private readonly string _firstToken = "ghs_" + new Bogus.Randomizer().AlphaNumeric(36);
    private readonly string _secondToken = "ghs_" + new Bogus.Randomizer().AlphaNumeric(36);
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 20, 0, 0, TimeSpan.Zero));
    private readonly FakeLogger _logger = new(new FakeLogCollector(), "setpoint");
    private readonly Dictionary<string, string> _environment = [];
    private WireMockServer _server = null!;
    private GitHubAdapter _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("github-app", TestContext.Current.CancellationToken);
        _environment["SETPOINT_GHE_APP_KEY"] = _key.ExportRSAPrivateKeyPem();
        _logger.ControlLevel(LogLevel.Trace, true);
        _sut = new GitHubAdapter(new GitHubAdapterOptions
        {
            GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
            Http = new HttpCoreOptions { MaxRetries = 1, BaseDelay = TimeSpan.FromMilliseconds(1) },
            Logger = _logger,
            Time = _time,
        });
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        _key.Dispose();
        WireMockServerFactory.Dispose(_server);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given an App installed on the scope's owner,
    /// when the target is described,
    /// then the App signs in with its JWT and reports itself as its bot account.
    /// </summary>
    [Fact(DisplayName = "👍 Describes an App target as the App's bot account")]
    public async Task Describe_AppInstalled_ReportsBotIdentity()
    {
        // Arrange
        StubApp();

        // Act
        TargetInfo info = await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);

        // Assert
        info.Identity.ShouldBe("setpoint-governance[bot]");
    }

    /// <summary>
    /// Given an App target,
    /// when repos are listed,
    /// then they come from /installation/repositories, signed with a freshly minted installation token.
    /// </summary>
    [Fact(DisplayName = "👍 Lists repos through the installation with an installation token")]
    public async Task ListRepos_App_UsesInstallationRepositories()
    {
        // Arrange
        StubApp();
        StubInstallationRepos(_firstToken, "payments");
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        List<ListedRepo> listed = await ListAsync(target);

        // Assert
        listed.ShouldHaveSingleItem().Repo.ShouldBe(new RepoRef("ghe", "acme", "payments"));
        MintCount().ShouldBe(1);
    }

    /// <summary>
    /// Given an installation token that is about to expire in a long run,
    /// when another request is made,
    /// then a new token is minted first and the request uses it.
    /// </summary>
    [Fact(DisplayName = "🖐️ Mints a new installation token before the old one expires")]
    public async Task ListRepos_TokenNearExpiry_RefreshesFirst()
    {
        // Arrange
        StubApp();
        StubInstallationRepos(_firstToken, "payments");
        StubInstallationRepos(_secondToken, "payments");
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        await ListAsync(target);

        // Act
        _time.Advance(TimeSpan.FromMinutes(56));
        List<ListedRepo> listed = await ListAsync(target);

        // Assert
        listed.ShouldHaveSingleItem();
        MintCount().ShouldBe(2);
        _server.LogEntries.Last().RequestMessage!.Headers!["Authorization"].ShouldBe([$"Bearer {_secondToken}"]);
    }

    /// <summary>
    /// Given an App that isn't installed on the scope's owner,
    /// when the target is described,
    /// then the preflight fails naming the owner.
    /// </summary>
    [Fact(DisplayName = "👎 Fails the preflight when the App isn't installed on the owner")]
    public async Task Describe_NotInstalled_FailsNamingOwner()
    {
        // Arrange
        _server.Given(Request.Create().WithPath($"{Root}/app").UsingGet()).RespondWith(Json(new JsonObject { ["slug"] = "setpoint-governance" }));
        _server.Given(Request.Create().WithPath($"{Root}/orgs/acme/installation").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath($"{Root}/users/acme/installation").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("isn't installed on 'acme'");
    }

    /// <summary>
    /// Given no private key in the environment,
    /// when the target is described,
    /// then it fails naming SETPOINT_GHE_APP_KEY, before any request.
    /// </summary>
    [Fact(DisplayName = "👎 Fails the preflight when the App key variable isn't set")]
    public async Task Describe_NoKey_FailsNamingVariable()
    {
        // Arrange
        _environment.Clear();

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("SETPOINT_GHE_APP_KEY");
        _server.LogEntries.ShouldBeEmpty();
    }

    /// <summary>
    /// Given trace logging, which includes headers,
    /// when an App target is described and listed,
    /// then no log line holds the key, the JWT or an installation token.
    /// </summary>
    [Fact(DisplayName = "👍 Never logs the key, the JWT or the installation tokens")]
    public async Task Logs_AppFlow_HoldNoSecrets()
    {
        // Arrange
        StubApp();
        StubInstallationRepos(_firstToken, "payments");
        Target target = Target();

        // Act
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        await ListAsync(target);

        // Assert
        string logs = string.Join("\n", _logger.Collector.GetSnapshot().Select(r => r.Message));
        logs.ShouldNotBeEmpty();
        logs.ShouldNotContain(_firstToken);
        logs.ShouldNotContain("eyJ");
        logs.ShouldNotContain("PRIVATE KEY");
    }

    private Target Target() => new("ghe", new TargetDefinition
    {
        Platform = Platform.GitHub,
        Url = new Uri(_server.Urls[0]),
        Auth = AuthMethod.App,
        AppId = _appId,
        Scope = ["acme/*"],
    });

    private void StubApp()
    {
        _server.Given(Request.Create().WithPath($"{Root}/app").UsingGet().WithHeader("Authorization", "Bearer eyJ*"))
            .RespondWith(Json(new JsonObject { ["slug"] = "setpoint-governance", ["id"] = _appId }));
        _server.Given(Request.Create().WithPath($"{Root}/orgs/acme/installation").UsingGet().WithHeader("Authorization", "Bearer eyJ*"))
            .RespondWith(Json(new JsonObject { ["id"] = InstallationId, ["account"] = new JsonObject { ["login"] = "acme" } }));

        // The first mint gives the first token; any later one the second.
        _server.Given(Request.Create().WithPath($"{Root}/app/installations/{InstallationId}/access_tokens").UsingPost().WithHeader("Authorization", "Bearer eyJ*"))
            .InScenario("mint").WillSetStateTo("minted")
            .RespondWith(Json(Token(_firstToken), status: 201));
        _server.Given(Request.Create().WithPath($"{Root}/app/installations/{InstallationId}/access_tokens").UsingPost().WithHeader("Authorization", "Bearer eyJ*"))
            .InScenario("mint").WhenStateIs("minted")
            .RespondWith(Json(Token(_secondToken), status: 201));
    }

    private void StubInstallationRepos(string token, params string[] names) =>
        _server.Given(Request.Create().WithPath($"{Root}/installation/repositories").UsingGet().WithHeader("Authorization", $"Bearer {token}"))
            .RespondWith(Json(new JsonObject
            {
                ["total_count"] = names.Length,
                ["repositories"] = new JsonArray([.. names.Select(n => (JsonNode)new GitHubRepoBuilder().WithOwner("acme").WithName(n).Build())]),
            }));

    private JsonObject Token(string token) => new()
    {
        ["token"] = token,
        ["expires_at"] = _time.GetUtcNow().AddHours(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
    };

    private int MintCount() => _server.LogEntries.Count(e => e.RequestMessage!.Method == "POST");

    private async Task<List<ListedRepo>> ListAsync(Target target)
    {
        var listed = new List<ListedRepo>();
        await foreach (ListedRepo repo in _sut.ListReposAsync(target, TestContext.Current.CancellationToken))
        {
            listed.Add(repo);
        }

        return listed;
    }

    private static IResponseBuilder Json(JsonNode body, int status = 200) => Response.Create()
        .WithStatusCode(status)
        .WithHeader("Content-Type", "application/json; charset=utf-8")
        .WithBody(body.ToJsonString());
}
