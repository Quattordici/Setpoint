using System.CommandLine;
using Setpoint.Cli.Composition;
using Setpoint.Cli.Rendering;
using Setpoint.Core;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Model;
using Spectre.Console;

namespace Setpoint.Cli.Commands;

/// <summary><c>setpoint validate</c>: offline checks of the policy repo.</summary>
internal static class ValidateCommand
{
    public static Command Create(CliConsole console)
    {
        Option<DirectoryInfo> policy = CommonOptions.Policy();
        Option<bool> strict = CommonOptions.Strict();
        Option<OutputFormat?> output = CommonOptions.Output();

        var command = new Command("validate", "Check the policy repo against the schema and merge rules, offline.")
        {
            policy,
            strict,
            CommonOptions.Verbose(),
            output,
        };

        command.SetAction((result, _) => Task.FromResult((int)Run(
            console,
            result.GetValue(policy)!,
            result.GetValue(strict),
            OutputFormatResolver.Resolve(result.GetValue(output), console.Out))));
        return command;
    }

    /// <summary>Loads and validates the policy repo, prints what it finds, and returns the exit code.</summary>
    internal static ExitCode Run(CliConsole console, DirectoryInfo policyDirectory, bool strict, OutputFormat format)
    {
        (PolicyLoadResult load, IReadOnlyList<Diagnostic> diagnostics) = PolicyGate.Check(policyDirectory, strict);
        DiagnosticRenderer.Write(console, diagnostics, format);

        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        string summary = errors > 0
            ? $"Policy is invalid: {Count(errors, "error")}, {Count(warnings, "warning")}."
            : $"Policy is valid{Describe(load.Repo)}{(warnings > 0 ? $", with {Count(warnings, "warning")}" : string.Empty)}.";

        if (format == OutputFormat.Plain)
        {
            console.OutWriter.WriteLine(summary);
        }
        else
        {
            console.Out.MarkupLine(errors > 0 ? $"[red]{Markup.Escape(summary)}[/]" : $"[green]{Markup.Escape(summary)}[/]");
        }

        return errors > 0 ? ExitCode.InvalidPolicyOrUsage : ExitCode.Success;
    }

    private static string Describe(PolicyRepo? repo) => repo is null
        ? string.Empty
        : $": {Count(repo.Targets.Content.Targets.Count, "target")}, {Count(repo.Groups.Count, "group")}, {Count(repo.RepoOverrides.Count, "repo override")}";

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? string.Empty : "s")}";
}
