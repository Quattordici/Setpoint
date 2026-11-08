using Spectre.Console;

namespace Setpoint.Cli;

/// <summary>Picks the output layout: the one asked for, else tree on a terminal and plain otherwise.</summary>
internal static class OutputFormatResolver
{
    public static OutputFormat Resolve(OutputFormat? requested, IAnsiConsole console) =>
        requested ?? (console.Profile.Out.IsTerminal && console.Profile.Capabilities.Ansi ? OutputFormat.Tree : OutputFormat.Plain);
}
