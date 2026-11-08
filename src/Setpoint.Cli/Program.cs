using Setpoint.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var console = new CliConsole(
    Out: ConsoleFactory.Create(Console.Out),
    Error: ConsoleFactory.Create(Console.Error),
    OutWriter: Console.Out,
    ErrorWriter: Console.Error);

return (int)await SetpointCli.RunAsync(args, console, cancellation.Token);
