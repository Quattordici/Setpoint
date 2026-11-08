using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Validation;

namespace Setpoint.Cli.Composition;

/// <summary>Loads and validates a policy repo, the first step of validate, plan and apply.</summary>
internal static class PolicyGate
{
    /// <summary>The load result and every diagnostic from loading and validating, after <c>--strict</c>.</summary>
    public static (PolicyLoadResult Load, IReadOnlyList<Diagnostic> Diagnostics) Check(DirectoryInfo policyDirectory, bool strict)
    {
        PolicyLoadResult load = new PolicyRepoLoader(ProviderBlocks.All).LoadDirectory(policyDirectory.FullName);
        IEnumerable<Diagnostic> found = load.Diagnostics;
        if (load.Repo is not null)
        {
            found = found.Concat(new PolicyValidator(ProviderBlocks.All).Validate(load.Repo));
        }

        return (load, DiagnosticList.ApplyStrict(found, strict));
    }
}
