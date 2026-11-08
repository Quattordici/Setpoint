using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Setpoint.Http.Tests.TestSupport;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Setpoint.Http.Tests;

/// <summary>
/// Tests for the pipeline <see cref="TargetHttpClientFactory"/> builds, against a WireMock server:
/// which failures are retried, <c>Retry-After</c>, giving up, and the User-Agent.
/// </summary>
public sealed class TargetHttpClientFactoryTests : IAsyncLifetime
{
    private static readonly HttpCoreOptions s_fastRetries = new() { BaseDelay = TimeSpan.FromMilliseconds(5), MaxRetries = 3 };

    private readonly List<RetryAttempt> _retries = [];
    private WireMockServer _api = null!;

    public async ValueTask InitializeAsync() => _api = await WireMockServerFactory.CreateAsync("platform-api", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        WireMockServerFactory.Dispose(_api);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given a GET that fails with 503 once and then succeeds,
    /// when it is sent,
    /// then it is retried and the caller sees the success.
    /// </summary>
    [Fact(DisplayName = "👍 Retries a GET after a 5xx")]
    public async Task Send_GetAfter503_RetriesAndSucceeds()
    {
        // Arrange
        WireMockServerFactory.Reset(_api);
        _api.Given(Request.Create().WithPath("/repos").UsingGet()).InScenario("flaky").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503));
        _api.Given(Request.Create().WithPath("/repos").UsingGet()).InScenario("flaky").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));
        using HttpClient client = CreateClient(s_fastRetries);

        // Act
        using HttpResponseMessage response = await client.GetAsync("/repos", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _api.LogEntries.Count().ShouldBe(2);
    }

    /// <summary>
    /// Given a 429 with Retry-After: 1,
    /// when the request is sent,
    /// then Setpoint waits the second the server asked for before retrying.
    /// </summary>
    [Fact(DisplayName = "👍 Honours Retry-After on 429")]
    public async Task Send_429WithRetryAfter_WaitsAsAsked()
    {
        // Arrange
        WireMockServerFactory.Reset(_api);
        _api.Given(Request.Create().WithPath("/policy").UsingGet()).InScenario("throttled").WillSetStateTo("ok")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "1"));
        _api.Given(Request.Create().WithPath("/policy").UsingGet()).InScenario("throttled").WhenStateIs("ok")
            .RespondWith(Response.Create().WithStatusCode(200));
        using HttpClient client = CreateClient(s_fastRetries);

        // Act
        using HttpResponseMessage response = await client.GetAsync("/policy", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _retries.ShouldHaveSingleItem().ShouldBe(new RetryAttempt(1, TimeSpan.FromSeconds(1), 429));
    }

    /// <summary>
    /// Given a POST that fails with 500,
    /// when it is sent,
    /// then it is not retried, because the write may have half-happened.
    /// </summary>
    [Fact(DisplayName = "👎 Does not retry a POST after a 5xx")]
    public async Task Send_PostAfter500_IsNotRetried()
    {
        // Arrange
        WireMockServerFactory.Reset(_api);
        _api.Given(Request.Create().WithPath("/configurations").UsingPost()).RespondWith(Response.Create().WithStatusCode(500));
        using HttpClient client = CreateClient(s_fastRetries);

        // Act
        using HttpResponseMessage response = await client.PostAsync("/configurations", new StringContent("{}"), TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _api.LogEntries.Count().ShouldBe(1);
    }

    /// <summary>
    /// Given a POST throttled with 429 once,
    /// when it is sent,
    /// then it is retried, because a throttled request was never processed.
    /// </summary>
    [Fact(DisplayName = "🖐️ Retries a POST after a 429")]
    public async Task Send_PostAfter429_IsRetried()
    {
        // Arrange
        WireMockServerFactory.Reset(_api);
        _api.Given(Request.Create().WithPath("/configurations").UsingPost()).InScenario("busy").WillSetStateTo("free")
            .RespondWith(Response.Create().WithStatusCode(429));
        _api.Given(Request.Create().WithPath("/configurations").UsingPost()).InScenario("busy").WhenStateIs("free")
            .RespondWith(Response.Create().WithStatusCode(201));
        using HttpClient client = CreateClient(s_fastRetries);

        // Act
        using HttpResponseMessage response = await client.PostAsync("/configurations", new StringContent("{}"), TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// Given a GET that always fails with 502,
    /// when it is sent,
    /// then Setpoint gives up after the configured retries and returns the last failure.
    /// </summary>
    [Fact(DisplayName = "👎 Gives up after the configured retries")]
    public async Task Send_AlwaysFailing_GivesUpAfterMaxRetries()
    {
        // Arrange
        WireMockServerFactory.Reset(_api);
        _api.Given(Request.Create().WithPath("/repos").UsingGet()).RespondWith(Response.Create().WithStatusCode(502));
        using HttpClient client = CreateClient(s_fastRetries with { MaxRetries = 2 });

        // Act
        using HttpResponseMessage response = await client.GetAsync("/repos", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        _api.LogEntries.Count().ShouldBe(3);
    }

    /// <summary>
    /// Given a 404,
    /// when it is sent,
    /// then it is not retried, because the answer won't change.
    /// </summary>
    [Fact(DisplayName = "🖐️ Does not retry a 4xx other than 408 and 429")]
    public async Task Send_404_IsNotRetried()
    {
        // Arrange
        WireMockServerFactory.Reset(_api);
        _api.Given(Request.Create().WithPath("/missing").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        using HttpClient client = CreateClient(s_fastRetries);

        // Act
        using HttpResponseMessage response = await client.GetAsync("/missing", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _api.LogEntries.Count().ShouldBe(1);
    }

    /// <summary>
    /// Given any request,
    /// when it is sent,
    /// then it carries the Setpoint User-Agent.
    /// </summary>
    [Fact(DisplayName = "👍 Sends the Setpoint User-Agent")]
    public async Task Send_AnyRequest_SendsUserAgent()
    {
        // Arrange
        WireMockServerFactory.Reset(_api);
        _api.Given(Request.Create().WithPath("/").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        using HttpClient client = CreateClient(s_fastRetries);

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // Assert
        _api.LogEntries.ShouldHaveSingleItem().RequestMessage!.Headers!["User-Agent"].ToString().ShouldStartWith("Setpoint/");
    }

    /// <summary>
    /// Given a concurrency limit and many requests started at once,
    /// when they are sent,
    /// then no more than the limit are ever in flight together.
    /// </summary>
    [Theory(DisplayName = "👍 Keeps requests in flight within the per-target limit")]
    [InlineData(1)]
    [InlineData(HttpCoreOptions.DefaultMaxConcurrency)]
    public async Task Send_ManyAtOnce_StaysWithinLimit(int limit)
    {
        // Arrange
        var primary = new InFlightCountingHandler(TimeSpan.FromMilliseconds(30));
        using HttpClient client = TargetHttpClientFactory.Create(
            "target", new Uri("https://example.test/"), new HttpCoreOptions { MaxConcurrency = limit }, NullLogger.Instance, new SecretRedactor(), primaryHandler: primary);

        // Act
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => client.GetAsync($"/r/{i}", TestContext.Current.CancellationToken)));

        // Assert
        primary.MaxInFlight.ShouldBe(limit);
    }

    private HttpClient CreateClient(HttpCoreOptions options) =>
        TargetHttpClientFactory.Create(
            "target",
            new Uri(_api.Url!),
            options,
            NullLogger.Instance,
            new SecretRedactor(),
            onRetry: _retries.Add);
}
