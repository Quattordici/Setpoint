using System.Buffers;

namespace Setpoint.Core.Policy.Loading;

/// <summary>
/// Builds JSON paths in the same format System.Text.Json reports in <see cref="System.Text.Json.JsonException.Path"/>,
/// so an error from the serializer can be traced back to a line.
/// </summary>
internal static class JsonPath
{
    public const string Root = "$";

    private static readonly SearchValues<char> s_specialCharacters = SearchValues.Create(".'\"[]() \t\r\n\f\b\\\u0085\u2028\u2029");

    public static string Property(string parent, string name) =>
        name.AsSpan().ContainsAny(s_specialCharacters)
            ? $"{parent}['{name.Replace("'", "\\'", StringComparison.Ordinal)}']"
            : $"{parent}.{name}";

    public static string Index(string parent, int index) => $"{parent}[{index}]";

    /// <summary>The parent path, or null for the root.</summary>
    public static string? Parent(string path)
    {
        if (path == Root)
        {
            return null;
        }

        if (path.EndsWith(']'))
        {
            int open = path.LastIndexOf(path.EndsWith("']", StringComparison.Ordinal) ? "['" : "[", StringComparison.Ordinal);
            return open > 0 ? path[..open] : Root;
        }

        int dot = path.LastIndexOf('.');
        return dot > 0 ? path[..dot] : Root;
    }

    /// <summary>A path as a person reads it: <c>rules.minimumReviewers.count</c>.</summary>
    public static string ForDisplay(string path) =>
        path == Root ? "the top level" : path.TrimStart('$').TrimStart('.');
}
