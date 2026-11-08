using System.Text.Json.Nodes;

namespace Setpoint.Core.Planning;

/// <summary>Writes rule values for people, as plan output shows them: <c>count 2, resetOnPush</c>, <c>squash, rebase</c>.</summary>
public static class ValueText
{
    /// <summary>What a missing live value reads as.</summary>
    public const string None = "(none)";

    /// <summary>What a removed value reads as.</summary>
    public const string Removed = "(removed)";

    /// <summary>What a value the token can't see reads as.</summary>
    public const string Unknown = "(unknown)";

    /// <summary>The value for people, or <paramref name="missing"/> when there is none.</summary>
    public static string Format(JsonNode? value, string missing = None) => value switch
    {
        null => missing,
        JsonArray array when array.Count == 0 => "(empty)",
        JsonArray array => string.Join(", ", array.Select(Element)),
        JsonObject obj when obj.Count == 0 => "true",
        JsonObject obj => string.Join(", ", obj.Where(p => p.Value is not null).Select(Field)),
        JsonValue scalar => Scalar(scalar),
        _ => CanonicalJson.ToText(value),
    };

    // A list of { name: x } reads as its names.
    private static string Element(JsonNode? element) => element switch
    {
        JsonObject { Count: 1 } obj when obj["name"] is JsonValue name => Scalar(name),
        _ => Format(element),
    };

    // A true flag reads as its name; a false one is left to the reader as "not name".
    private static string Field(KeyValuePair<string, JsonNode?> field) => field.Value switch
    {
        JsonValue v when v.TryGetValue(out bool flag) => flag ? field.Key : $"not {field.Key}",
        JsonValue v => $"{field.Key} {Scalar(v)}",
        _ => $"{field.Key} [{Format(field.Value)}]",
    };

    private static string Scalar(JsonValue value) =>
        value.TryGetValue(out string? text) ? text
        : value.TryGetValue(out bool flag) ? (flag ? "true" : "false")
        : CanonicalJson.ToText(value);
}
