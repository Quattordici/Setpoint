using Setpoint.Core.Diagnostics;

namespace Setpoint.Core.Policy.Loading;

/// <summary>
/// Where each key of a policy file sits, by JSON path (<c>$.rules.minimumReviewers.count</c>).
/// Lets any later check, such as a locked-rule error in layer merge, point at a file and line.
/// </summary>
public sealed class SourceMap
{
    private readonly Dictionary<string, SourceLocation> _keys = new(StringComparer.Ordinal);

    internal SourceMap(string file)
    {
        File = file;
    }

    /// <summary>The file this map belongs to, relative to the policy repo root.</summary>
    public string File { get; }

    /// <summary>
    /// The location of the key at <paramref name="jsonPath"/>, or of its nearest ancestor that has one,
    /// or the start of the file.
    /// </summary>
    public SourceLocation Find(string? jsonPath)
    {
        string? path = jsonPath;
        while (!string.IsNullOrEmpty(path))
        {
            if (_keys.TryGetValue(path, out SourceLocation? location))
            {
                return location;
            }

            path = JsonPath.Parent(path);
        }

        return SourceLocation.StartOf(File);
    }

    internal void Record(string jsonPath, SourceLocation location) => _keys.TryAdd(jsonPath, location);
}
