using Setpoint.Adapters.AzureDevOps.Policy;
using Setpoint.Adapters.GitHub.Policy;
using Setpoint.Cli.Composition;
using Setpoint.Cli.Tests.TestSupport;
using Setpoint.Core;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Testing;

namespace Setpoint.Cli.Tests.Commands;

/// <summary>
/// Tests for <c>setpoint apply</c> end to end, with fake adapters for both platforms: applying a saved plan file,
/// a bare <c>apply --yes</c>, a stale plan, <c>--max-changes</c>, an unreadable plan file and an in-sync estate.
/// </summary>
public sealed class ApplyCommandTests : IDisposable
{
    private static readonly string s_policy = Path.Combine(AppContext.BaseDirectory, "TestData", "valid-policy-repo");

    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("setpoint-apply-");
    private readonly FakeProviderAdapter _github = new(Platform.GitHub, GitHubProviderBlockDefinition.Instance);
    private readonly FakeProviderAdapter _azureDevOps = new(Platform.AzureDevOps, AzureDevOpsProviderBlockDefinition.Instance);

    public ApplyCommandTests()
    {
        _github.AddRepo(new RepoRef("github", "quattordici", "payments-gateway"));
        _azureDevOps.AddRepo(new RepoRef("ado-onprem", "Payments", "legacy-ledger"));
    }

    private CliServices Services => CliServices.With([_github, _azureDevOps], new FixedTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero)));

    private string PlanPath => Path.Combine(_scratch.FullName, "plan.json");

    public void Dispose() => _scratch.Delete(recursive: true);

    /// <summary>
    /// Given a plan saved by plan --out with nothing live changed since,
    /// when apply runs with the plan file,
    /// then every planned repo is written, the summary counts them and the exit code is 0.
    /// </summary>
    [Fact(DisplayName = "👍 apply writes a saved plan and prints a summary")]
    public async Task Apply_SavedPlan_WritesAndSummarises()
    {
        // Arrange
        await SavePlanAsync();

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "apply", PlanPath, "-p", s_policy);

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success);
        run.Out.ShouldContain("applied        github/quattordici/payments-gateway");
        run.Out.ShouldContain("apply: repos applied=2 failed=0 not_attempted=0 skipped=0");
        _github.Applied.ShouldHaveSingleItem();
        _azureDevOps.Applied.ShouldHaveSingleItem();
    }

    /// <summary>
    /// Given a bare apply confirmed with --yes,
    /// when it runs,
    /// then it plans, prints the plan and applies it in one step.
    /// </summary>
    [Fact(DisplayName = "👍 apply --yes re-plans and applies in one step")]
    public async Task Apply_Yes_PlansAndApplies()
    {
        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "apply", "--yes", "-p", s_policy, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success);
        run.Out.ShouldContain(" plan policy=");
        run.Out.ShouldContain("apply: repos applied=2");
    }

    /// <summary>
    /// Given a saved plan and a live object that appeared after it was made,
    /// when apply runs with the plan file,
    /// then the plan is refused as stale with exit code 3 and nothing is written.
    /// </summary>
    [Fact(DisplayName = "👎 apply refuses a stale plan with exit code 3")]
    public async Task Apply_StalePlan_ExitsThree()
    {
        // Arrange
        await SavePlanAsync();
        _github.AddRepo(new RepoRef("github", "quattordici", "payments-gateway")).WithLive("requireCommentResolution", "false");

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "apply", PlanPath, "-p", s_policy, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.StalePlan);
        run.Out.ShouldContain("SP4001");
        run.Out.ShouldContain("apply: Nothing was written.");
        _github.Applied.ShouldBeEmpty();
        _azureDevOps.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a saved plan changing two repos,
    /// when apply runs with --max-changes 1,
    /// then the guard trips with exit code 4 and nothing is written.
    /// </summary>
    [Fact(DisplayName = "👎 apply trips --max-changes with exit code 4")]
    public async Task Apply_OverMaxChanges_ExitsFour()
    {
        // Arrange
        await SavePlanAsync();

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "apply", PlanPath, "-p", s_policy, "--max-changes", "1", "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.GuardTripped);
        run.Out.ShouldContain("SP4002");
        _github.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a plan file path that doesn't exist, or holds text that isn't a plan,
    /// when apply runs with it,
    /// then it says the plan file can't be read and exits 2 without contacting a target.
    /// </summary>
    [Theory(DisplayName = "👎 apply refuses a plan file it can't read")]
    [InlineData(null)]
    [InlineData("not json")]
    public async Task Apply_UnreadablePlanFile_ExitsTwo(string? content)
    {
        // Arrange
        if (content is not null)
        {
            await File.WriteAllTextAsync(PlanPath, content, TestContext.Current.CancellationToken);
        }

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "apply", PlanPath, "-p", s_policy);

        // Assert
        run.ExitCode.ShouldBe(ExitCode.InvalidPolicyOrUsage);
        run.Error.ShouldContain("Can't read the plan file");
        _github.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a saved plan whose repos are all in sync,
    /// when apply runs with it,
    /// then it says there is nothing to apply and exits 0.
    /// </summary>
    [Fact(DisplayName = "🖐️ apply with an in-sync plan has nothing to do")]
    public async Task Apply_InSyncPlan_NothingToApply()
    {
        // Arrange
        await SavePlanAsync();
        await CliRun.InvokeAsync(Services, "apply", PlanPath, "-p", s_policy);
        ApplyWrittenToLive();
        await SavePlanAsync();

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "apply", PlanPath, "-p", s_policy);

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success);
        run.Out.ShouldContain("Nothing to apply");
    }

    private async Task SavePlanAsync()
    {
        CliRun plan = await CliRun.InvokeAsync(Services, "plan", "-p", s_policy, "--out", PlanPath, "-o", "plain");
        plan.ExitCode.ShouldBe(ExitCode.Success, plan.Out + plan.Error);
    }

    // The fake adapter only records writes, so copy what was written into the live state, as a real target keeps it.
    private void ApplyWrittenToLive()
    {
        foreach (FakeProviderAdapter adapter in new[] { _github, _azureDevOps })
        {
            foreach ((RepoRef repo, Core.Providers.RepoChangeSet changes) in adapter.Applied.ToArray())
            {
                FakeProviderAdapter.FakeRepo live = adapter.AddRepo(repo);
                foreach (Core.Planning.PlanObject written in changes.Objects.Where(o => o.Payload is not null))
                {
                    live.WithLive(written.Payload!["rule"]!.GetValue<string>(), written.Payload["value"]!.ToJsonString());
                }
            }

            adapter.Applied.Clear();
        }
    }
}
