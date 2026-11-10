using System.CommandLine;
using System.CommandLine.Help;
using Setpoint.Cli.Commands;
using Setpoint.Cli.Composition;
using Setpoint.Core;

namespace Setpoint.Cli;

/// <summary>Builds the <c>setpoint</c> command tree and runs it.</summary>
internal static class SetpointCli
{
    /// <summary>The command name, used in help whatever the host executable is called.</summary>
    public const string CommandName = "setpoint";

    /// <summary>
    /// Builds the root command. It is a named <see cref="Command"/> rather than a
    /// <see cref="RootCommand"/>, whose name comes from the host executable and would read
    /// "dotnet" or the test runner's name in help.
    /// </summary>
    public static Command CreateRootCommand(CliConsole console, CliServices services) =>
        new(CommandName, "Setpoint, repository governance as code for GitHub and Azure DevOps.")
        {
            new HelpOption(),
            new VersionOption(),
            ValidateCommand.Create(console),
            PlanCommand.Create(console, services),
            DriftCommand.Create(console, services),
            ApplyCommand.Create(console, services),
        };

    /// <summary>
    /// Parses <paramref name="args"/> and runs the matching command. A usage error returns
    /// <see cref="ExitCode.InvalidPolicyOrUsage"/>, so pipelines can tell it from a runtime failure.
    /// </summary>
    public static async Task<ExitCode> RunAsync(string[] args, CliConsole console, CliServices services, CancellationToken cancellationToken)
    {
        Command root = CreateRootCommand(console, services);
        ParseResult parseResult = root.Parse(args);

        var invocation = new InvocationConfiguration
        {
            Output = console.OutWriter,
            Error = console.ErrorWriter,
        };

        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await console.ErrorWriter.WriteLineAsync(error.Message);
            }

            Command command = parseResult.CommandResult.Command;
            string commandPath = command == root ? CommandName : $"{CommandName} {command.Name}";
            await console.ErrorWriter.WriteLineAsync($"Run '{commandPath} --help' for usage.");
            return ExitCode.InvalidPolicyOrUsage;
        }

        return (ExitCode)await parseResult.InvokeAsync(invocation, cancellationToken);
    }
}
