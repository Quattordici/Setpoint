using System.CommandLine;
using System.Text.Json;
using Setpoint.Cli.Composition;
using Setpoint.Cli.Rendering;
using Setpoint.Core;
using Setpoint.Core.Applying;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Providers;
using Setpoint.Http;

namespace Setpoint.Cli.Commands;

/// <summary>
/// <c>setpoint apply</c>: apply a saved plan file, or re-plan and apply in one step with <c>--yes</c>.
/// </summary>
internal static class ApplyCommand
{
    public static Command Create(CliConsole console, CliServices services)
    {
        var planFile = new Argument<FileInfo?>("plan-file")
        {
            Description = "A plan file saved by 'setpoint plan --out'. Leave it out to re-plan, which needs --yes.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var yes = new Option<bool>("--yes", "-y")
        {
            Description = "Confirm a bare apply, which re-plans and applies without a saved plan.",
        };
        Option<DirectoryInfo> policy = CommonOptions.Policy();
        Option<int> maxChanges = CommonOptions.MaxChanges();
        Option<bool> failFast = CommonOptions.FailFast();
        Option<bool> strict = CommonOptions.Strict();
        Option<bool> verbose = CommonOptions.Verbose();
        Option<OutputFormat?> output = CommonOptions.Output();
        Option<bool> skipUnreachable = CommonOptions.SkipUnreachable();
        Option<bool> allowUnsupportedServer = CommonOptions.AllowUnsupportedServer();
        Option<DirectoryInfo?> record = CommonOptions.Record();

        var command = new Command("apply", "Apply a saved plan, or re-plan and apply with --yes.")
        {
            planFile,
            policy,
            yes,
            maxChanges,
            failFast,
            strict,
            verbose,
            output,
            skipUnreachable,
            allowUnsupportedServer,
            record,
        };

        command.Validators.Add(result =>
        {
            if (result.GetValue(planFile) is null && !result.GetValue(yes))
            {
                result.AddError("Give a plan file, or pass --yes to re-plan and apply in one step.");
            }
        });

        command.SetAction(async (result, cancellationToken) => (int)await RunAsync(
            console,
            services,
            new ApplyArguments(
                result.GetValue(planFile),
                result.GetValue(policy)!,
                OutputFormatResolver.Resolve(result.GetValue(output), console.Out),
                new ApplyOptions(result.GetValue(maxChanges), result.GetValue(failFast), result.GetValue(strict), result.GetValue(skipUnreachable)),
                result.GetValue(allowUnsupportedServer),
                result.GetValue(verbose),
                result.GetValue(record)),
            cancellationToken));
        return command;
    }

    /// <summary>Loads the policy and the plan (or makes one), applies it, prints the result, and returns the exit code.</summary>
    internal static async Task<ExitCode> RunAsync(CliConsole console, CliServices services, ApplyArguments arguments, CancellationToken cancellationToken)
    {
        (PolicyLoadResult load, IReadOnlyList<Diagnostic> policyDiagnostics) = PolicyGate.Check(arguments.Policy, arguments.Options.Strict);
        if (load.Repo is null || policyDiagnostics.HasErrors())
        {
            DiagnosticRenderer.Write(console, policyDiagnostics, arguments.Format);
            console.ErrorWriter.WriteLine("The policy is invalid, so no target was contacted. Run 'setpoint validate' for details.");
            return ExitCode.InvalidPolicyOrUsage;
        }

        PlanDocument? saved = null;
        if (arguments.PlanFile is { } file)
        {
            try
            {
                saved = PlanFile.Deserialize(await File.ReadAllTextAsync(file.FullName, cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                console.ErrorWriter.WriteLine($"Can't read the plan file {file.FullName}: {ex.Message}");
                return ExitCode.InvalidPolicyOrUsage;
            }
        }

        IReadOnlyList<IProviderAdapter> adapters = services.CreateAdapters(new AdapterSettings(
            new HttpCoreOptions { RecordDirectory = arguments.Record?.FullName },
            arguments.AllowUnsupportedServer));
        try
        {
            PlanDocument plan;
            if (saved is not null)
            {
                plan = saved;
            }
            else
            {
                // A bare apply plans first, exactly as plan would, and stops where plan would refuse to save.
                var planner = new Planner(adapters, ProviderBlocks.All, services.Time);
                var planOptions = new PlanOptions(arguments.Options.Strict, arguments.Options.SkipUnreachable, arguments.AllowUnsupportedServer, arguments.Options.MaxChanges);
                PlanResult planned = await planner.PlanAsync(new PlanInput(load.Repo, load.Sources, PolicyCommit.Find(arguments.Policy.FullName)), planOptions, cancellationToken);
                DiagnosticRenderer.Write(console, [.. policyDiagnostics, .. planned.RunDiagnostics], arguments.Format);
                PlanRenderer.Write(console, planned.Document, arguments.Format, arguments.Verbose, savedTo: null);
                if (planned.HasErrors)
                {
                    console.ErrorWriter.WriteLine("The plan has errors, so nothing was applied.");
                    return planned.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Code[2] == '0') ? ExitCode.InvalidPolicyOrUsage : ExitCode.RuntimeFailure;
                }

                plan = planned.Document;
            }

            if (!plan.HasChanges)
            {
                console.OutWriter.WriteLine("Nothing to apply: every repo is in sync.");
                return ExitCode.Success;
            }

            ApplyResultSet result = await new Applier(adapters).ApplyAsync(plan, load.Repo, load.Sources, arguments.Options, cancellationToken);
            DiagnosticRenderer.Write(console, result.Diagnostics, arguments.Format);
            ApplyRenderer.Write(console, result);
            return result.ExitCode;
        }
        finally
        {
            foreach (IDisposable adapter in adapters.OfType<IDisposable>())
            {
                adapter.Dispose();
            }
        }
    }
}

/// <summary>The parsed arguments of <c>setpoint apply</c>.</summary>
internal sealed record ApplyArguments(
    FileInfo? PlanFile,
    DirectoryInfo Policy,
    OutputFormat Format,
    ApplyOptions Options,
    bool AllowUnsupportedServer = false,
    bool Verbose = false,
    DirectoryInfo? Record = null);
