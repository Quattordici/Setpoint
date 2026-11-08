using Setpoint.Adapters.AzureDevOps.Policy;
using Setpoint.Adapters.GitHub.Policy;
using Setpoint.Core.Providers;

namespace Setpoint.Cli.Composition;

/// <summary>The provider blocks of every compiled-in adapter.</summary>
internal static class ProviderBlocks
{
    public static readonly IReadOnlyList<IProviderBlockDefinition> All =
    [
        GitHubProviderBlockDefinition.Instance,
        AzureDevOpsProviderBlockDefinition.Instance,
    ];
}
