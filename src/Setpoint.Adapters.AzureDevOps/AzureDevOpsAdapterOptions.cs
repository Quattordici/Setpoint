using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Setpoint.Http;

namespace Setpoint.Adapters.AzureDevOps;

/// <summary>How the Azure DevOps adapter reaches its targets.</summary>
public sealed record AzureDevOpsAdapterOptions
{
    /// <summary>Retries, concurrency, timeouts and recording for each target's HTTP client.</summary>
    public HttpCoreOptions Http { get; init; } = new();

    /// <summary>Where request logs go. They are redacted.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>Reads an environment variable. Tokens only ever come from here; tests pass their own.</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The clock for retry back-off.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Creates the Entra ID credential for <c>auth: entra</c>. By default workload identity federation, managed
    /// identity, then the Azure CLI login; tests pass their own.
    /// </summary>
    public Func<Azure.Core.TokenCredential>? CreateEntraCredential { get; init; }

    /// <summary>
    /// The innermost handler for <c>auth: integrated</c> targets. By default one that signs in with the Windows
    /// account Setpoint runs as; tests pass their own.
    /// </summary>
    public Func<HttpMessageHandler>? CreateIntegratedHandler { get; init; }

    /// <summary>
    /// <c>--allow-unsupported-server</c>: accept Azure DevOps Server releases older than 2022, using the newest REST
    /// version they have instead of the pinned 7.0.
    /// </summary>
    public bool AllowUnsupportedServer { get; init; }
}
