using Setpoint.Cli.Tests.TestSupport;
using Setpoint.Core;

namespace Setpoint.Cli.Tests.Policy;

/// <summary>
/// The live gate policies under tests/live are assembled with each environment's targets.yml at run time, the way
/// Invoke-LiveGate.ps1 does it. A gate that fails at validate on the night wastes the run, so check them here.
/// </summary>
public sealed class LiveGatePolicyTests : IDisposable
{
    private static readonly string s_live = Path.Combine(RepoRoot(), "tests", "live");

    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("setpoint-live-");

    /// <summary>
    /// Given each live environment's targets and each gate policy,
    /// when they are put together and validated with --strict,
    /// then they pass with no warnings.
    /// </summary>
    [Theory(DisplayName = "👍 Each live gate policy validates against each environment")]
    [InlineData("github", "gate")]
    [InlineData("github", "reset")]
    [InlineData("ado-cloud", "gate")]
    [InlineData("ado-cloud", "reset")]
    [InlineData("ado-server", "gate")]
    [InlineData("ado-server", "reset")]
    public async Task Validate_LivePolicy_Succeeds(string environment, string policy)
    {
        // Arrange
        string repo = Path.Combine(_scratch.FullName, $"{environment}-{policy}");
        CopyDirectory(Path.Combine(s_live, "policies", policy), repo);
        File.Copy(Path.Combine(s_live, "targets", $"{environment}.yml"), Path.Combine(repo, "targets.yml"));

        // Act
        CliRun run = await CliRun.InvokeAsync("validate", "--policy", repo, "--strict", "--output", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success, run.Out);
    }

    /// <summary>
    /// Given the live targets,
    /// when their target names are read,
    /// then every environment names its one target 'live', so the script's single SETPOINT_LIVE_TOKEN fits all.
    /// </summary>
    [Theory(DisplayName = "👍 Every live environment names its target 'live'")]
    [InlineData("github")]
    [InlineData("ado-cloud")]
    [InlineData("ado-server")]
    public void Targets_LiveEnvironment_NameTheTargetLive(string environment)
    {
        // Arrange
        string text = File.ReadAllText(Path.Combine(s_live, "targets", $"{environment}.yml"));

        // Act
        string[] targets = [.. text.ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("   ", StringComparison.Ordinal) && l.TrimEnd().EndsWith(':'))];

        // Assert
        targets.ShouldBe(["  live:"]);
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

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Setpoint.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repo root (Setpoint.slnx) above the test output directory.");
    }
}
