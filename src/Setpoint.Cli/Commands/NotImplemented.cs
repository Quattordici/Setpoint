using Setpoint.Core;
using Spectre.Console;

namespace Setpoint.Cli.Commands;

/// <summary>Placeholder action for commands whose behaviour lands in later build tickets.</summary>
internal static class NotImplemented
{
    public static Task<int> Report(CliConsole console, string commandName)
    {
        console.Error.MarkupLineInterpolated($"[yellow]setpoint {commandName} is not implemented yet.[/]");
        return Task.FromResult((int)ExitCode.RuntimeFailure);
    }
}
