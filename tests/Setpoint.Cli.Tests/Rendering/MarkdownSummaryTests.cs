using Setpoint.Cli.Rendering;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;

namespace Setpoint.Cli.Tests.Rendering;

/// <summary>
/// Tests for <see cref="MarkdownSummary"/> on the renderer's sample estate: the drift and plan summaries, the cap on
/// changes per repo, errors, Markdown escaping, appending to an existing file, and that no payload reaches it.
/// </summary>
public sealed class MarkdownSummaryTests : IDisposable
{
    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("setpoint-summary-");

    public void Dispose() => _scratch.Delete(recursive: true);

    /// <summary>
    /// Given the sample plan,
    /// when it is summarised as a drift check,
    /// then the summary has the headline, the verdict, the counts, a table of drifted repos, each change on one line,
    /// and warnings by code, with in-sync and skipped repos counted but not listed.
    /// </summary>
    [Fact(DisplayName = "👍 The drift summary lists drifted repos, their changes and warnings")]
    public void Render_Drift_ListsDriftedRepos()
    {
        // Act
        string md = MarkdownSummary.Render(PlanRendererTests.Sample(), PlanView.Drift);

        // Assert
        md.ShouldBe(
            """
            ## Setpoint drift: drift found

            Drift found: 1 of 3 repos differ from the policy.

            Policy `3f9c2a1` · 2 targets · 4 repos in scope · 2 in sync · 1 skipped · 0 failed

            | Repo | Layers | Differences |
            |---|---|---:|
            | `ado-onprem/Payments/payments-api` | baseline → payments | 2 |

            **`ado-onprem/Payments/payments-api`**

            - `~` `minimumReviewers.count`: 1 → 2 (baseline, locked)
            - `+` `requiredChecks`: (none) → payments-ci (group payments)

            ### Warnings

            | Code | Count | First |
            |---|---:|---|
            | `SP2201` | 1 | ado-onprem/Platform/build-tools: minimumReviewers: 2 live objects match type + branch main (ado:policy/31, ado:policy/32); left alone. |
            | `SP3001` | 1 | github/quattordici/payments-gateway: private repo on GitHub Free; skipped. |


            """.ReplaceLineEndings("\n"));
        md.ShouldNotContain("Web/storefront");
    }

    /// <summary>
    /// Given the sample plan,
    /// when it is summarised as a plan,
    /// then the headline and verdict speak of changes to apply.
    /// </summary>
    [Fact(DisplayName = "👍 The plan summary speaks of changes to apply")]
    public void Render_Plan_SaysChanges()
    {
        // Act
        string md = MarkdownSummary.Render(PlanRendererTests.Sample(), PlanView.Plan);

        // Assert
        md.ShouldStartWith("## Setpoint plan: changes to apply\n\n1 repo to change: 1 create, 1 update, 0 remove.\n");
        md.ShouldContain("| Repo | Layers | Changes |");
    }

    /// <summary>
    /// Given a repo with more changes than the cap,
    /// when it is summarised,
    /// then only the first changes are listed, followed by an "and N more" line.
    /// </summary>
    [Fact(DisplayName = "🖐️ Changes per repo are capped with an \"and N more\" line")]
    public void Render_ManyChanges_Capped()
    {
        // Arrange
        PlanDocument sample = PlanRendererTests.Sample();
        PlanRepo repo = sample.Repos[0];
        PlanChange[] changes = [.. Enumerable.Range(1, MarkdownSummary.ChangesPerRepo + 3)
            .Select(i => new PlanChange($"rule{i}", ChangeAction.Update, "a", "b", "baseline", false, "ado:policy/17"))];
        PlanDocument plan = sample with { Repos = [repo with { Changes = changes }, .. sample.Repos.Skip(1)] };

        // Act
        string md = MarkdownSummary.Render(plan, PlanView.Drift);

        // Assert
        md.ShouldContain($"`rule{MarkdownSummary.ChangesPerRepo}`");
        md.ShouldNotContain($"`rule{MarkdownSummary.ChangesPerRepo + 1}`");
        md.ShouldContain("- and 3 more\n");
    }

    /// <summary>
    /// Given a run with an unreachable target, and values holding Markdown and HTML characters,
    /// when it is summarised,
    /// then the headline says failed, the error is listed, and the characters are escaped so they show as text.
    /// </summary>
    [Fact(DisplayName = "👎 Errors are listed and Markdown in values is escaped")]
    public void Render_ErrorsAndMarkdown_ListedAndEscaped()
    {
        // Arrange
        PlanDocument sample = PlanRendererTests.Sample();
        PlanRepo repo = sample.Repos[0] with
        {
            Changes = [new PlanChange("requiredChecks", ChangeAction.Create, "(none)", "ci | <b>*build*</b>", "group payments", false, "ado:policy/17")],
        };
        PlanDocument plan = sample with
        {
            Repos = [repo, .. sample.Repos.Skip(1)],
            Diagnostics = [new PlanDiagnostic(DiagnosticCodes.TargetUnreachable, DiagnosticSeverity.Error, "Target 'github' is unreachable (No such host is known.).")],
        };

        // Act
        string md = MarkdownSummary.Render(plan, PlanView.Drift);

        // Assert
        md.ShouldStartWith("## Setpoint drift: failed\n");
        md.ShouldContain("### Errors\n\n- `SP1001` Target 'github' is unreachable (No such host is known.).\n");
        md.ShouldContain(@"(none) → ci \| \<b\>\*build\*\</b\> (group payments)");
    }

    /// <summary>
    /// Given a summary file another step already wrote to, as $GITHUB_STEP_SUMMARY may be,
    /// when the summary is appended,
    /// then the earlier text is kept and the summary follows it.
    /// </summary>
    [Fact(DisplayName = "👍 The summary is appended to an existing file")]
    public async Task AppendAsync_ExistingFile_Appends()
    {
        // Arrange
        var file = new FileInfo(Path.Combine(_scratch.FullName, "nested", "summary.md"));
        file.Directory!.Create();
        await File.WriteAllTextAsync(file.FullName, "# Earlier step\n\n", TestContext.Current.CancellationToken);

        // Act
        await MarkdownSummary.AppendAsync(file, PlanRendererTests.Sample(), PlanView.Drift, TestContext.Current.CancellationToken);

        // Assert
        string text = await File.ReadAllTextAsync(file.FullName, TestContext.Current.CancellationToken);
        text.ShouldStartWith("# Earlier step\n\n## Setpoint drift: drift found\n");
    }

    /// <summary>
    /// Given a plan whose objects carry native payloads,
    /// when it is summarised,
    /// then no payload, fingerprint or object key reaches the summary.
    /// </summary>
    [Fact(DisplayName = "🛡️ No payload or fingerprint reaches the summary")]
    public void Render_Payloads_LeftOut()
    {
        // Arrange
        PlanDocument sample = PlanRendererTests.Sample();
        PlanRepo repo = sample.Repos[0] with
        {
            Objects = [new PlanObject("ado:policy/17", ObjectOperation.Update, "sha256:feedface", "ado:minimumReviewers", System.Text.Json.Nodes.JsonNode.Parse("""{"secretish":"payload-value"}"""))],
        };
        PlanDocument plan = sample with { Repos = [repo, .. sample.Repos.Skip(1)] };

        // Act
        string md = MarkdownSummary.Render(plan, PlanView.Drift);

        // Assert
        md.ShouldNotContain("payload-value");
        md.ShouldNotContain("sha256:feedface");
        md.ShouldNotContain("ado:policy/17");
    }
}
