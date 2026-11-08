using Setpoint.Core.Applying;

namespace Setpoint.Cli.Rendering;

/// <summary>
/// Writes an apply result: one line per repo, then a summary line. It is plain text in every layout, so pipeline
/// logs keep it as it is.
/// </summary>
internal static class ApplyRenderer
{
    public static void Write(CliConsole console, ApplyResultSet result)
    {
        TextWriter writer = console.OutWriter;
        if (result.Refusal is { } refusal)
        {
            writer.WriteLine($"apply: {refusal}");
            return;
        }

        foreach (RepoApplyResult repo in result.Repos)
        {
            writer.WriteLine(repo.Status switch
            {
                RepoApplyStatus.Applied => $"applied        {repo.Repo}  {repo.Written} of {repo.Planned} written",
                RepoApplyStatus.Failed => $"failed         {repo.Repo}  {repo.Written} of {repo.Planned} written; the rest not attempted",
                RepoApplyStatus.NotAttempted => $"not attempted  {repo.Repo}  stopped by --fail-fast",
                _ => $"skipped        {repo.Repo}  target unreachable",
            });
        }

        int Count(RepoApplyStatus status) => result.Repos.Count(r => r.Status == status);
        writer.WriteLine(
            $"apply: repos applied={Count(RepoApplyStatus.Applied)} failed={Count(RepoApplyStatus.Failed)} " +
            $"not_attempted={Count(RepoApplyStatus.NotAttempted)} skipped={Count(RepoApplyStatus.Skipped)} " +
            $"objects_written={result.Repos.Sum(r => r.Written)}");
    }
}
