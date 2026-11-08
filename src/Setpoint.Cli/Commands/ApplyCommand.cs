using System.CommandLine;

namespace Setpoint.Cli.Commands;

/// <summary>
/// <c>setpoint apply</c>: apply a saved plan file, or re-plan and apply in one step with <c>--yes</c>.
/// </summary>
internal static class ApplyCommand
{
    public static Command Create(CliConsole console)
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

        var command = new Command("apply", "Apply a saved plan, or re-plan and apply with --yes.")
        {
            planFile,
            CommonOptions.Policy(),
            yes,
            CommonOptions.MaxChanges(),
            CommonOptions.FailFast(),
            CommonOptions.Strict(),
            CommonOptions.Verbose(),
            CommonOptions.Output(),
            CommonOptions.SkipUnreachable(),
            CommonOptions.AllowUnsupportedServer(),
            CommonOptions.Record(),
        };

        command.Validators.Add(result =>
        {
            if (result.GetValue(planFile) is null && !result.GetValue(yes))
            {
                result.AddError("Give a plan file, or pass --yes to re-plan and apply in one step.");
            }
        });

        command.SetAction((_, _) => NotImplemented.Report(console, "apply"));
        return command;
    }
}
