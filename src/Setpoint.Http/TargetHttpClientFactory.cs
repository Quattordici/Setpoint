using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Setpoint.Http.Handlers;

namespace Setpoint.Http;

/// <summary>
/// Creates the one <see cref="HttpClient"/> an adapter uses for one target. From the outside in, a request passes
/// retry (429 and 5xx, honouring <c>Retry-After</c>), the per-target concurrency limit, the adapter's own handlers
/// (such as authentication), recording when enabled, redacted logging, and finally the platform's default handler.
/// TLS is the operating system's: certificate validation is never replaced, and there is no option to turn it off.
/// </summary>
public static class TargetHttpClientFactory
{
    /// <summary>The User-Agent every request sends.</summary>
    public static readonly string UserAgent =
        $"Setpoint/{typeof(TargetHttpClientFactory).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} (+https://github.com/Quattordici/Setpoint)";

    /// <summary>Creates the client for <paramref name="target"/>.</summary>
    /// <param name="target">The target name, used in logs and recording file names.</param>
    /// <param name="baseAddress">The target's API root.</param>
    /// <param name="options">Retry, concurrency, timeout and recording settings.</param>
    /// <param name="logger">Where request logs go.</param>
    /// <param name="redactor">Masks secrets in logs and recordings. Register the target's token with it.</param>
    /// <param name="adapterHandlers">The adapter's own handlers, outermost first, such as one that adds the Authorization header.</param>
    /// <param name="primaryHandler">The innermost handler. Tests pass a fake; production uses the platform default.</param>
    /// <param name="timeProvider">The clock for back-off delays. Tests can pass a fake one.</param>
    /// <param name="onRetry">Called before each retry, for progress output and tests.</param>
    public static HttpClient Create(
        string target,
        Uri baseAddress,
        HttpCoreOptions options,
        ILogger logger,
        SecretRedactor redactor,
        IEnumerable<DelegatingHandler>? adapterHandlers = null,
        HttpMessageHandler? primaryHandler = null,
        TimeProvider? timeProvider = null,
        Action<RetryAttempt>? onRetry = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrency, 1);

        var chain = new List<DelegatingHandler>
        {
            new ResilienceHandler(RetryPolicy.Build(options, timeProvider ?? TimeProvider.System, onRetry)),
            new ConcurrencyLimitHandler(options.MaxConcurrency),
        };
        chain.AddRange(adapterHandlers ?? []);
        if (options.RecordDirectory is { } directory)
        {
            chain.Add(new RecordingHandler(directory, target, redactor));
        }

        chain.Add(new LoggingHandler(logger, redactor, target));

        HttpMessageHandler pipeline = primaryHandler ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            chain[i].InnerHandler = pipeline;
            pipeline = chain[i];
        }

        var client = new HttpClient(pipeline, disposeHandler: true)
        {
            BaseAddress = baseAddress,

            // Each attempt has its own timeout in the retry pipeline; the client must not cut retries short.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
