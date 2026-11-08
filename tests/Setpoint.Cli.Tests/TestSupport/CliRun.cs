using Setpoint.Cli.Composition;
using Setpoint.Core;
using Spectre.Console.Testing;

namespace Setpoint.Cli.Tests.TestSupport;

/// <summary>
/// Runs the CLI in memory and captures everything it writes, so a test can assert on
/// the exit code and on the text a user would see.
/// </summary>
internal sealed class CliRun
{
    private readonly TestConsole _out = new();
    private readonly TestConsole _error = new();
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errorWriter = new();

    private CliRun()
    {
    }

    public ExitCode ExitCode { get; private set; }

    /// <summary>Everything written to standard output, rich and plain.</summary>
    public string Out => _outWriter.ToString() + _out.Output;

    /// <summary>Everything written to standard error, rich and plain.</summary>
    public string Error => _errorWriter.ToString() + _error.Output;

    public static Task<CliRun> InvokeAsync(params string[] args) => InvokeAsync(CliServices.Default, args);

    /// <summary>Runs the CLI with the given services, such as fake adapters.</summary>
    public static async Task<CliRun> InvokeAsync(CliServices services, params string[] args)
    {
        var run = new CliRun();
        var console = new CliConsole(run._out, run._error, run._outWriter, run._errorWriter);
        run.ExitCode = await SetpointCli.RunAsync(args, console, services, TestContext.Current.CancellationToken);
        return run;
    }
}
