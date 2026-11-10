using System.CommandLine;
using Setpoint.Cli.Composition;
using Setpoint.Cli.Rendering;
using Setpoint.Core;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;

namespace Setpoint.Cli.Commands;

/// <summary>
/// <c>setpoint drift</c>: check that live state still matches the policy, for a scheduled pipeline. It plans
/// exactly like <c>plan</c> and never writes. It exits 5 when anything differs, and 1 when a target or repo
/// couldn't be read, because what wasn't read could be hiding drift.
/// </summary>
internal static class DriftCommand
{
    public static Command Create(CliConsole console, CliServices services)
    {
        Option<DirectoryInfo> policy = CommonOptions.Policy();
        var report = new Option<FileInfo>("--report")
        {
            Description = "Save the drift report here, in the plan file schema. After review, 'setpoint apply <file>' restores the policy.",
            HelpName = "file",
        };
        var json = new Option<bool>("--json")
        {
            Description = "Write the drift report to standard output as JSON, in the plan file schema.",
        };
        Option<FileInfo?> summary = CommonOptions.Summary();
        Option<bool> strict = CommonOptions.Strict();
        Option<bool> verbose = CommonOptions.Verbose();
        Option<OutputFormat?> output = CommonOptions.Output();
        Option<bool> skipUnreachable = CommonOptions.SkipUnreachable();
        Option<bool> allowUnsupportedServer = CommonOptions.AllowUnsupportedServer();
        Option<DirectoryInfo?> record = CommonOptions.Record();

        var command = new Command("drift", "Check that live state still matches the policy. Exits 5 when it doesn't.")
        {
            policy,
            report,
            json,
            summary,
            strict,
            verbose,
            output,
            skipUnreachable,
            allowUnsupportedServer,
            record,
        };

        command.SetAction(async (result, cancellationToken) => (int)await RunAsync(
            console,
            services,
            new DriftArguments(
                result.GetValue(policy)!,
                result.GetValue(report),
                result.GetValue(json),
                result.GetValue(verbose),
                OutputFormatResolver.Resolve(result.GetValue(output), console.Out),
                new PlanOptions(result.GetValue(strict), result.GetValue(skipUnreachable), result.GetValue(allowUnsupportedServer)),
                result.GetValue(record),
                result.GetValue(summary)),
            cancellationToken));
        return command;
    }

    /// <summary>Plans every target, prints the drift, saves the report, and returns the exit code.</summary>
    internal static async Task<ExitCode> RunAsync(CliConsole console, CliServices services, DriftArguments arguments, CancellationToken cancellationToken)
    {
        // With --json, standard output carries only the report, so everything else goes to standard error.
        PlanRunResult run = await PlanRun.RunAsync(
            console, services, arguments.Policy, arguments.Options, arguments.Record, arguments.Format, toError: arguments.Json, cancellationToken);
        if (run.Result is not { } result)
        {
            return ExitCode.InvalidPolicyOrUsage;
        }

        IReadOnlyList<Diagnostic> diagnostics = run.Diagnostics;
        bool hasErrors = diagnostics.HasErrors();
        string report = PlanFile.Serialize(result.Document);

        // Unlike plan --out, the report is saved even when the run has errors: an unattended run needs the record.
        // What was read is still exact, and apply checks every object's fingerprint before it writes.
        string? savedTo = null;
        if (arguments.Report is { } file)
        {
            file.Directory?.Create();
            await File.WriteAllTextAsync(file.FullName, report, cancellationToken);
            savedTo = file.FullName;
        }

        if (arguments.Summary is { } summaryFile)
        {
            await MarkdownSummary.AppendAsync(summaryFile, result.Document, PlanView.Drift, cancellationToken);
        }

        if (arguments.Json)
        {
            DiagnosticRenderer.Write(console, diagnostics, arguments.Format, toError: true);
            console.OutWriter.Write(report);
            console.ErrorWriter.WriteLine(PlanRenderer.DriftVerdict(result.Document));
            if (savedTo is not null)
            {
                console.ErrorWriter.WriteLine($"Saved the drift report to {savedTo}.");
            }
        }
        else
        {
            // Repo diagnostics show inline; the rest come first, as plan prints them.
            DiagnosticRenderer.Write(console, [.. run.PolicyDiagnostics, .. result.RunDiagnostics], arguments.Format);
            PlanRenderer.Write(console, result.Document, arguments.Format, arguments.Verbose, savedTo, PlanView.Drift);
        }

        if (hasErrors && savedTo is not null)
        {
            console.ErrorWriter.WriteLine("The drift report is incomplete because the run had errors. Plan again before applying it.");
        }

        return hasErrors ? PlanRun.ErrorExitCode(diagnostics)
            : result.Document.HasChanges ? ExitCode.ChangesFound
            : ExitCode.Success;
    }
}

/// <summary>The parsed arguments of <c>setpoint drift</c>.</summary>
internal sealed record DriftArguments(
    DirectoryInfo Policy,
    FileInfo? Report,
    bool Json,
    bool Verbose,
    OutputFormat Format,
    PlanOptions Options,
    DirectoryInfo? Record = null,
    FileInfo? Summary = null);
