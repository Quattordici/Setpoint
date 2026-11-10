using Setpoint.Cli.Rendering;
using Setpoint.Core;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Providers;
using Setpoint.Http;

namespace Setpoint.Cli.Composition;

/// <summary>
/// The steps <c>plan</c> and <c>drift</c> share: check the policy, create the adapters, plan every target and
/// dispose the adapters. Neither command writes to a target.
/// </summary>
internal static class PlanRun
{
    /// <summary>
    /// Plans every target. An invalid policy is reported here, with no target contacted, and comes back as
    /// a run with no <see cref="PlanRunResult.Result"/>.
    /// </summary>
    /// <param name="toError">Write the policy diagnostics to standard error, because standard output carries JSON.</param>
    public static async Task<PlanRunResult> RunAsync(
        CliConsole console,
        CliServices services,
        DirectoryInfo policy,
        PlanOptions options,
        DirectoryInfo? record,
        OutputFormat format,
        bool toError,
        CancellationToken cancellationToken)
    {
        (PolicyLoadResult load, IReadOnlyList<Diagnostic> policyDiagnostics) = PolicyGate.Check(policy, options.Strict);
        if (load.Repo is null || policyDiagnostics.HasErrors())
        {
            DiagnosticRenderer.Write(console, policyDiagnostics, format, toError);
            console.ErrorWriter.WriteLine("The policy is invalid, so no target was contacted. Run 'setpoint validate' for details.");
            return new PlanRunResult(null, policyDiagnostics);
        }

        IReadOnlyList<IProviderAdapter> adapters = services.CreateAdapters(new AdapterSettings(
            new HttpCoreOptions { RecordDirectory = record?.FullName },
            options.AllowUnsupportedServer));
        try
        {
            var planner = new Planner(adapters, ProviderBlocks.All, services.Time);
            var input = new PlanInput(load.Repo, load.Sources, PolicyCommit.Find(policy.FullName));
            return new PlanRunResult(await planner.PlanAsync(input, options, cancellationToken), policyDiagnostics);
        }
        finally
        {
            foreach (IDisposable adapter in adapters.OfType<IDisposable>())
            {
                adapter.Dispose();
            }
        }
    }

    /// <summary>
    /// The exit code for a run with errors: problems with the policy or the selectors exit 2, like validate;
    /// problems reaching or reading live state exit 1.
    /// </summary>
    public static ExitCode ErrorExitCode(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Code[2] == '0')
            ? ExitCode.InvalidPolicyOrUsage
            : ExitCode.RuntimeFailure;
}

/// <summary>A finished plan run.</summary>
/// <param name="Result">The plan, or null when the policy was invalid and no target was contacted.</param>
/// <param name="PolicyDiagnostics">What loading and validating the policy found, after <c>--strict</c>.</param>
internal sealed record PlanRunResult(PlanResult? Result, IReadOnlyList<Diagnostic> PolicyDiagnostics)
{
    /// <summary>Every diagnostic: the policy's first, then the plan's.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => Result is null ? PolicyDiagnostics : [.. PolicyDiagnostics, .. Result.Diagnostics];
}
