using System.Text;
using System.Text.RegularExpressions;

namespace Setpoint.Core.Policy.Selection;

/// <summary>
/// A glob over slash-separated repo paths: <c>&lt;target&gt;/&lt;project-or-owner&gt;/&lt;repo&gt;</c> for selectors,
/// and <c>&lt;project-or-owner&gt;/&lt;repo&gt;</c> for a target's scope and exclude. <c>*</c> matches any run of
/// characters inside one part and <c>?</c> one character; nothing else is special. Matching ignores case.
/// </summary>
public sealed partial class RepoGlob
{
    private readonly Regex[] _parts;

    private RepoGlob(string pattern, string[] parts)
    {
        Pattern = pattern;
        Parts = parts;
        _parts = [.. parts.Select(ToRegex)];
        IsExact = parts.All(p => p.IndexOfAny(['*', '?']) < 0);
    }

    /// <summary>The glob as written.</summary>
    public string Pattern { get; }

    /// <summary>The slash-separated parts.</summary>
    public IReadOnlyList<string> Parts { get; }

    /// <summary>True when the glob has no wildcards, so it names exactly one repo.</summary>
    public bool IsExact { get; }

    /// <summary>
    /// Parses <paramref name="pattern"/>, which must have exactly <paramref name="partCount"/> parts.
    /// Returns null and an explanation when the syntax is wrong.
    /// </summary>
    public static RepoGlob? TryParse(string pattern, int partCount, out string? error)
    {
        string[] parts = pattern.Split('/');
        if (parts.Length != partCount)
        {
            error = partCount == 3
                ? $"'{pattern}' must have three parts: <target>/<project-or-owner>/<repo>."
                : $"'{pattern}' must have two parts: <project-or-owner>/<repo>.";
            return null;
        }

        if (parts.Any(p => p.Length == 0))
        {
            error = $"'{pattern}' has an empty part.";
            return null;
        }

        if (UnsupportedCharacters().Match(pattern) is { Success: true } match)
        {
            error = $"'{pattern}' uses '{match.Value}', which globs here don't support. Only * and ? are wildcards.";
            return null;
        }

        error = null;
        return new RepoGlob(pattern, parts);
    }

    /// <summary>True when every part of <paramref name="path"/> matches.</summary>
    public bool Matches(params ReadOnlySpan<string> path)
    {
        if (path.Length != _parts.Length)
        {
            return false;
        }

        for (int i = 0; i < path.Length; i++)
        {
            if (!_parts[i].IsMatch(path[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when this three-part selector matches <paramref name="repo"/>.</summary>
    public bool Matches(RepoRef repo) => Matches(repo.Target, repo.Owner, repo.Name);

    /// <inheritdoc />
    public override string ToString() => Pattern;

    private static Regex ToRegex(string part)
    {
        var builder = new StringBuilder("^");
        foreach (char c in part)
        {
            builder.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }

        return new Regex(builder.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"\*\*|[\[\]{}!\\]")]
    private static partial Regex UnsupportedCharacters();
}
