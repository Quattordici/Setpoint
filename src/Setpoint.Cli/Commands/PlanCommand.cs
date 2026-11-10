using System.CommandLine;
using Setpoint.Cli.Composition;
using Setpoint.Cli.Rendering;
using Setpoint.Core;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;

namespace Setpoint.Cli.Commands;

/// <summary><c>setpoint plan</c>: read live state and show the changes apply would make.</summary>
internal static class PlanCommand
{
    public static Command Create(CliConsole console, CliServices services)
    {
        Option<DirectoryInfo> policy = CommonOptions.Policy();
        var outFile = new Option<FileInfo>("--out")
        {
            Description = "Save the plan file here, for a later 'setpoint apply <file>'.",
            HelpName = "file",
        };
        var json = new Option<bool>("--json")
        {
            Description = "Write the plan to standard output as JSON, in the plan file schema.",
        };
        var exitCode = new Option<bool>("--exit-code")
        {
            Description = "Exit with code 5 when there are changes.",
        };
        Option<bool> strict = CommonOptions.Strict();
        Option<bool> verbose = CommonOptions.Verbose();
        Option<OutputFormat?> output = CommonOptions.Output();
        Option<bool> skipUnreachable = CommonOptions.SkipUnreachable();
        Option<bool> allowUnsupportedServer = CommonOptions.AllowUnsupportedServer();
        Option<int> maxChanges = CommonOptions.MaxChanges();
        Option<DirectoryInfo?> record = CommonOptions.Record();

        var command = new Command("plan", "Read live state and show the changes apply would make.")
        {
            policy,
            outFile,
            json,
            exitCode,
            strict,
            verbose,
            output,
            skipUnreachable,
            allowUnsupportedServer,
            maxChanges,
            record,
        };

        command.SetAction(async (result, cancellationToken) => (int)await RunAsync(
            console,
            services,
            new PlanArguments(
                result.GetValue(policy)!,
                result.GetValue(outFile),
                result.GetValue(json),
                result.GetValue(exitCode),
                result.GetValue(verbose),
                OutputFormatResolver.Resolve(result.GetValue(output), console.Out),
                new PlanOptions(result.GetValue(strict), result.GetValue(skipUnreachable), result.GetValue(allowUnsupportedServer), result.GetValue(maxChanges)),
                result.GetValue(record)),
            cancellationToken));
        return command;
    }

    /// <summary>Validates the policy, plans every target, prints or saves the plan, and returns the exit code.</summary>
    internal static async Task<ExitCode> RunAsync(CliConsole console, CliServices services, PlanArguments arguments, CancellationToken cancellationToken)
    {
        // With --json, standard output carries only the plan, so everything else goes to standard error.
        PlanRunResult run = await PlanRun.RunAsync(
            console, services, arguments.Policy, arguments.Options, arguments.Record, arguments.Format, toError: arguments.Json, cancellationToken);
        if (run.Result is not { } result)
        {
            return ExitCode.InvalidPolicyOrUsage;
        }

        IReadOnlyList<Diagnostic> policyDiagnostics = run.PolicyDiagnostics;
        IReadOnlyList<Diagnostic> diagnostics = run.Diagnostics;
        bool hasErrors = diagnostics.HasErrors();

        string? savedTo = null;
        if (arguments.Out is { } file)
        {
            if (hasErrors)
            {
                console.ErrorWriter.WriteLine($"No plan file was written to {file.FullName}, because the plan has errors.");
            }
            else
            {
                file.Directory?.Create();
                await File.WriteAllTextAsync(file.FullName, PlanFile.Serialize(result.Document), cancellationToken);
                savedTo = file.FullName;
            }
        }

        if (arguments.Json)
        {
            DiagnosticRenderer.Write(console, diagnostics, arguments.Format, toError: true);
            console.OutWriter.Write(PlanFile.Serialize(result.Document));
            if (savedTo is not null)
            {
                console.ErrorWriter.WriteLine($"Saved the plan to {savedTo}.");
            }
        }
        else
        {
            // Repo diagnostics show inline in the plan; the rest come first, as validate prints them.
            DiagnosticRenderer.Write(console, [.. policyDiagnostics, .. result.RunDiagnostics], arguments.Format);
            PlanRenderer.Write(console, result.Document, arguments.Format, arguments.Verbose, savedTo);
        }

        return hasErrors ? PlanRun.ErrorExitCode(diagnostics)
            : arguments.ExitCode && result.Document.HasChanges ? ExitCode.ChangesFound
            : ExitCode.Success;
    }
}

/// <summary>The parsed arguments of <c>setpoint plan</c>.</summary>
internal sealed record PlanArguments(
    DirectoryInfo Policy,
    FileInfo? Out,
    bool Json,
    bool ExitCode,
    bool Verbose,
    OutputFormat Format,
    PlanOptions Options,
    DirectoryInfo? Record = null);
