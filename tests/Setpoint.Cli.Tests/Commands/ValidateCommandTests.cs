using Setpoint.Cli.Tests.TestSupport;
using Setpoint.Core;

namespace Setpoint.Cli.Tests.Commands;

/// <summary>
/// Tests for <c>setpoint validate</c> end to end: exit codes, the plain diagnostic format and <c>--strict</c>.
/// </summary>
public sealed class ValidateCommandTests : IDisposable
{
    private static readonly string s_testData = Path.Combine(AppContext.BaseDirectory, "TestData");

    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("setpoint-validate-");

    /// <summary>
    /// Given a valid policy repo,
    /// when validate runs,
    /// then it succeeds and summarises what it checked.
    /// </summary>
    [Fact(DisplayName = "👍 A valid policy repo validates with exit 0")]
    public async Task Validate_ValidRepo_Succeeds()
    {
        // Arrange
        string policy = Path.Combine(s_testData, "valid-policy-repo");

        // Act
        CliRun run = await CliRun.InvokeAsync("validate", "--policy", policy, "--output", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success);
        run.Out.ShouldContain("Policy is valid: 3 targets, 1 group, 1 repo override.");
    }

    /// <summary>
    /// Given the prototype policy repo, whose GitHub target predates the scope decision,
    /// when validate runs with plain output,
    /// then it exits 2 and prints the error in file:line:col format.
    /// </summary>
    [Fact(DisplayName = "👎 An invalid policy exits 2 with compiler-style lines")]
    public async Task Validate_InvalidRepo_ReturnsInvalidPolicy()
    {
        // Arrange
        string policy = Path.Combine(s_testData, "prototype-policy-repo");

        // Act
        CliRun run = await CliRun.InvokeAsync("validate", "-p", policy, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.InvalidPolicyOrUsage);
        run.Out.ShouldContain("targets.yml:13:3: error SP0118: GitHub target 'github' needs 'scope:'");
        run.Out.ShouldContain("Policy is invalid: 1 error, 1 warning.");
    }

    /// <summary>
    /// Given a policy whose only problem is a warning,
    /// when validate runs with and without --strict,
    /// then it passes without and fails with it.
    /// </summary>
    [Theory(DisplayName = "🖐️ --strict fails on warnings")]
    [InlineData(false, ExitCode.Success)]
    [InlineData(true, ExitCode.InvalidPolicyOrUsage)]
    public async Task Validate_WarningOnly_FailsOnlyWhenStrict(bool strict, ExitCode expected)
    {
        // Arrange
        CopyDirectory(Path.Combine(s_testData, "valid-policy-repo"), _scratch.FullName);
        File.WriteAllText(
            Path.Combine(_scratch.FullName, "baseline.yml"),
            "version: 1\nlayer: baseline\nrules:\n  requireCommentResolution: { enabled: true }\n");
        string[] args = strict
            ? ["validate", "-p", _scratch.FullName, "-o", "plain", "--strict"]
            : ["validate", "-p", _scratch.FullName, "-o", "plain"];

        // Act
        CliRun run = await CliRun.InvokeAsync(args);

        // Assert
        run.ExitCode.ShouldBe(expected);
        run.Out.ShouldContain("SP0119");
    }

    /// <summary>
    /// Given a directory that doesn't exist,
    /// when validate runs,
    /// then it exits 2 explaining the directory is missing.
    /// </summary>
    [Fact(DisplayName = "👎 A missing policy directory exits 2")]
    public async Task Validate_MissingDirectory_ReturnsInvalidPolicy()
    {
        // Arrange
        string policy = Path.Combine(_scratch.FullName, "nope");

        // Act
        CliRun run = await CliRun.InvokeAsync("validate", "-p", policy, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.InvalidPolicyOrUsage);
        run.Out.ShouldContain("does not exist");
    }

    public void Dispose() => _scratch.Delete(recursive: true);

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
