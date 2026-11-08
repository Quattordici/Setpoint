using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Setpoint.Cli.Rendering;

/// <summary>
/// Writes a plan for people. The tree layout has a target table, a tree per repo with the native change on a dim
/// line under each neutral change and warnings inline, and a summary panel. The plain layout, for logs and
/// <c>--output plain</c>, writes one line per change with no colour and no wrapping.
/// In-sync repos are hidden unless they have a diagnostic or <c>--verbose</c> is set.
/// </summary>
internal static class PlanRenderer
{
    /// <summary>The mark on a locked rule in the tree layout.</summary>
    public const string LockMark = "🔒";

    public static void Write(CliConsole console, PlanDocument plan, OutputFormat format, bool verbose, string? savedTo)
    {
        if (format == OutputFormat.Plain)
        {
            WritePlain(console.OutWriter, plan, verbose, savedTo);
        }
        else
        {
            WriteTree(console.Out, plan, verbose, savedTo);
        }
    }

    /// <summary>True when the repo is shown: it changes, failed, has a diagnostic, or <c>--verbose</c> is set.</summary>
    internal static bool IsShown(PlanRepo repo, bool verbose) =>
        verbose || repo.Status != PlanRepoStatus.InSync || repo.Diagnostics.Count > 0;

    private static void WriteTree(IAnsiConsole console, PlanDocument plan, bool verbose, string? savedTo)
    {
        console.MarkupLine(
            $"[bold]Setpoint {Markup.Escape(plan.SetpointVersion)}[/] [grey]· plan · policy[/] {Markup.Escape(PolicyLabel(plan.Policy))} " +
            $"[grey]({plan.Policy.Files} files) · {plan.Repos.Count} repos in scope[/]");
        console.WriteLine();

        if (plan.Targets.Count > 0)
        {
            var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey)
                .AddColumn("Target").AddColumn("Detected").AddColumn("API").AddColumn("Auth").AddColumn("Identity")
                .AddColumn(new TableColumn("Repos").RightAligned());
            foreach (PlanTarget target in plan.Targets)
            {
                table.AddRow(
                    $"[bold]{Markup.Escape(target.Name)}[/]",
                    Markup.Escape(target.Detected),
                    Markup.Escape(target.ApiVersion),
                    Markup.Escape(Auth(target)),
                    Markup.Escape(target.Identity ?? "-"),
                    target.RepoCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            console.Write(table);
        }

        foreach (PlanRepo repo in plan.Repos.Where(r => IsShown(r, verbose)))
        {
            console.Write(RepoTree(repo, verbose));
            console.WriteLine();
        }

        console.Write(SummaryPanel(plan, savedTo));
    }

    private static Tree RepoTree(PlanRepo repo, bool verbose)
    {
        string status = repo.Status switch
        {
            PlanRepoStatus.Changes => $"[yellow]{Count(repo.Changes.Count, "change")}[/]",
            PlanRepoStatus.InSync => "[green]in sync[/]",
            PlanRepoStatus.Skipped => "[grey]skipped[/]",
            _ => "[red]failed[/]",
        };
        var tree = new Tree(
            $"[bold]{Markup.Escape(Name(repo))}[/]  [grey]{Markup.Escape(string.Join(" → ", repo.Layers))}[/]  {status}  " +
            $"[grey]{repo.UnmanagedCount} unmanaged[/]").Guide(TreeGuide.Line);

        Dictionary<string, PlanObject> objects = repo.Objects.ToDictionary(o => o.Key, StringComparer.Ordinal);
        foreach (PlanChange change in repo.Changes)
        {
            string colour = Colour(change.Action);
            string locked = change.Locked ? $" [grey]{LockMark}[/]" : string.Empty;
            TreeNode node = tree.AddNode(
                $"[{colour}]{Symbol(change.Action)} {Markup.Escape(change.Rule)}[/]  [grey]{Markup.Escape(change.From)}[/] → " +
                $"[bold]{Markup.Escape(change.To)}[/]  [deepskyblue1][[{Markup.Escape(change.Layer)}]][/]{locked}");
            node.AddNode($"[grey]{Markup.Escape(Native(change, objects))}[/]");
        }

        foreach (PlanDiagnostic diagnostic in repo.Diagnostics)
        {
            string colour = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => "red",
                DiagnosticSeverity.Warning => "darkorange",
                _ => "blue",
            };
            string mark = diagnostic.Severity == DiagnosticSeverity.Info ? "ℹ" : "⚠";
            tree.AddNode($"[{colour}]{mark} {diagnostic.Code}[/] {Markup.Escape(Message(repo, diagnostic))}");
        }

        if (verbose)
        {
            foreach (PlanUnmanaged setting in repo.Unmanaged)
            {
                tree.AddNode($"[grey]· unmanaged {Markup.Escape(setting.Rule)} on {Markup.Escape(setting.Source)}: {Markup.Escape(setting.Value)}[/]");
            }
        }

        return tree;
    }

    private static Panel SummaryPanel(PlanDocument plan, string? savedTo)
    {
        PlanSummary s = plan.Summary;
        string[] warningCodes = [.. AllDiagnostics(plan).Where(d => d.Severity == DiagnosticSeverity.Warning).Select(d => d.Code).Distinct()];
        var grid = new Grid().AddColumn().AddColumn();
        string failed = s.Errors > 0 ? $", [red]{s.Errors} failed[/]" : string.Empty;
        grid.AddRow(
            new Markup("[bold]Repos[/]"),
            new Markup($"[yellow]{s.ReposChanged} to change[/], [green]{s.InSync} in sync[/], [grey]{s.Skipped} skipped[/]{failed}  {MaxChanges(plan, markup: true)}"));
        grid.AddRow(
            new Markup("[bold]Changes[/]"),
            new Markup($"[green]{s.Create} create[/], [yellow]{s.Update} update[/], [red]{s.Remove} remove[/]  [grey]→ {Count(plan.Repos.Sum(r => r.Objects.Count), "native write")}[/]"));
        grid.AddRow(new Markup("[bold]Unmanaged[/]"), new Markup($"{Count(s.Unmanaged, "setting")} left alone [grey](--verbose lists them)[/]"));
        grid.AddRow(new Markup("[bold]Warnings[/]"), new Markup(warningCodes.Length == 0
            ? "[green]none[/]"
            : $"[darkorange]{s.Warnings}[/] [grey]({string.Join(", ", warningCodes)}{(plan.Options.Strict ? string.Empty : "; --strict would fail")})[/]"));
        if (savedTo is not null)
        {
            grid.AddRow(new Markup("[bold]Saved[/]"), new Markup($"{Markup.Escape(savedTo)}  [grey]→ setpoint apply {Markup.Escape(Path.GetFileName(savedTo))}[/]"));
        }

        return new Panel(grid).Header("Plan").BorderColor(Color.Grey).Expand();
    }

    private static void WritePlain(TextWriter writer, PlanDocument plan, bool verbose, string? savedTo)
    {
        writer.WriteLine($"setpoint {plan.SetpointVersion} plan policy={PolicyLabel(plan.Policy)} files={plan.Policy.Files} repos={plan.Repos.Count}");
        foreach (PlanTarget target in plan.Targets)
        {
            writer.WriteLine($"target {target.Name} detected=\"{target.Detected}\" api={target.ApiVersion} auth={Auth(target)} identity={target.Identity ?? "-"} repos={target.RepoCount}");
        }

        foreach (PlanRepo repo in plan.Repos.Where(r => IsShown(r, verbose)))
        {
            string name = Name(repo);
            Dictionary<string, PlanObject> objects = repo.Objects.ToDictionary(o => o.Key, StringComparer.Ordinal);
            foreach (PlanChange change in repo.Changes)
            {
                string locked = change.Locked ? " (locked)" : string.Empty;
                writer.WriteLine($"{Symbol(change.Action)} {name} {change.Rule}: {change.From} -> {change.To} [{change.Layer}]{locked} ({Native(change, objects)})");
            }

            foreach (PlanDiagnostic diagnostic in repo.Diagnostics)
            {
                writer.WriteLine($"{Severity(diagnostic.Severity)} {diagnostic.Code} {name}: {Message(repo, diagnostic)}");
            }

            switch (repo.Status)
            {
                case PlanRepoStatus.Skipped:
                    writer.WriteLine($"skip {name}");
                    break;
                case PlanRepoStatus.InSync:
                    writer.WriteLine($"insync {name}");
                    break;
                case PlanRepoStatus.Error:
                    writer.WriteLine($"failed {name}");
                    break;
            }

            if (verbose)
            {
                foreach (PlanUnmanaged setting in repo.Unmanaged)
                {
                    writer.WriteLine($"unmanaged {name} {setting.Rule} on {setting.Source}: {setting.Value}");
                }
            }
        }

        PlanSummary s = plan.Summary;
        writer.WriteLine(
            $"summary repos_changed={s.ReposChanged} create={s.Create} update={s.Update} remove={s.Remove} in_sync={s.InSync} " +
            $"skipped={s.Skipped} failed={s.Errors} unmanaged={s.Unmanaged} warnings={s.Warnings} {MaxChanges(plan, markup: false)}");
        if (savedTo is not null)
        {
            writer.WriteLine($"plan saved to {savedTo}");
        }
    }

    // "max-changes 3/25", flagged when apply would refuse; "max-changes off" when the limit is 0.
    private static string MaxChanges(PlanDocument plan, bool markup)
    {
        int limit = plan.Options.MaxChanges;
        int used = plan.Summary.ReposChanged;
        if (!markup)
        {
            return limit == 0 ? "max_changes=off" : $"max_changes={used}/{limit}";
        }

        if (limit == 0)
        {
            return "[grey](max-changes off)[/]";
        }

        return used > limit
            ? $"[red](max-changes {used}/{limit}: apply would refuse; raise --max-changes)[/]"
            : $"[grey](max-changes {used}/{limit})[/]";
    }

    private static string Native(PlanChange change, IReadOnlyDictionary<string, PlanObject> objects)
    {
        if (!objects.TryGetValue(change.Object, out PlanObject? planned))
        {
            return change.Object;
        }

        string verb = planned.Operation switch
        {
            ObjectOperation.Create => "create",
            ObjectOperation.Delete => "delete",
            _ => "update",
        };
        return planned.Title is null ? $"{verb} {planned.Key}" : $"{verb} {planned.Title} ({planned.Key})";
    }

    // Repo diagnostics carry the repo name so they read on their own in a log; inside the repo's tree it is noise.
    private static string Message(PlanRepo repo, PlanDiagnostic diagnostic)
    {
        string prefix = Name(repo) + ": ";
        return diagnostic.Message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? diagnostic.Message[prefix.Length..] : diagnostic.Message;
    }

    private static IEnumerable<PlanDiagnostic> AllDiagnostics(PlanDocument plan) =>
        plan.Diagnostics.Concat(plan.Repos.SelectMany(r => r.Diagnostics));

    private static string PolicyLabel(PlanPolicy policy) =>
        policy.Commit is { Length: >= 7 } commit ? commit[..7] : policy.Hash[Fingerprint.Prefix.Length..][..12];

    private static string Auth(PlanTarget target) => target.Auth switch
    {
        Core.Policy.Model.AuthMethod.AccessToken => "accessToken",
        _ => target.Auth.ToString().ToLowerInvariant(),
    };

    private static string Name(PlanRepo repo) => $"{repo.Target}/{repo.Path}";

    private static string Symbol(ChangeAction action) => action switch
    {
        ChangeAction.Create => "+",
        ChangeAction.Remove => "-",
        _ => "~",
    };

    private static string Colour(ChangeAction action) => action switch
    {
        ChangeAction.Create => "green",
        ChangeAction.Remove => "red",
        _ => "yellow",
    };

    private static string Severity(DiagnosticSeverity severity) => severity.ToString().ToLowerInvariant();

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? string.Empty : "s")}";
}
