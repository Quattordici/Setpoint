using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Setpoint.Http;

namespace Setpoint.Adapters.GitHub;

/// <summary>How the GitHub adapter reaches its targets.</summary>
public sealed record GitHubAdapterOptions
{
    /// <summary>Retries, concurrency, timeouts and recording for each target's HTTP client.</summary>
    public HttpCoreOptions Http { get; init; } = new();

    /// <summary>Where request logs go. They are redacted.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>Reads an environment variable. Tokens only ever come from here; tests pass their own.</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The clock for retry back-off.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}
