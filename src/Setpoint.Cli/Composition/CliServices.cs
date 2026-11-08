using Setpoint.Adapters.GitHub;
using Setpoint.Core.Providers;
using Setpoint.Http;

namespace Setpoint.Cli.Composition;

/// <summary>What the commands depend on beyond the console: the compiled-in adapters and the clock. Tests pass fakes.</summary>
/// <param name="CreateAdapters">Creates one adapter per platform for a run. Adapters that are disposable are disposed after it.</param>
/// <param name="Time">The clock plan files are stamped with.</param>
internal sealed record CliServices(Func<AdapterSettings, IReadOnlyList<IProviderAdapter>> CreateAdapters, TimeProvider Time)
{
    /// <summary>
    /// The production services. The Azure DevOps adapter joins the list when its build ticket lands; until then
    /// plan reports Azure DevOps targets as unreachable.
    /// </summary>
    public static CliServices Default { get; } = new(
        settings => [new GitHubAdapter(new GitHubAdapterOptions { Http = settings.Http })],
        TimeProvider.System);

    /// <summary>Services that hand every run the same adapters, for tests.</summary>
    public static CliServices With(IReadOnlyList<IProviderAdapter> adapters, TimeProvider time) => new(_ => adapters, time);
}

/// <summary>What a run tells the adapters it creates.</summary>
/// <param name="Http">Retries, concurrency and, with the hidden <c>--record</c> option, where to record HTTP exchanges.</param>
internal sealed record AdapterSettings(HttpCoreOptions Http);
