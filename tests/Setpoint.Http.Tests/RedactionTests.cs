using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Setpoint.Http.Tests.TestSupport;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Setpoint.Http.Tests;

/// <summary>
/// Tests that secrets never reach logs or recordings: <see cref="SecretRedactor"/> itself, the logging handler at
/// trace level, and <c>--record</c> mapping files, which must also replay in WireMock.
/// </summary>
public sealed class RedactionTests : IAsyncLifetime
{
    private WireMockServer _api = null!;
    private readonly DirectoryInfo _recordings = Directory.CreateTempSubdirectory("setpoint-record-");

    public async ValueTask InitializeAsync() => _api = await WireMockServerFactory.CreateAsync("platform-api", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        WireMockServerFactory.Dispose(_api);
        _recordings.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given a registered token,
    /// when text containing it, or its basic-auth form, is redacted,
    /// then neither survives.
    /// </summary>
    [Fact(DisplayName = "👍 Masks a registered secret and its basic-auth form")]
    public void Redact_RegisteredSecret_IsMasked()
    {
        // Arrange
        string pat = TestTokens.AzureDevOpsPat();
        string basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + pat));
        var redactor = new SecretRedactor();
        redactor.Add(pat);

        // Act
        string redacted = redactor.Redact($"token={pat} header=Basic {basic}");

        // Assert
        redacted.ShouldNotContain(pat);
        redacted.ShouldNotContain(basic);
    }

    /// <summary>
    /// Given GitHub tokens and a JWT nobody registered,
    /// when text is redacted,
    /// then they are masked by shape.
    /// </summary>
    [Fact(DisplayName = "👍 Masks token-shaped values nobody registered")]
    public void Redact_TokenShapes_AreMasked()
    {
        // Arrange
        string github = TestTokens.GitHubPat();
        string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiIxMjM0NSJ9.c2lnbmF0dXJlLXNpZ25hdHVyZQ";
        var redactor = new SecretRedactor();

        // Act
        string redacted = redactor.Redact($"{github} and {jwt}");

        // Assert
        redacted.ShouldBe($"{SecretRedactor.Mask} and {SecretRedactor.Mask}");
    }

    /// <summary>
    /// Given a value shorter than four characters,
    /// when it is registered,
    /// then it is ignored, so it can't mask ordinary text.
    /// </summary>
    [Fact(DisplayName = "🖐️ Ignores very short secrets")]
    public void Add_ShortValue_IsIgnored()
    {
        // Arrange
        var redactor = new SecretRedactor();

        // Act
        redactor.Add("ab");

        // Assert
        redactor.Redact("about").ShouldBe("about");
    }

    /// <summary>
    /// Given trace logging and a request carrying a token in its Authorization header and in its query,
    /// when it is sent,
    /// then no log line contains the token.
    /// </summary>
    [Fact(DisplayName = "👎 Never logs Authorization headers or tokens")]
    public async Task Send_WithToken_LogsNoSecret()
    {
        // Arrange
        WireMockServerFactory.Reset(_api);
        _api.Given(Request.Create().UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Set-Cookie", "session=abc123secret"));
        string token = TestTokens.GitHubPat();
        var logger = new FakeLogger();
        logger.ControlLevel(LogLevel.Trace, enabled: true);
        var redactor = new SecretRedactor();
        redactor.Add(token);
        using HttpClient client = TargetHttpClientFactory.Create(
            "github", new Uri(_api.Url!), new HttpCoreOptions(), logger, redactor, [new StaticTokenHandler(token)]);

        // Act
        using HttpResponseMessage response = await client.GetAsync($"/user?access_token={token}", TestContext.Current.CancellationToken);

        // Assert
        string logs = string.Join("\n", logger.Collector.GetSnapshot().Select(r => r.Message));
        logs.ShouldContain("Authorization: ***");
        logs.ShouldContain("GET");
        logs.ShouldNotContain(token);
        logs.ShouldNotContain("abc123secret");
    }

    /// <summary>
    /// Given --record and a response that echoes the token,
    /// when a request is sent,
    /// then a WireMock mapping file is written with no token in it, and WireMock replays it.
    /// </summary>
    [Fact(DisplayName = "👍 Records redacted fixtures that WireMock can replay")]
    public async Task Send_WithRecording_WritesReplayableRedactedMapping()
    {
        // Arrange
        WireMockServerFactory.Reset(_api);
        string token = TestTokens.AzureDevOpsPat();
        _api.Given(Request.Create().WithPath("/DefaultCollection/_apis/policy/types").WithParam("api-version", "7.0").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"count":1,"value":[{"id":"fa4e907d","echo":"{{token}}"}]}"""));
        var redactor = new SecretRedactor();
        redactor.Add(token);
        using HttpClient client = TargetHttpClientFactory.Create(
            "ado", new Uri(_api.Url!), new HttpCoreOptions { RecordDirectory = _recordings.FullName }, NullLoggerInstance(), redactor, [new StaticTokenHandler(token)]);
        await client.GetAsync("/DefaultCollection/_apis/policy/types?api-version=7.0", TestContext.Current.CancellationToken);

        // Act
        FileInfo mapping = _recordings.GetFiles("*.json").ShouldHaveSingleItem();
        string recorded = await File.ReadAllTextAsync(mapping.FullName, TestContext.Current.CancellationToken);
        WireMockServer replay = await WireMockServerFactory.CreateAsync("replay", TestContext.Current.CancellationToken);
        try
        {
            replay.ReadStaticMappingAndAddOrUpdate(mapping.FullName);
            using var replayClient = new HttpClient { BaseAddress = new Uri(replay.Url!) };
            string replayed = await replayClient.GetStringAsync("/DefaultCollection/_apis/policy/types?api-version=7.0", TestContext.Current.CancellationToken);

            // Assert
            recorded.ShouldNotContain(token);
            mapping.Name.ShouldStartWith("ado-0001-get-");
            replayed.ShouldContain("fa4e907d");
            replayed.ShouldContain(SecretRedactor.Mask);
        }
        finally
        {
            WireMockServerFactory.Dispose(replay);
        }
    }

    private static ILogger NullLoggerInstance() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}
