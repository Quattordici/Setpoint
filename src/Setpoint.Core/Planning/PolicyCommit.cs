namespace Setpoint.Core.Planning;

/// <summary>
/// Finds the commit a policy repo checkout is at, by reading <c>.git</c> directly, so plan needs no git on the
/// path. Returns null when the directory isn't in a git checkout or the commit can't be told.
/// </summary>
public static class PolicyCommit
{
    /// <summary>The commit of the checkout containing <paramref name="directory"/>, or null.</summary>
    public static string? Find(string directory)
    {
        string? gitDirectory = FindGitDirectory(directory);
        if (gitDirectory is null)
        {
            return null;
        }

        string? head = ReadFirstLine(Path.Combine(gitDirectory, "HEAD"));
        if (head is null)
        {
            return null;
        }

        if (!head.StartsWith("ref: ", StringComparison.Ordinal))
        {
            return IsSha(head) ? head : null;
        }

        string reference = head["ref: ".Length..].Trim();
        string? commonDirectory = ReadFirstLine(Path.Combine(gitDirectory, "commondir")) is { } common
            ? Path.GetFullPath(Path.Combine(gitDirectory, common))
            : gitDirectory;

        foreach (string root in new[] { gitDirectory, commonDirectory }.Distinct())
        {
            if (ReadFirstLine(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar))) is { } loose && IsSha(loose))
            {
                return loose;
            }
        }

        return FindPacked(Path.Combine(commonDirectory, "packed-refs"), reference);
    }

    private static string? FindGitDirectory(string directory)
    {
        for (DirectoryInfo? current = new(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            string candidate = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            // A worktree or submodule: .git is a file that says "gitdir: <path>".
            if (File.Exists(candidate) && ReadFirstLine(candidate) is { } line && line.StartsWith("gitdir: ", StringComparison.Ordinal))
            {
                return Path.GetFullPath(Path.Combine(current.FullName, line["gitdir: ".Length..].Trim()));
            }
        }

        return null;
    }

    private static string? FindPacked(string packedRefs, string reference)
    {
        if (!File.Exists(packedRefs))
        {
            return null;
        }

        foreach (string line in File.ReadLines(packedRefs))
        {
            string[] parts = line.Split(' ', 2);
            if (parts.Length == 2 && parts[1].Trim() == reference && IsSha(parts[0]))
            {
                return parts[0];
            }
        }

        return null;
    }

    private static string? ReadFirstLine(string path) =>
        File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;

    private static bool IsSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}
