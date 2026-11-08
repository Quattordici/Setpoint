using Setpoint.Core.Providers;

namespace Setpoint.Cli.Composition;

/// <summary>What the commands depend on beyond the console: the compiled-in adapters and the clock. Tests pass fakes.</summary>
/// <param name="Adapters">One adapter per platform.</param>
/// <param name="Time">The clock plan files are stamped with.</param>
internal sealed record CliServices(IReadOnlyList<IProviderAdapter> Adapters, TimeProvider Time)
{
    /// <summary>
    /// The production services. The GitHub and Azure DevOps adapters join the list as their build tickets land;
    /// until then plan reports their targets as unreachable.
    /// </summary>
    public static CliServices Default { get; } = new([], TimeProvider.System);
}
