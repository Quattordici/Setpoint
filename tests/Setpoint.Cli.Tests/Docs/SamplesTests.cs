using System.Text.RegularExpressions;
using Setpoint.Cli.Composition;
using Setpoint.Cli.Tests.TestSupport;
using Setpoint.Core;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Providers;

namespace Setpoint.Cli.Tests.Docs;

/// <summary>
/// Keeps the getting-started guide and the samples it points at honest: the sample policy repo validates, the
/// pinned Setpoint version is the one being built, the sample pipelines set every token the sample targets read,
/// and the drift pipelines run on a schedule with read-only secrets only.
/// </summary>
public sealed partial class SamplesTests
{
    private static readonly string s_root = RepoRoot();
    private static readonly string s_policyRepo = Path.Combine(s_root, "samples", "policy-repo");

    /// <summary>
    /// Given the sample policy repo that docs/getting-started.md copies,
    /// when validate runs on it with --strict,
    /// then it passes with no warnings, and says what the guide says it says.
    /// </summary>
    [Fact(DisplayName = "👍 The sample policy repo validates cleanly with --strict")]
    public async Task Validate_SamplePolicyRepo_Succeeds()
    {
        // Arrange
        string guide = File.ReadAllText(Path.Combine(s_root, "docs", "getting-started.md"));

        // Act
        CliRun run = await CliRun.InvokeAsync("validate", "--policy", s_policyRepo, "--strict", "--output", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success, run.Out);
        run.Out.ShouldContain("Policy is valid: 2 targets, 1 group, 1 repo override.");
        guide.ShouldContain("Policy is valid: 2 targets, 1 group, 1 repo override.");
    }

    /// <summary>
    /// Given the guide and both sample pipelines,
    /// when the Setpoint version they pin is read,
    /// then it is the version being built, because apply refuses a plan made by another version.
    /// </summary>
    [Theory(DisplayName = "👍 Docs and samples pin the version being built")]
    [InlineData("docs/getting-started.md")]
    [InlineData("samples/pipelines/azure-pipelines.yml")]
    [InlineData("samples/pipelines/github-actions.yml")]
    [InlineData("samples/pipelines/drift-azure-pipelines.yml")]
    [InlineData("samples/pipelines/drift-github-actions.yml")]
    public void PinnedVersion_DocsAndSamples_MatchCurrentVersion(string file)
    {
        // Arrange
        string text = File.ReadAllText(Path.Combine(s_root, file));
        string current = SetpointVersion.Current.Split('+')[0];

        // Act
        string[] pinned = [.. PinnedVersion().Matches(text).Select(m => m.Groups["version"].Value).Distinct()];

        // Assert
        pinned.ShouldBe([current], $"{file} pins another Setpoint version. Update it to {current}.");
    }

    /// <summary>
    /// Given the sample policy repo's targets,
    /// when the sample pipelines are read,
    /// then each pipeline maps every target's token variable in both its plan and its apply step.
    /// </summary>
    [Theory(DisplayName = "👍 Sample pipelines set every sample target's token, in both stages")]
    [InlineData("samples/pipelines/azure-pipelines.yml")]
    [InlineData("samples/pipelines/github-actions.yml")]
    public void Pipelines_TokenVariables_CoverEveryTarget(string file)
    {
        // Arrange
        PolicyLoadResult load = new PolicyRepoLoader(ProviderBlocks.All).LoadDirectory(s_policyRepo);
        string[] variables = [.. load.Repo!.Targets.Content.Targets.Select(t => TargetCredentials.TokenVariable(new Target(t.Key, t.Value)))];
        string text = File.ReadAllText(Path.Combine(s_root, file));

        // Act
        Dictionary<string, int> mappings = variables.ToDictionary(v => v, v => Regex.Matches(text, $@"^\s+{v}:", RegexOptions.Multiline).Count);

        // Assert
        mappings.ShouldAllBe(m => m.Value == 2, $"{file} must map each token variable once in the plan step and once in the apply step.");
    }

    /// <summary>
    /// Given the sample policy repo's targets,
    /// when the drift pipelines are read,
    /// then each maps every target's token once, from the read-only secrets, and never names a write secret.
    /// </summary>
    [Theory(DisplayName = "🛡️ Drift pipelines set every token once, from read-only secrets only")]
    [InlineData("samples/pipelines/drift-azure-pipelines.yml", "setpoint-plan", "setpoint-apply")]
    [InlineData("samples/pipelines/drift-github-actions.yml", "setpoint-plan", "setpoint-apply")]
    public void DriftPipelines_Tokens_ReadOnly(string file, string readOnlySecrets, string writeSecretsMarker)
    {
        // Arrange
        PolicyLoadResult load = new PolicyRepoLoader(ProviderBlocks.All).LoadDirectory(s_policyRepo);
        string[] variables = [.. load.Repo!.Targets.Content.Targets.Select(t => TargetCredentials.TokenVariable(new Target(t.Key, t.Value)))];
        string text = File.ReadAllText(Path.Combine(s_root, file));

        // Act
        Dictionary<string, int> mappings = variables.ToDictionary(v => v, v => Regex.Matches(text, $@"^\s+{v}:", RegexOptions.Multiline).Count);

        // Assert
        mappings.ShouldAllBe(m => m.Value == 1, $"{file} must map each token variable once, in the drift step.");
        text.ShouldContain(readOnlySecrets);
        text.ShouldNotContain(writeSecretsMarker, Case.Sensitive);
        text.ShouldNotContain("ApplyToken", Case.Sensitive);
    }

    /// <summary>
    /// Given the sample pipelines,
    /// when their triggers and drift steps are read,
    /// then only the drift pipelines are scheduled, and they run drift with --strict, --report and --summary
    /// and publish the report even when the check fails.
    /// </summary>
    [Theory(DisplayName = "👍 Only the drift pipelines are scheduled, and they keep the report")]
    [InlineData("samples/pipelines/azure-pipelines.yml", false)]
    [InlineData("samples/pipelines/github-actions.yml", false)]
    [InlineData("samples/pipelines/drift-azure-pipelines.yml", true)]
    [InlineData("samples/pipelines/drift-github-actions.yml", true)]
    public void Pipelines_Schedule_OnlyDrift(string file, bool drift)
    {
        // Arrange
        string text = File.ReadAllText(Path.Combine(s_root, file));

        // Act
        bool scheduled = Regex.IsMatch(text, @"^\s*-?\s*cron:", RegexOptions.Multiline);

        // Assert
        scheduled.ShouldBe(drift, $"{file}: drift checks belong in the drift pipelines.");
        if (drift)
        {
            text.ShouldContain(" drift --strict ");
            text.ShouldContain("--report");
            text.ShouldContain("--summary");
            text.ShouldMatch(@"(condition: always\(\)|if: always\(\))");
            text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).ShouldNotContain(l => l.Contains(" apply ", StringComparison.Ordinal), "a drift pipeline never applies.");
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

    [GeneratedRegex(@"(?:--version|setpointVersion:|SETPOINT_VERSION:) (?<version>[0-9][0-9A-Za-z.\-]*)")]
    private static partial Regex PinnedVersion();
}
