namespace Setpoint.Http;

/// <summary>Settings for one target's HTTP pipeline.</summary>
public sealed record HttpCoreOptions
{
    /// <summary>The default number of requests in flight per target.</summary>
    public const int DefaultMaxConcurrency = 4;

    /// <summary>How many requests to one target may be in flight at once. Retry back-off doesn't hold a slot.</summary>
    public int MaxConcurrency { get; init; } = DefaultMaxConcurrency;

    /// <summary>How many times a failed attempt is retried.</summary>
    public int MaxRetries { get; init; } = 4;

    /// <summary>The first back-off delay when the server gives no <c>Retry-After</c>; later ones grow exponentially, with jitter.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest Setpoint waits for a single attempt.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest Setpoint waits before a retry. A longer <c>Retry-After</c> is capped to this.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Dev-only: when set, every request and response is saved here, redacted, as a WireMock.Net mapping file.
    /// </summary>
    public string? RecordDirectory { get; init; }
}
