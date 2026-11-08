using Setpoint.Core.Planning;
using Spectre.Console;

namespace Setpoint.Cli.Rendering;

/// <summary>
/// A first, plain rendering of a plan: one line per change and a summary line. The tree view, the target table
/// and the summary panel replace it in the plan renderer ticket.
/// </summary>
internal static class PlanSummaryRenderer
{
    public static void Write(CliConsole console, PlanDocument plan, bool verbose)
    {
        TextWriter writer = console.OutWriter;
        foreach (PlanRepo repo in plan.Repos)
        {
            string name = $"{repo.Target}/{repo.Path}";
            if (verbose && repo.Status != PlanRepoStatus.Changes)
            {
                writer.WriteLine($"{name}: {Status(repo.Status)}");
            }

            foreach (PlanChange change in repo.Changes)
            {
                string locked = change.Locked ? " (locked)" : string.Empty;
                writer.WriteLine($"{name}: {Action(change.Action)} {change.Rule}: {change.From} -> {change.To} [{change.Layer}]{locked}");
            }

            if (verbose)
            {
                foreach (PlanUnmanaged setting in repo.Unmanaged)
                {
                    writer.WriteLine($"{name}: unmanaged {setting.Rule} on {setting.Source}: {setting.Value}");
                }
            }
        }

        PlanSummary s = plan.Summary;
        string summary = $"Plan: {s.ReposChanged} repo{(s.ReposChanged == 1 ? string.Empty : "s")} to change " +
            $"({s.Create} to add, {s.Update} to change, {s.Remove} to remove), {s.InSync} in sync, {s.Skipped} skipped, " +
            $"{s.Errors} failed, {s.Unmanaged} unmanaged settings.";
        writer.WriteLine(summary);
        if (!verbose && s.Unmanaged > 0)
        {
            writer.WriteLine("Run with --verbose to list the unmanaged settings and in-sync repos.");
        }
    }

    private static string Action(ChangeAction action) => action switch
    {
        ChangeAction.Create => "add",
        ChangeAction.Remove => "remove",
        _ => "change",
    };

    private static string Status(PlanRepoStatus status) => status switch
    {
        PlanRepoStatus.InSync => "in sync",
        PlanRepoStatus.Skipped => "skipped",
        PlanRepoStatus.Error => "failed",
        _ => "changes",
    };
}
