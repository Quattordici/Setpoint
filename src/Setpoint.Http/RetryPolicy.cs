using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Setpoint.Http;

/// <summary>
/// The retry policy. 429 is retried for every method, because the server did nothing. 5xx, timeouts and
/// connection failures are retried only for idempotent methods: retrying a POST that may have half-worked
/// could create a duplicate policy. <c>Retry-After</c> is honoured, capped at <see cref="HttpCoreOptions.MaxRetryAfter"/>.
/// </summary>
internal static class RetryPolicy
{
    public static ResiliencePipeline<HttpResponseMessage> Build(HttpCoreOptions options, TimeProvider timeProvider, Action<RetryAttempt>? onRetry = null)
    {
        var retry = new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = options.MaxRetries,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = options.BaseDelay,
            ShouldRetryAfterHeader = true,
            MaxDelay = options.MaxRetryAfter,
            ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome, args.Context)),
            OnRetry = args =>
            {
                onRetry?.Invoke(new RetryAttempt(args.AttemptNumber + 1, args.RetryDelay, (int?)args.Outcome.Result?.StatusCode));
                return ValueTask.CompletedTask;
            },
        };

        return new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = timeProvider }
            .AddRetry(retry)
            .AddTimeout(options.AttemptTimeout)
            .Build();
    }

    private static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, ResilienceContext context)
    {
        if (outcome.Result is { StatusCode: HttpStatusCode.TooManyRequests })
        {
            return true;
        }

        HttpRequestMessage? request = context.GetRequestMessage();
        bool idempotent = request?.Method is { } method
            && (method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Put || method == HttpMethod.Delete || method == HttpMethod.Options);
        if (!idempotent)
        {
            return false;
        }

        return outcome switch
        {
            { Exception: HttpRequestException or TimeoutRejectedException } => true,
            { Result: { } response } => (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout,
            _ => false,
        };
    }
}

/// <summary>One retry, as reported to an observer: which retry it is, how long Setpoint waits, and why.</summary>
/// <param name="Retry">Which retry this is, counting the first retry as 1.</param>
/// <param name="Delay">The wait before it.</param>
/// <param name="StatusCode">The status code that caused the retry, if there was a response.</param>
public sealed record RetryAttempt(int Retry, TimeSpan Delay, int? StatusCode);
