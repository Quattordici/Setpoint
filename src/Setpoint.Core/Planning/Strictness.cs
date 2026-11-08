using System.Text.Json.Nodes;

namespace Setpoint.Core.Planning;

/// <summary>
/// Tells whether an unmanaged live setting is stricter than what the policy declares for the same neutral rule,
/// so the effective result differs from the declared one (SP2101). Provider-block rules are not compared.
/// </summary>
public static class Strictness
{
    /// <summary>
    /// True when <paramref name="unmanaged"/> asks more of a pull request than <paramref name="declared"/>.
    /// A null <paramref name="declared"/> means the policy says the setting must be absent.
    /// </summary>
    public static bool IsStricter(string rule, JsonNode? unmanaged, JsonNode? declared)
    {
        if (unmanaged is null)
        {
            return false;
        }

        return rule switch
        {
            "minimumReviewers" => Count(unmanaged) > Count(declared) || (Flag(unmanaged, "resetOnPush") && !Flag(declared, "resetOnPush")),
            "requiredChecks" => Names(unmanaged).Except(Names(declared), StringComparer.OrdinalIgnoreCase).Any(),
            "requireCommentResolution" or "protectFromForcePushAndDelete" => IsTrue(unmanaged) && !IsTrue(declared),
            "allowedMergeTypes" => declared is not null && Names(declared).Except(Names(unmanaged), StringComparer.Ordinal).Any(),
            _ => false,
        };
    }

    private static int Count(JsonNode? node) => node?["count"] is JsonValue v && v.TryGetValue(out int count) ? count : 0;

    private static bool Flag(JsonNode? node, string name) => node?[name] is JsonValue v && v.TryGetValue(out bool flag) && flag;

    private static bool IsTrue(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool flag) && flag;

    private static IEnumerable<string> Names(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(e => e is JsonObject o ? o["name"]?.GetValue<string>() : e?.GetValue<string>()).OfType<string>()
            : [];
}
