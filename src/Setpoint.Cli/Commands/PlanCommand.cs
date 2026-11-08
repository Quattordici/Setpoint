using System.CommandLine;

namespace Setpoint.Cli.Commands;

/// <summary><c>setpoint plan</c>: read live state and show the changes apply would make.</summary>
internal static class PlanCommand
{
    public static Command Create(CliConsole console)
    {
        var command = new Command("plan", "Read live state and show the changes apply would make.")
        {
            CommonOptions.Policy(),
            new Option<FileInfo>("--out")
            {
                Description = "Save the plan file here, for a later 'setpoint apply <file>'.",
                HelpName = "file",
            },
            new Option<bool>("--json")
            {
                Description = "Write the plan to standard output as JSON, in the plan file schema.",
            },
            new Option<bool>("--exit-code")
            {
                Description = "Exit with code 5 when there are changes.",
            },
            CommonOptions.Strict(),
            CommonOptions.Verbose(),
            CommonOptions.Output(),
            CommonOptions.SkipUnreachable(),
            CommonOptions.AllowUnsupportedServer(),
            CommonOptions.Record(),
        };

        command.SetAction((_, _) => NotImplemented.Report(console, "plan"));
        return command;
    }
}
