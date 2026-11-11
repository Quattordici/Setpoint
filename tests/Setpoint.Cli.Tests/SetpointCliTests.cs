using Setpoint.Cli.Tests.TestSupport;
using Setpoint.Core;

namespace Setpoint.Cli.Tests;

/// <summary>
/// Tests for <see cref="SetpointCli"/>, the <c>setpoint</c> command tree: help and version output,
/// usage errors and their exit code, and the rule that a bare <c>apply</c> needs <c>--yes</c>.
/// </summary>
public sealed class SetpointCliTests
{
    /// <summary>
    /// Given the setpoint command,
    /// when it is run with --help,
    /// then the help lists the validate, plan, drift and apply commands and the run succeeds.
    /// </summary>
    [Fact(DisplayName = "👍 Root help lists validate, plan, drift and apply")]
    public async Task RunAsync_RootHelp_ListsAllCommands()
    {
        // Arrange
        string[] args = ["--help"];

        // Act
        CliRun run = await CliRun.InvokeAsync(args);

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success);
        run.Out.ShouldContain("validate");
        run.Out.ShouldContain("plan");
        run.Out.ShouldContain("drift");
        run.Out.ShouldContain("apply");
    }

    /// <summary>
    /// Given one of the three commands,
    /// when its help is requested,
    /// then the help shows the command's own usage line and an option that only it has.
    /// </summary>
    [Theory(DisplayName = "👍 Command help shows the command's usage and its own options")]
    [InlineData("validate", "--policy")]
    [InlineData("plan", "--exit-code")]
    [InlineData("apply", "--max-changes")]
    [InlineData("drift", "--report")]
    public async Task RunAsync_CommandHelp_ShowsCommandOptions(string command, string expectedOption)
    {
        // Arrange
        string[] args = [command, "--help"];

        // Act
        CliRun run = await CliRun.InvokeAsync(args);

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success);
        run.Out.ShouldContain($"setpoint {command}");
        run.Out.ShouldContain(expectedOption);
    }

    /// <summary>
    /// Given the setpoint command,
    /// when it is run with --version,
    /// then it prints the package version and succeeds.
    /// </summary>
    [Fact(DisplayName = "👍 Prints the version")]
    public async Task RunAsync_Version_PrintsVersion()
    {
        // Arrange
        string[] args = ["--version"];

        // Act
        CliRun run = await CliRun.InvokeAsync(args);

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success);
        run.Out.Trim().ShouldBe(SetpointVersion.Current);
        run.Out.ShouldStartWith("1.");
    }

    /// <summary>
    /// Given a command line with an option the command doesn't have,
    /// when it is run,
    /// then it exits with the usage error code and points at the command's help.
    /// </summary>
    [Fact(DisplayName = "👎 Rejects an unknown option with the usage exit code")]
    public async Task RunAsync_UnknownOption_ReturnsUsageError()
    {
        // Arrange
        string[] args = ["plan", "--no-such-option"];

        // Act
        CliRun run = await CliRun.InvokeAsync(args);

        // Assert
        run.ExitCode.ShouldBe(ExitCode.InvalidPolicyOrUsage);
        run.Error.ShouldContain("--no-such-option");
        run.Error.ShouldContain("setpoint plan --help");
    }

    /// <summary>
    /// Given an apply with neither a plan file nor --yes,
    /// when it is run,
    /// then it is refused as a usage error before anything is planned or applied.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a bare apply without --yes")]
    public async Task RunAsync_BareApplyWithoutYes_ReturnsUsageError()
    {
        // Arrange
        string[] args = ["apply"];

        // Act
        CliRun run = await CliRun.InvokeAsync(args);

        // Assert
        run.ExitCode.ShouldBe(ExitCode.InvalidPolicyOrUsage);
        run.Error.ShouldContain("--yes");
    }

    /// <summary>
    /// Given an --output value that is neither tree nor plain,
    /// when it is run,
    /// then it is refused as a usage error.
    /// </summary>
    [Fact(DisplayName = "👎 Rejects an unknown output layout")]
    public async Task RunAsync_UnknownOutputFormat_ReturnsUsageError()
    {
        // Arrange
        string[] args = ["validate", "--output", "fancy"];

        // Act
        CliRun run = await CliRun.InvokeAsync(args);

        // Assert
        run.ExitCode.ShouldBe(ExitCode.InvalidPolicyOrUsage);
    }

    /// <summary>
    /// Given an apply with a plan file, or a bare apply confirmed with --yes,
    /// when it is run,
    /// then it passes argument validation and reaches the command action.
    /// </summary>
    [Theory(DisplayName = "🖐️ Accepts apply with a plan file or with --yes")]
    [InlineData("apply", "plan.json")]
    [InlineData("apply", "--yes")]
    public async Task RunAsync_ApplyWithPlanFileOrYes_PassesValidation(string command, string argument)
    {
        // Arrange
        string[] args = [command, argument];

        // Act
        CliRun run = await CliRun.InvokeAsync(args);

        // Assert
        run.Error.ShouldNotContain("for usage.");
        run.Error.ShouldNotContain("pass --yes");
    }

    /// <summary>
    /// Given plain output requested with the short and long option names,
    /// when the command is run,
    /// then both spellings parse.
    /// </summary>
    [Theory(DisplayName = "🖐️ Accepts --output plain and -o plain")]
    [InlineData("--output")]
    [InlineData("-o")]
    public async Task RunAsync_OutputPlain_Parses(string optionName)
    {
        // Arrange
        string[] args = ["plan", optionName, "plain"];

        // Act
        CliRun run = await CliRun.InvokeAsync(args);

        // Assert: plan now runs and fails on the missing policy, so check that parsing reported nothing.
        run.Error.ShouldNotContain("for usage.");
    }

    /// <summary>
    /// Given the dev-only --record option,
    /// when plan help is shown and plan is run with it,
    /// then help doesn't list it but the option parses.
    /// </summary>
    [Fact(DisplayName = "🖐️ --record is hidden from help but accepted")]
    public async Task RunAsync_RecordOption_HiddenButAccepted()
    {
        // Arrange
        string[] help = ["plan", "--help"];
        string[] withRecord = ["plan", "--record", Path.GetTempPath()];

        // Act
        CliRun helpRun = await CliRun.InvokeAsync(help);
        CliRun recordRun = await CliRun.InvokeAsync(withRecord);

        // Assert
        helpRun.Out.ShouldNotContain("--record");
        recordRun.Error.ShouldNotContain("for usage.");
    }
}
