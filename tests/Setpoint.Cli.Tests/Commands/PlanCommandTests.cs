using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Policy;
using Setpoint.Adapters.GitHub.Policy;
using Setpoint.Cli.Composition;
using Setpoint.Cli.Tests.TestSupport;
using Setpoint.Core;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Testing;

namespace Setpoint.Cli.Tests.Commands;

/// <summary>
/// Tests for <c>setpoint plan</c> end to end, with fake adapters for both platforms: <c>--json</c>, <c>--out</c>,
/// <c>--exit-code</c>, and the exit codes for an invalid policy and an unreachable target.
/// </summary>
public sealed class PlanCommandTests : IDisposable
{
    private static readonly string s_policy = Path.Combine(AppContext.BaseDirectory, "TestData", "valid-policy-repo");

    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("setpoint-plan-");
    private readonly FakeProviderAdapter _github = new(Platform.GitHub, GitHubProviderBlockDefinition.Instance);
    private readonly FakeProviderAdapter _azureDevOps = new(Platform.AzureDevOps, AzureDevOpsProviderBlockDefinition.Instance);

    public PlanCommandTests()
    {
        // Every selector and the repo override in the test policy match a repo, so a plan has no selection diagnostics.
        _github.AddRepo(new RepoRef("github", "quattordici", "payments-gateway"));
        _azureDevOps.AddRepo(new RepoRef("ado-onprem", "Payments", "legacy-ledger"));
    }

    private CliServices Services => CliServices.With([_github, _azureDevOps], new FixedTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero)));

    public void Dispose() => _scratch.Delete(recursive: true);

    /// <summary>
    /// Given the hidden dev-only --record option,
    /// when plan runs,
    /// then the adapters it creates are told to record HTTP exchanges into that directory.
    /// </summary>
    [Fact(DisplayName = "🖐️ plan --record passes the recording directory to the adapters")]
    public async Task Plan_Record_PassesDirectoryToAdapters()
    {
        // Arrange
        AdapterSettings? settings = null;
        var services = new CliServices(
            given =>
            {
                settings = given;
                return [_github, _azureDevOps];
            },
            TimeProvider.System);
        string recordings = Path.Combine(_scratch.FullName, "fixtures");

        // Act
        await CliRun.InvokeAsync(services, "plan", "-p", s_policy, "--record", recordings);

        // Assert
        settings.ShouldNotBeNull().Http.RecordDirectory.ShouldBe(recordings);
    }

    /// <summary>
    /// Given the production services,
    /// when they create a run's adapters,
    /// then the GitHub adapter is among them.
    /// </summary>
    [Fact(DisplayName = "👍 The production services include the GitHub adapter")]
    public void Default_CreateAdapters_IncludesGitHub()
    {
        // Act
        IReadOnlyList<Setpoint.Core.Providers.IProviderAdapter> adapters = CliServices.Default.CreateAdapters(new AdapterSettings(new Setpoint.Http.HttpCoreOptions()));

        // Assert
        adapters.ShouldContain(a => a is Setpoint.Adapters.GitHub.GitHubAdapter);
        foreach (IDisposable adapter in adapters.OfType<IDisposable>())
        {
            adapter.Dispose();
        }
    }

    /// <summary>
    /// Given the test policy and repos with nothing live,
    /// when plan runs with --json,
    /// then standard output is only the plan, in the plan file schema, with the changes for each repo.
    /// </summary>
    [Fact(DisplayName = "👍 plan --json prints the plan file schema to standard output")]
    public async Task Plan_Json_PrintsPlan()
    {
        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "plan", "-p", s_policy, "--json");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success);
        JsonNode plan = JsonNode.Parse(run.Out)!;
        plan["$schema"]!.GetValue<string>().ShouldBe(PlanDocument.SchemaUri);
        plan["setpointVersion"]!.GetValue<string>().ShouldBe(SetpointVersion.Current);
        plan["targets"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ShouldBe(["ado-onprem", "ado-cloud", "github"]);
        plan["repos"]!.AsArray().Count.ShouldBe(2);
        plan["summary"]!["reposChanged"]!.GetValue<int>().ShouldBe(2);
    }

    /// <summary>
    /// Given repos with changes,
    /// when plan runs with --out,
    /// then the plan file is written and reads back, and the repo override's rules show in the ADO repo's changes.
    /// </summary>
    [Fact(DisplayName = "👍 plan --out saves a plan file that reads back")]
    public async Task Plan_Out_WritesFile()
    {
        // Arrange
        string file = Path.Combine(_scratch.FullName, "out", "plan.json");

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "plan", "-p", s_policy, "--out", file, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.Success);
        run.Out.ShouldContain($"plan saved to {file}");
        PlanDocument plan = PlanFile.Deserialize(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        PlanRepo ledger = plan.Repos.Single(r => r.Path == "Payments/legacy-ledger");
        ledger.Layers.ShouldBe(["baseline", "payments", "repo"]);
        ledger.Changes.ShouldContain(c => c.Rule == "allowedMergeTypes" && c.To == "merge, squash" && c.Layer == "repo");
        ledger.Changes.ShouldNotContain(c => c.Rule == "requiredChecks");
    }

    /// <summary>
    /// Given changes, and then a repo already in line,
    /// when plan runs with --exit-code,
    /// then it exits 5 with changes and 0 without.
    /// </summary>
    [Fact(DisplayName = "🖐️ plan --exit-code returns 5 only when there are changes")]
    public async Task Plan_ExitCode_FiveOnChanges()
    {
        // Arrange
        var inLine = new FakeProviderAdapter(Platform.GitHub, GitHubProviderBlockDefinition.Instance);
        var adoEmpty = new FakeProviderAdapter(Platform.AzureDevOps, AzureDevOpsProviderBlockDefinition.Instance);
        string policy = CopyPolicyWithoutAdoSelectors();

        // Act
        CliRun withChanges = await CliRun.InvokeAsync(Services, "plan", "-p", s_policy, "--exit-code", "-o", "plain");
        CliRun withoutChanges = await CliRun.InvokeAsync(CliServices.With([inLine, adoEmpty], TimeProvider.System), "plan", "-p", policy, "--exit-code", "-o", "plain");

        // Assert
        withChanges.ExitCode.ShouldBe(ExitCode.ChangesFound);
        withoutChanges.ExitCode.ShouldBe(ExitCode.Success, withoutChanges.Out + withoutChanges.Error);
    }

    /// <summary>
    /// Given an invalid policy,
    /// when plan runs,
    /// then it exits 2 without contacting any target.
    /// </summary>
    [Fact(DisplayName = "👎 An invalid policy exits 2 before any target is contacted")]
    public async Task Plan_InvalidPolicy_ExitsTwo()
    {
        // Arrange
        string policy = Path.Combine(AppContext.BaseDirectory, "TestData", "prototype-policy-repo");
        _github.DescribeFailure = new InvalidOperationException("must not be contacted");

        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "plan", "-p", policy, "-o", "plain");

        // Assert
        run.ExitCode.ShouldBe(ExitCode.InvalidPolicyOrUsage);
        run.Error.ShouldContain("no target was contacted");
        run.Out.ShouldNotContain("must not be contacted");
    }

    /// <summary>
    /// Given an unreachable GitHub target and --out,
    /// when plan runs,
    /// then it exits 1 and writes no plan file; with --skip-unreachable it writes one without that target.
    /// </summary>
    [Fact(DisplayName = "👎 An unreachable target exits 1 and writes no plan file")]
    public async Task Plan_Unreachable_NoFile()
    {
        // Arrange
        string file = Path.Combine(_scratch.FullName, "plan.json");
        _github.DescribeFailure = new HttpRequestException("No such host is known.");

        // Act
        CliRun failed = await CliRun.InvokeAsync(Services, "plan", "-p", s_policy, "--out", file, "-o", "plain");
        bool writtenWhenFailed = File.Exists(file);
        CliRun skipped = await CliRun.InvokeAsync(Services, "plan", "-p", s_policy, "--out", file, "--skip-unreachable", "-o", "plain");

        // Assert
        failed.ExitCode.ShouldBe(ExitCode.RuntimeFailure);
        failed.Out.ShouldContain("error SP1001: Target 'github' is unreachable");
        writtenWhenFailed.ShouldBeFalse();
        skipped.ExitCode.ShouldBe(ExitCode.Success);
        skipped.Out.ShouldContain("warning SP1001");
        PlanFile.Deserialize(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Targets.Select(t => t.Name).ShouldNotContain("github");
    }

    /// <summary>
    /// Given a repo with changes,
    /// when plan runs with plain output,
    /// then each change is one line with its rule, from, to and layer, followed by the summary.
    /// </summary>
    [Fact(DisplayName = "👍 Plain output prints one line per change and a summary")]
    public async Task Plan_Plain_OneLinePerChange()
    {
        // Act
        CliRun run = await CliRun.InvokeAsync(Services, "plan", "-p", s_policy, "-o", "plain");

        // Assert
        run.Out.ShouldContain("+ github/quattordici/payments-gateway minimumReviewers: (none) -> count 2, resetOnPush [baseline] (locked) (create Fake minimumReviewers (fake:new/minimumReviewers))");
        run.Out.ShouldContain("summary repos_changed=2 ");
        run.Out.ShouldContain("max_changes=2/25");
        run.Out.ShouldNotContain("\u001b");
    }

    // A copy of the test policy whose only targets are a GitHub one without repos and an ADO one, with the payments
    // group and the repo override removed, so an empty estate plans in sync.
    private string CopyPolicyWithoutAdoSelectors()
    {
        string root = Path.Combine(_scratch.FullName, "policy");
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(s_policy, "targets.yml"), Path.Combine(root, "targets.yml"));
        File.Copy(Path.Combine(s_policy, "baseline.yml"), Path.Combine(root, "baseline.yml"));
        return root;
    }
}
