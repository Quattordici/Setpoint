using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Policy;
using Setpoint.Adapters.GitHub.Policy;
using Setpoint.Cli.Composition;
using Setpoint.Cli.Tests.TestSupport;
using Setpoint.Core;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Testing;

namespace Setpoint.Cli.Tests.Commands;

/// <summary>
/// Tests for <c>setpoint drift</c> end to end, with fake adapters for both platforms: the exit code for drift, no
/// drift, an invalid policy, an unreachable target and an unreadable repo; <c>--report</c> and <c>--json</c>; and
/// that a drift report applies and restores the policy.
/// </summary>
public sealed class DriftCommandTests : IDisposable
{
    private static readonly string s_policy = Path.Combine(AppContext.BaseDirectory, "TestData", "valid-policy-repo");
    private static readonly RepoRef s_gateway = new("github", "quattordici", "payments-gateway");
    private static readonly RepoRef s_ledger = new("ado-onprem", "Payments", "legacy-ledger");

    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("setpoint-drift-");
    private readonly FakeProviderAdapter _github = new(Platform.GitHub, GitHubProviderBlockDefinition.Instance);
    private readonly FakeProviderAdapter _azureDevOps = new(Platform.AzureDevOps, AzureDevOpsProviderBlockDefinition.Instance);

    public DriftCommandTests()
    {
        // Every selector and the repo override in the test policy match a repo, so a run has no selection diagnostics.
        _github.AddRepo(s_gateway);
        _azureDevOps.AddRepo(s_ledger);
    }

    private CliServices Services => CliServices.With([_github, _azureDevOps], new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 6, 0, 0, TimeSpan.Zero)));

    private string ReportPath => Path.Combine(_scratch.FullName, "drift.json");

    public void Dispose() => _scratch.Delete(recursive: true);

    /// <summary>
    /// Given repos whose live state doesn't match the policy,
    /// when drift runs,
    /// then it exits 5, says drift in plain output, and ends with the verdict, writing nothing to the targets.
    /// </summary>
    [Fact(DisplayName = "👍 drift exits 5 and names the drifted repos")]
    public async Task Drift_Differences_ExitsFive()
    {
        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "drift", "-p", s_policy, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.ChangesFound, run.Out + run.Error);
        run.Out.ShouldStartWith($"setpoint {SetpointVersion.Current} drift ");
        run.Out.ShouldContain("+ github/quattordici/payments-gateway minimumReviewers: (none) -> count 2, resetOnPush [baseline]");
        run.Out.ShouldContain("summary repos_drifted=2 ");
        run.Out.ShouldNotContain("max_changes");
        run.Out.TrimEnd().ShouldEndWith("Drift found: 2 of 2 repos differ from the policy.");
        _github.Applied.ShouldBeEmpty();
        _azureDevOps.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given live state that matches the policy everywhere,
    /// when drift runs,
    /// then it exits 0 and says there is no drift.
    /// </summary>
    [Fact(DisplayName = "👍 drift exits 0 when everything matches the policy")]
    public async Task Drift_InSync_ExitsZero()
    {
        // Arrange
        await RestoreAsync();

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "drift", "-p", s_policy, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success, run.Out + run.Error);
        run.Out.TrimEnd().ShouldEndWith("No drift: 2 repos match the policy.");
    }

    /// <summary>
    /// Given drift,
    /// when drift runs with --report, the report is applied, and drift runs again,
    /// then the report is a plan file, apply writes it, and the second drift run finds nothing.
    /// </summary>
    [Fact(DisplayName = "👍 A drift report applies and restores the policy")]
    public async Task Drift_Report_AppliesAndRestores()
    {
        // Act
        CliRun drift = await CliRun.InvokeAsync(Services, "drift", "-p", s_policy, "--report", ReportPath, "-o", "plain");
        PlanDocument report = PlanFile.Deserialize(await File.ReadAllTextAsync(ReportPath, TestContext.Current.CancellationToken));
        CliRun apply = await CliRun.InvokeAsync(Services, "apply", ReportPath, "-p", s_policy);
        CopyWrittenToLive();
        CliRun again = await CliRun.InvokeAsync(Services, "drift", "-p", s_policy, "-o", "plain");

        // Assert
        drift.ExitCode.ShouldBe(ExitCode.ChangesFound);
        drift.Out.ShouldContain($"report saved to {ReportPath}");
        report.Summary.ReposChanged.ShouldBe(2);
        apply.ExitCode.ShouldBe(ExitCode.Success, apply.Out + apply.Error);
        again.ExitCode.ShouldBe(ExitCode.Success, again.Out + again.Error);
    }

    /// <summary>
    /// Given drift,
    /// when drift runs with --json,
    /// then standard output is only the report in the plan file schema, and the verdict goes to standard error.
    /// </summary>
    [Fact(DisplayName = "👍 drift --json prints the report to standard output")]
    public async Task Drift_Json_PrintsReport()
    {
        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "drift", "-p", s_policy, "--json");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.ChangesFound);
        JsonNode report = JsonNode.Parse(run.Out)!;
        report["$schema"]!.GetValue<string>().ShouldBe(PlanDocument.SchemaUri);
        report["summary"]!["reposChanged"]!.GetValue<int>().ShouldBe(2);
        run.Error.ShouldContain("Drift found: 2 of 2 repos differ from the policy.");
    }

    /// <summary>
    /// Given an unreachable GitHub target and --report,
    /// when drift runs, and again with --skip-unreachable,
    /// then the first exits 1, because the target could be hiding drift, and still saves an incomplete report;
    /// the second leaves the target out with a warning and exits 5 for the drift on the other target.
    /// </summary>
    [Fact(DisplayName = "👎 An unreachable target fails the drift check")]
    public async Task Drift_Unreachable_ExitsOne()
    {
        // Arrange
        _github.DescribeFailure = new HttpRequestException("No such host is known.");

        // Act
        CliRun failed = await CliRun.InvokeAsync(Services, "drift", "-p", s_policy, "--report", ReportPath, "-o", "plain");
        bool reportSaved = File.Exists(ReportPath);
        CliRun skipped = await CliRun.InvokeAsync(Services, "drift", "-p", s_policy, "--skip-unreachable", "-o", "plain");

        // Assert
        failed.ExitCode.ShouldBe(ExitCode.RuntimeFailure);
        failed.Out.ShouldContain("error SP1001: Target 'github' is unreachable");
        failed.Out.ShouldContain("Drift check failed with 1 error, so some drift may not be shown.");
        failed.Error.ShouldContain("The drift report is incomplete");
        reportSaved.ShouldBeTrue();
        skipped.ExitCode.ShouldBe(ExitCode.ChangesFound, skipped.Out + skipped.Error);
        skipped.Out.ShouldContain("warning SP1001");
    }

    /// <summary>
    /// Given a repo whose live state can't be read,
    /// when drift runs,
    /// then it exits 1 and the verdict says the check failed.
    /// </summary>
    [Fact(DisplayName = "👎 A repo that can't be read fails the drift check")]
    public async Task Drift_ReadFailure_ExitsOne()
    {
        // Arrange
        _azureDevOps.AddRepo(s_ledger).ReadFailure = new HttpRequestException("500 Internal Server Error");

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "drift", "-p", s_policy, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.RuntimeFailure, run.Out + run.Error);
        run.Out.ShouldContain("failed ado-onprem/Payments/legacy-ledger");
        run.Out.ShouldContain("Drift check failed with 1 error, so some drift may not be shown. 1 repo drifted among those that were read.");
    }

    /// <summary>
    /// Given an invalid policy,
    /// when drift runs,
    /// then it exits 2 without contacting any target.
    /// </summary>
    [Fact(DisplayName = "👎 An invalid policy exits 2 before any target is contacted")]
    public async Task Drift_InvalidPolicy_ExitsTwo()
    {
        // Arrange
        string policy = Path.Combine(AppContext.BaseDirectory, "TestData", "prototype-policy-repo");
        _github.DescribeFailure = new InvalidOperationException("must not be contacted");

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "drift", "-p", policy, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.InvalidPolicyOrUsage);
        run.Error.ShouldContain("no target was contacted");
    }

    // Applies a fresh plan and copies what was written into the live state, so the estate matches the policy.
    private async Task RestoreAsync()
    {
        CliRun apply = await CliRun.InvokeAsync(Services, "apply", "--yes", "-p", s_policy);
        apply.ExitCode.ShouldBe(ExitCode.Success, apply.Out + apply.Error);
        CopyWrittenToLive();
    }

    // The fake adapter only records writes, so copy what was written into the live state, as a real target keeps it.
    private void CopyWrittenToLive()
    {
        foreach (FakeProviderAdapter adapter in new[] { _github, _azureDevOps })
        {
            foreach ((RepoRef repo, RepoChangeSet changes) in adapter.Applied.ToArray())
            {
                FakeProviderAdapter.FakeRepo live = adapter.AddRepo(repo);
                foreach (PlanObject written in changes.Objects.Where(o => o.Payload is not null))
                {
                    live.WithLive(written.Payload!["rule"]!.GetValue<string>(), written.Payload["value"]!.ToJsonString());
                }
            }

            adapter.Applied.Clear();
        }
    }
}
