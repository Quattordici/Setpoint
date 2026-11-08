using System.Text.Json;
using System.Text.Json.Nodes;

namespace Setpoint.Core.Planning;

/// <summary>Puts JSON into one canonical form, so equal values compare and hash equal whatever order they came in.</summary>
public static class CanonicalJson
{
    private static readonly JsonSerializerOptions s_compact = new() { WriteIndented = false };

    /// <summary>
    /// The canonical form of a native object, for fingerprints: object keys sorted ordinally, arrays left in order
    /// (a native array's order can matter), and nulls kept.
    /// </summary>
    public static JsonNode? ForFingerprint(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, ForFingerprint(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(ForFingerprint)]),
        _ => node?.DeepClone(),
    };

    /// <summary>
    /// The canonical form of a rule value, for the diff: object keys sorted, null-valued keys dropped, and arrays
    /// sorted, because every list in the policy model is a set (merge types, checks, path filters).
    /// </summary>
    public static JsonNode? ForRule(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .Where(p => p.Value is not null)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, ForRule(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(ForRule).OrderBy(ToText, StringComparer.Ordinal)]),
        _ => node?.DeepClone(),
    };

    /// <summary>True when two rule values mean the same.</summary>
    public static bool RuleEquals(JsonNode? left, JsonNode? right) => JsonNode.DeepEquals(ForRule(left), ForRule(right));

    /// <summary>Compact JSON text, or <c>null</c>.</summary>
    public static string ToText(JsonNode? node) => node?.ToJsonString(s_compact) ?? "null";
}
