using System.Text.Json;

namespace Setpoint.Core.Policy.Model;

/// <summary>
/// A <see cref="JsonException"/> from a nested deserialization inside a converter. System.Text.Json sets
/// <see cref="JsonException.Path"/> to the converter's own position, so the path inside the nested value
/// is carried in <see cref="InnerPath"/>, and the full path is <c>Path + InnerPath</c>.
/// </summary>
public sealed class NestedJsonException : JsonException
{
    private NestedJsonException(string message, string innerPath, Exception innerException)
        : base(message, path: null, lineNumber: null, bytePositionInLine: null, innerException)
    {
        InnerPath = innerPath;
    }

    /// <summary>The path inside the nested value, such as <c>.count</c> or <c>[0]</c>; empty for the value itself.</summary>
    public string InnerPath { get; }

    /// <summary>The message without the serializer's position suffix.</summary>
    public string OriginalMessage => ((JsonException)InnerException!).Message is var m && m.IndexOf(" Path:", StringComparison.Ordinal) is var i and >= 0 ? m[..i] : InnerException!.Message;

    /// <summary>The full path: <see cref="JsonException.Path"/> followed by <see cref="InnerPath"/>.</summary>
    public string FullPath => (Path ?? "$") + InnerPath;

    /// <summary>Wraps an exception from a nested deserialization.</summary>
    public static NestedJsonException Wrap(JsonException inner)
    {
        string innerPath = inner is NestedJsonException nested
            ? nested.FullPath.TrimStart('$')
            : (inner.Path ?? "$").TrimStart('$');
        JsonException root = inner is NestedJsonException n ? (JsonException)n.InnerException! : inner;
        return new NestedJsonException(root.Message, innerPath, root);
    }
}
