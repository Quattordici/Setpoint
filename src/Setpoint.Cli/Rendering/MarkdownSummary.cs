using System.Globalization;
using System.Text;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;

namespace Setpoint.Cli.Rendering;

/// <summary>
/// Writes a short Markdown summary of a plan or a drift check for a pipeline's run page: GitHub Actions'
/// <c>$GITHUB_STEP_SUMMARY</c> or Azure Pipelines' <c>##vso[task.uploadsummary]</c>. It has a verdict, a table of
/// the repos that change, each change as one line (capped per repo), failed repos and warnings by code.
/// In-sync and skipped repos are counted, not listed, so it stays short at a hundred repos. It never holds a native
/// payload or a secret: only what the neutral view of the plan already shows people.
/// </summary>
internal static class MarkdownSummary
{
    /// <summary>The most changes listed for one repo; the rest are counted on an "and N more" line.</summary>
    public const int ChangesPerRepo = 10;

    /// <summary>The summary as Markdown, ending in a blank line so a second summary appended after it stays apart.</summary>
    public static string Render(PlanDocument plan, PlanView view)
    {
        bool drift = view == PlanView.Drift;
        PlanSummary s = plan.Summary;
        var md = new StringBuilder();

        md.Append("## Setpoint ").Append(drift ? "drift" : "plan").Append(": ").AppendLine(Headline(plan, drift)).AppendLine();
        md.AppendLine(drift ? PlanRenderer.DriftVerdict(plan) : PlanVerdict(plan)).AppendLine();
        md.Append("Policy ").Append(Code(PolicyLabel(plan.Policy)))
            .Append(" · ").Append(Count(plan.Targets.Count, "target"))
            .Append(" · ").Append(Count(plan.Repos.Count, "repo")).Append(" in scope")
            .Append(" · ").Append(s.InSync.ToString(CultureInfo.InvariantCulture)).Append(" in sync")
            .Append(" · ").Append(s.Skipped.ToString(CultureInfo.InvariantCulture)).Append(" skipped")
            .Append(" · ").Append(s.Errors.ToString(CultureInfo.InvariantCulture)).AppendLine(" failed")
            .AppendLine();

        PlanRepo[] changed = [.. plan.Repos.Where(r => r.Status == PlanRepoStatus.Changes)];
        if (changed.Length > 0)
        {
            md.Append("| Repo | Layers | ").Append(drift ? "Differences" : "Changes").AppendLine(" |");
            md.AppendLine("|---|---|---:|");
            foreach (PlanRepo repo in changed)
            {
                md.Append("| ").Append(Code(Name(repo))).Append(" | ").Append(Text(string.Join(" → ", repo.Layers))).Append(" | ")
                    .Append(repo.Changes.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
            }

            md.AppendLine();
            foreach (PlanRepo repo in changed)
            {
                md.Append("**").Append(Code(Name(repo))).AppendLine("**").AppendLine();
                foreach (PlanChange change in repo.Changes.Take(ChangesPerRepo))
                {
                    string locked = change.Locked ? ", locked" : string.Empty;
                    md.Append("- `").Append(Symbol(change.Action)).Append("` ").Append(Code(change.Rule)).Append(": ")
                        .Append(Text(change.From)).Append(" → ").Append(Text(change.To))
                        .Append(" (").Append(Text(change.Layer)).Append(locked).AppendLine(")");
                }

                if (repo.Changes.Count > ChangesPerRepo)
                {
                    md.Append("- and ").Append((repo.Changes.Count - ChangesPerRepo).ToString(CultureInfo.InvariantCulture)).AppendLine(" more");
                }

                md.AppendLine();
            }
        }

        PlanDiagnostic[] errors = [.. AllDiagnostics(plan).Where(d => d.Severity == DiagnosticSeverity.Error)];
        if (errors.Length > 0)
        {
            md.AppendLine("### Errors").AppendLine();
            foreach (PlanDiagnostic error in errors)
            {
                md.Append("- ").Append(Code(error.Code)).Append(' ').AppendLine(Text(error.Message));
            }

            md.AppendLine();
        }

        var warnings = AllDiagnostics(plan).Where(d => d.Severity == DiagnosticSeverity.Warning)
            .GroupBy(d => d.Code, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();
        if (warnings.Length > 0)
        {
            md.AppendLine("### Warnings").AppendLine();
            md.AppendLine("| Code | Count | First |");
            md.AppendLine("|---|---:|---|");
            foreach (IGrouping<string, PlanDiagnostic> group in warnings)
            {
                md.Append("| ").Append(Code(group.Key)).Append(" | ").Append(group.Count().ToString(CultureInfo.InvariantCulture))
                    .Append(" | ").Append(Text(group.First().Message)).AppendLine(" |");
            }

            md.AppendLine();
        }

        return md.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Appends the summary to <paramref name="file"/>, creating it and its directory when missing. It appends because
    /// <c>$GITHUB_STEP_SUMMARY</c> may already hold another step's summary.
    /// </summary>
    public static async Task AppendAsync(FileInfo file, PlanDocument plan, PlanView view, CancellationToken cancellationToken)
    {
        file.Directory?.Create();
        await File.AppendAllTextAsync(file.FullName, Render(plan, view), cancellationToken);
    }

    private static string Headline(PlanDocument plan, bool drift)
    {
        bool failed = plan.Summary.Errors > 0 || AllDiagnostics(plan).Any(d => d.Severity == DiagnosticSeverity.Error);
        if (failed)
        {
            return "failed";
        }

        return drift
            ? (plan.HasChanges ? "drift found" : "no drift")
            : (plan.HasChanges ? "changes to apply" : "in sync");
    }

    private static string PlanVerdict(PlanDocument plan)
    {
        PlanSummary s = plan.Summary;
        return plan.HasChanges
            ? $"{Count(s.ReposChanged, "repo")} to change: {s.Create} create, {s.Update} update, {s.Remove} remove."
            : "Nothing to change.";
    }

    private static IEnumerable<PlanDiagnostic> AllDiagnostics(PlanDocument plan) =>
        plan.Diagnostics.Concat(plan.Repos.SelectMany(r => r.Diagnostics));

    private static string PolicyLabel(PlanPolicy policy) =>
        policy.Commit is { Length: >= 7 } commit ? commit[..7] : policy.Hash[Fingerprint.Prefix.Length..][..12];

    private static string Name(PlanRepo repo) => $"{repo.Target}/{repo.Path}";

    private static string Symbol(ChangeAction action) => action switch
    {
        ChangeAction.Create => "+",
        ChangeAction.Remove => "-",
        _ => "~",
    };

    private static string Count(int n, string noun) => $"{n.ToString(CultureInfo.InvariantCulture)} {noun}{(n == 1 ? string.Empty : "s")}";

    // A code span that survives any backticks in the text, on one line, and safe in a table cell.
    private static string Code(string text)
    {
        string single = OneLine(text).Replace("|", "\\|", StringComparison.Ordinal);
        string fence = single.Contains('`', StringComparison.Ordinal) ? "``" : "`";
        string pad = single.StartsWith('`') || single.EndsWith('`') ? " " : string.Empty;
        return fence + pad + single + pad + fence;
    }

    // Prose from the plan (values, layer names, messages): Markdown and HTML characters escaped, on one line.
    private static string Text(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (char c in OneLine(text))
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|' or '~')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
