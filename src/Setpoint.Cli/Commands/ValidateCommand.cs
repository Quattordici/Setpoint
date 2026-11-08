using System.CommandLine;

namespace Setpoint.Cli.Commands;

/// <summary><c>setpoint validate</c>: offline checks of the policy repo.</summary>
internal static class ValidateCommand
{
    public static Command Create(CliConsole console)
    {
        var command = new Command("validate", "Check the policy repo against the schema and merge rules, offline.")
        {
            CommonOptions.Policy(),
            CommonOptions.Strict(),
            CommonOptions.Verbose(),
            CommonOptions.Output(),
        };

        command.SetAction((_, _) => NotImplemented.Report(console, "validate"));
        return command;
    }
}
