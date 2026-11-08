using Spectre.Console;

namespace Setpoint.Cli;

/// <summary>Creates the Spectre consoles the CLI writes to.</summary>
internal static class ConsoleFactory
{
    /// <summary>
    /// The width used when the output is not a terminal, such as a pipeline log. Spectre reports
    /// no width there (and renders nothing at all), or a guess from the environment such as 80 on
    /// a CI runner, so redirected output always gets this width instead.
    /// </summary>
    public const int RedirectedWidth = 120;

    public static IAnsiConsole Create(TextWriter writer)
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
        });

        if (!console.Profile.Out.IsTerminal || console.Profile.Width <= 0)
        {
            console.Profile.Width = RedirectedWidth;
        }

        return console;
    }
}
