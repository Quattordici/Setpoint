using Spectre.Console;

namespace Setpoint.Cli;

/// <summary>
/// The output streams the CLI writes to. Rich output goes through the Spectre consoles;
/// System.CommandLine writes help and parse errors to the plain writers.
/// Tests pass in-memory versions of all four.
/// </summary>
internal sealed record CliConsole(IAnsiConsole Out, IAnsiConsole Error, TextWriter OutWriter, TextWriter ErrorWriter);
