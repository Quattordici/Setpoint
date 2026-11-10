using Setpoint.Cli.Rendering;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy.Model;
using Spectre.Console.Testing;

namespace Setpoint.Cli.Tests.Rendering;

/// <summary>
/// Tests for <see cref="PlanRenderer"/> on the prototype's sample estate: the target table, the per-repo tree with
/// native lines, lock marks and inline warnings, the summary panel, the plain one-line-per-change layout, and which
/// repos are shown.
/// </summary>
public sealed class PlanRendererTests
{
    private readonly TestConsole _out = new TestConsole().Width(160);
    private readonly StringWriter _plain = new();

    private CliConsole Console => new(_out, new TestConsole(), _plain, new StringWriter());

    /// <summary>
    /// Given the sample plan,
    /// when it is rendered as a tree,
    /// then the target table lists detected version, API, auth, identity and repo count for each target.
    /// </summary>
    [Fact(DisplayName = "👍 The tree layout starts with a target table")]
    public void Tree_Targets_ShowsTable()
    {
        // Act
        PlanRenderer.Write(Console, Sample(), OutputFormat.Tree, verbose: false, savedTo: null);

        // Assert
        string output = _out.Output;
        output.ShouldContain("Setpoint 0.1.0-alpha · plan · policy 3f9c2a1 (5 files) · 4 repos in scope");
        output.ShouldContain("ado-onprem");
        output.ShouldContain("Azure DevOps Server 2022.1");
        output.ShouldContain("integrated");
        output.ShouldContain(@"CONTOSO\setpoint-svc");
    }

    /// <summary>
    /// Given a repo with an update on a locked rule and a create,
    /// when it is rendered as a tree,
    /// then each change shows from → to [layer], the lock mark, and the native change on the line below.
    /// </summary>
    [Fact(DisplayName = "👍 Each change shows from → to, layer, lock and its native change")]
    public void Tree_Changes_ShowNativeLine()
    {
        // Act
        PlanRenderer.Write(Console, Sample(), OutputFormat.Tree, verbose: false, savedTo: null);

        // Assert
        string[] lines = _out.Lines.ToArray();
        int change = Array.FindIndex(lines, l => l.Contains("~ minimumReviewers.count", StringComparison.Ordinal));
        change.ShouldBeGreaterThan(-1);
        lines[change].ShouldContain("1 → 2  [baseline] " + PlanRenderer.LockMark);
        lines[change + 1].ShouldContain("update Minimum number of reviewers #17 (ado:policy/17)");
        _out.Output.ShouldContain("Payments/payments-api  baseline → payments  2 changes  2 unmanaged");
        _out.Output.ShouldContain("create Build validation (new) (ado:new/build/payments-ci)");
    }

    /// <summary>
    /// Given an in-sync repo with an SP2201 warning, one without warnings, and a skipped repo,
    /// when the plan is rendered without --verbose,
    /// then the warning shows inline under its repo, the skipped repo shows, and the quiet in-sync repo is hidden.
    /// </summary>
    [Fact(DisplayName = "🖐️ Quiet in-sync repos are hidden; warnings show inline")]
    public void Tree_InSync_HiddenUnlessWarned()
    {
        // Act
        PlanRenderer.Write(Console, Sample(), OutputFormat.Tree, verbose: false, savedTo: null);

        // Assert
        string output = _out.Output;
        output.ShouldContain("Platform/build-tools  baseline  in sync");
        output.ShouldContain("⚠ SP2201 minimumReviewers: 2 live objects match");
        output.ShouldNotContain("ado-onprem/Platform/build-tools: minimumReviewers");
        output.ShouldContain("quattordici/payments-gateway");
        output.ShouldNotContain("Web/storefront");
    }

    /// <summary>
    /// Given the sample plan,
    /// when it is rendered with --verbose,
    /// then quiet in-sync repos and unmanaged settings are listed too.
    /// </summary>
    [Fact(DisplayName = "🖐️ --verbose shows in-sync repos and unmanaged settings")]
    public void Tree_Verbose_ShowsEverything()
    {
        // Act
        PlanRenderer.Write(Console, Sample(), OutputFormat.Tree, verbose: true, savedTo: null);

        // Assert
        _out.Output.ShouldContain("Web/storefront");
        _out.Output.ShouldContain("· unmanaged requiredChecks on ado:policy/40: nightly");
    }

    /// <summary>
    /// Given the sample plan saved to a file,
    /// when it is rendered as a tree,
    /// then the summary panel shows repo and change counts, max-changes usage, unmanaged, warnings and where it was saved.
    /// </summary>
    [Fact(DisplayName = "👍 The summary panel totals the plan")]
    public void Tree_Summary_Totals()
    {
        // Act
        PlanRenderer.Write(Console, Sample(), OutputFormat.Tree, verbose: false, savedTo: "/tmp/plan.json");

        // Assert
        string output = _out.Output;
        output.ShouldContain("1 to change, 2 in sync, 1 skipped  (max-changes 1/25)");
        output.ShouldContain("1 create, 1 update, 0 remove  → 2 native writes");
        output.ShouldContain("3 settings left alone");
        output.ShouldContain("(SP2201, SP3001; --strict would fail)");
        output.ShouldContain("/tmp/plan.json  → setpoint apply plan.json");
    }

    /// <summary>
    /// Given a plan with more repos to change than --max-changes allows, and one with the limit off,
    /// when the summary is rendered,
    /// then the first says apply would refuse and the second says the limit is off.
    /// </summary>
    [Theory(DisplayName = "👎 The summary flags a plan over the change limit")]
    [InlineData(2, "(max-changes 3/2: apply would refuse; raise --max-changes)")]
    [InlineData(0, "(max-changes off)")]
    public void Tree_MaxChanges_Flagged(int limit, string expected)
    {
        // Arrange
        PlanDocument plan = Sample();
        plan = plan with
        {
            Options = plan.Options with { MaxChanges = limit },
            Summary = plan.Summary with { ReposChanged = 3 },
        };

        // Act
        PlanRenderer.Write(Console, plan, OutputFormat.Tree, verbose: false, savedTo: null);

        // Assert
        _out.Output.ShouldContain(expected);
    }

    /// <summary>
    /// Given the sample plan,
    /// when it is rendered plain,
    /// then each change is one unwrapped line with its native change, warnings and skips have their own lines,
    /// and there are no escape codes.
    /// </summary>
    [Fact(DisplayName = "👍 Plain output is one line per change with no escape codes")]
    public void Plain_Changes_OneLineEach()
    {
        // Act
        PlanRenderer.Write(Console, Sample(), OutputFormat.Plain, verbose: false, savedTo: "plan.json");

        // Assert
        string[] lines = _plain.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldContain("setpoint 0.1.0-alpha plan policy=3f9c2a1 files=5 repos=4");
        lines.ShouldContain(@"target ado-onprem detected=""Azure DevOps Server 2022.1"" api=7.0 auth=integrated identity=CONTOSO\setpoint-svc repos=3");
        lines.ShouldContain("~ ado-onprem/Payments/payments-api minimumReviewers.count: 1 -> 2 [baseline] (locked) (update Minimum number of reviewers #17 (ado:policy/17))");
        lines.ShouldContain("warning SP2201 ado-onprem/Platform/build-tools: minimumReviewers: 2 live objects match type + branch main (ado:policy/31, ado:policy/32); left alone.");
        lines.ShouldContain("skip github/quattordici/payments-gateway");
        lines.ShouldContain("summary repos_changed=1 create=1 update=1 remove=0 in_sync=2 skipped=1 failed=0 unmanaged=3 warnings=2 max_changes=1/25");
        lines.ShouldContain("plan saved to plan.json");
        lines.ShouldNotContain(l => l.Contains('\u001b', StringComparison.Ordinal) || l.Contains("Web/storefront", StringComparison.Ordinal));
        _out.Output.ShouldBeEmpty();
    }

    /// <summary>
    /// Given the sample plan,
    /// when it is rendered as a tree in the drift view,
    /// then it reads as drift: the header, the repo status, the summary panel and a closing verdict, with no change limit.
    /// </summary>
    [Fact(DisplayName = "👍 The drift view reads as differences and ends with a verdict")]
    public void Tree_DriftView_ReadsAsDrift()
    {
        // Act
        PlanRenderer.Write(Console, Sample(), OutputFormat.Tree, verbose: false, savedTo: "/tmp/drift.json", PlanView.Drift);

        // Assert
        string output = _out.Output;
        output.ShouldContain("Setpoint 0.1.0-alpha · drift · policy 3f9c2a1");
        output.ShouldContain("2 differences");
        output.ShouldContain("1 drifted, 2 in sync, 1 skipped");
        output.ShouldContain("To restore");
        output.ShouldContain("/tmp/drift.json  → review it, then setpoint apply drift.json to restore");
        output.ShouldContain("Drift found: 1 of 3 repos differ from the policy.");
        output.ShouldNotContain("max-changes");
        output.ShouldNotContain("to change");
    }

    /// <summary>
    /// Given the sample plan,
    /// when it is rendered plain in the drift view,
    /// then the header and summary say drift, the change limit is left out, and the last line is the verdict.
    /// </summary>
    [Fact(DisplayName = "👍 Plain drift output ends with the verdict line")]
    public void Plain_DriftView_EndsWithVerdict()
    {
        // Act
        PlanRenderer.Write(Console, Sample(), OutputFormat.Plain, verbose: false, savedTo: "drift.json", PlanView.Drift);

        // Assert
        string[] lines = _plain.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldContain("setpoint 0.1.0-alpha drift policy=3f9c2a1 files=5 repos=4");
        lines.ShouldContain("summary repos_drifted=1 create=1 update=1 remove=0 in_sync=2 skipped=1 failed=0 unmanaged=3 warnings=2");
        lines.ShouldContain("report saved to drift.json");
        lines[^1].ShouldBe("Drift found: 1 of 3 repos differ from the policy.");
    }

    /// <summary>
    /// Given an in-sync plan, and one where a repo couldn't be read,
    /// when the drift verdict is made,
    /// then the first says there is no drift and the second says the check failed, because unread repos can hide drift.
    /// </summary>
    [Fact(DisplayName = "👎 The drift verdict fails when a repo couldn't be read")]
    public void DriftVerdict_ReadFailure_Fails()
    {
        // Arrange
        PlanDocument sample = Sample();
        PlanDocument inSync = sample with { Summary = sample.Summary with { ReposChanged = 0, InSync = 3 } };
        PlanDocument failed = sample with { Summary = sample.Summary with { Errors = 1 } };

        // Act
        string inSyncVerdict = PlanRenderer.DriftVerdict(inSync);
        string failedVerdict = PlanRenderer.DriftVerdict(failed);

        // Assert
        inSyncVerdict.ShouldBe("No drift: 3 repos match the policy.");
        failedVerdict.ShouldBe("Drift check failed with 1 error, so some drift may not be shown. 1 repo drifted among those that were read.");
    }

    private static PlanDocument Sample() => new()
    {
        FormatVersion = 1,
        SetpointVersion = "0.1.0-alpha",
        CreatedAt = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero),
        Policy = new PlanPolicy("sha256:700dc9956fa84b9e700dc9956fa84b9e", 5, "3f9c2a1e5b7d9f0a1c3e5b7d9f0a1c3e5b7d9f0a"),
        Options = new PlanOptionsRecord(false, false, false, 25),
        Targets =
        [
            new PlanTarget("ado-onprem", Platform.AzureDevOps, "Azure DevOps Server 2022.1", "7.0", AuthMethod.Integrated, @"CONTOSO\setpoint-svc", 3),
            new PlanTarget("github", Platform.GitHub, "github.com (Free)", "2022-11-28", AuthMethod.Pat, "setpoint-bot", 1),
        ],
        Repos =
        [
            Repo("ado-onprem", "Payments/payments-api", PlanRepoStatus.Changes, ["baseline", "payments"]) with
            {
                Objects =
                [
                    new PlanObject("ado:policy/17", ObjectOperation.Update, "sha256:1", "ado:minimumReviewers", null, "Minimum number of reviewers #17"),
                    new PlanObject("ado:new/build/payments-ci", ObjectOperation.Create, "sha256:2", "ado:build/payments-ci", null, "Build validation (new)"),
                ],
                Changes =
                [
                    new PlanChange("minimumReviewers.count", ChangeAction.Update, "1", "2", "baseline", true, "ado:policy/17"),
                    new PlanChange("requiredChecks", ChangeAction.Create, "(none)", "payments-ci", "group payments", false, "ado:new/build/payments-ci"),
                ],
                Unmanaged = [new PlanUnmanaged("requiredChecks", "ado:policy/40", "nightly"), new PlanUnmanaged("requireCommentResolution", "ado:policy/41", "true")],
            },
            Repo("ado-onprem", "Platform/build-tools", PlanRepoStatus.InSync, ["baseline"]) with
            {
                Unmanaged = [new PlanUnmanaged("minimumReviewers", "ado:policy/31", "count 1")],
                Diagnostics = [new PlanDiagnostic(DiagnosticCodes.AmbiguousMatch, DiagnosticSeverity.Warning, "ado-onprem/Platform/build-tools: minimumReviewers: 2 live objects match type + branch main (ado:policy/31, ado:policy/32); left alone.")],
            },
            Repo("ado-onprem", "Web/storefront", PlanRepoStatus.InSync, ["baseline"]),
            Repo("github", "quattordici/payments-gateway", PlanRepoStatus.Skipped, ["baseline", "payments"]) with
            {
                Diagnostics = [new PlanDiagnostic(DiagnosticCodes.PrivateRepoOnGitHubFree, DiagnosticSeverity.Warning, "github/quattordici/payments-gateway: private repo on GitHub Free; skipped.")],
            },
        ],
        Diagnostics = [],
        Summary = new PlanSummary(ReposChanged: 1, Create: 1, Update: 1, Remove: 0, InSync: 2, Skipped: 1, Errors: 0, Unmanaged: 3, Warnings: 2),
    };

    private static PlanRepo Repo(string target, string path, PlanRepoStatus status, IReadOnlyList<string> layers) => new()
    {
        Target = target,
        Path = path,
        Status = status,
        Layers = layers,
        Objects = [],
        Changes = [],
        Unmanaged = [],
        Diagnostics = [],
    };
}
