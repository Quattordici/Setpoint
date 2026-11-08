using System.CommandLine;
using Setpoint.Cli.Composition;
using Setpoint.Cli.Rendering;
using Setpoint.Core;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy.Loading;
using Spectre.Console;

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
            CommonOptions.Record(),
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
                new PlanOptions(result.GetValue(strict), result.GetValue(skipUnreachable), result.GetValue(allowUnsupportedServer))),
            cancellationToken));
        return command;
    }

    /// <summary>Validates the policy, plans every target, prints or saves the plan, and returns the exit code.</summary>
    internal static async Task<ExitCode> RunAsync(CliConsole console, CliServices services, PlanArguments arguments, CancellationToken cancellationToken)
    {
        // With --json, standard output carries only the plan, so everything else goes to standard error.
        bool toError = arguments.Json;

        (PolicyLoadResult load, IReadOnlyList<Diagnostic> policyDiagnostics) = PolicyGate.Check(arguments.Policy, arguments.Options.Strict);
        if (load.Repo is null || policyDiagnostics.HasErrors())
        {
            DiagnosticRenderer.Write(console, policyDiagnostics, arguments.Format, toError);
            console.ErrorWriter.WriteLine("The policy is invalid, so no target was contacted. Run 'setpoint validate' for details.");
            return ExitCode.InvalidPolicyOrUsage;
        }

        var planner = new Planner(services.Adapters, ProviderBlocks.All, services.Time);
        var input = new PlanInput(load.Repo, load.Sources, PolicyCommit.Find(arguments.Policy.FullName));
        PlanResult result = await planner.PlanAsync(input, arguments.Options, cancellationToken);

        IReadOnlyList<Diagnostic> diagnostics = [.. policyDiagnostics, .. result.Diagnostics];
        DiagnosticRenderer.Write(console, diagnostics, arguments.Format, toError);

        if (arguments.Json)
        {
            console.OutWriter.Write(PlanFile.Serialize(result.Document));
        }
        else
        {
            PlanSummaryRenderer.Write(console, result.Document, arguments.Verbose);
        }

        bool hasErrors = diagnostics.HasErrors();
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
                (toError ? console.ErrorWriter : console.OutWriter).WriteLine($"Saved the plan to {file.FullName}. Apply it with 'setpoint apply {file.Name}'.");
            }
        }

        return hasErrors ? ErrorExitCode(diagnostics)
            : arguments.ExitCode && result.Document.HasChanges ? ExitCode.ChangesFound
            : ExitCode.Success;
    }

    // Problems with the policy or the selectors exit 2, like validate; problems reaching or reading live state exit 1.
    private static ExitCode ErrorExitCode(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Code[2] == '0')
            ? ExitCode.InvalidPolicyOrUsage
            : ExitCode.RuntimeFailure;
}

/// <summary>The parsed arguments of <c>setpoint plan</c>.</summary>
internal sealed record PlanArguments(
    DirectoryInfo Policy,
    FileInfo? Out,
    bool Json,
    bool ExitCode,
    bool Verbose,
    OutputFormat Format,
    PlanOptions Options);
