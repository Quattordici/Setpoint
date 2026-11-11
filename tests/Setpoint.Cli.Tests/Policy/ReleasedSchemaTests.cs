using System.Text.Json.Nodes;
using Setpoint.Core.Policy.Schema;

namespace Setpoint.Cli.Tests.Policy;

/// <summary>
/// Freezes schema v1 at 1.0. The schema files as released are kept under <c>Policy/Released/v1/</c>, and the
/// committed ones under <c>schema/v1/</c> may only add to them: a new optional key, a new enum value, a new
/// description. Removing or renaming a key, making one required, narrowing a type or dropping an enum value would
/// break policy files and plan readers written against 1.0, so it needs schema v2 instead.
/// </summary>
public sealed class ReleasedSchemaTests
{
    public static TheoryData<string> SchemaFiles =>
        [PolicySchemaGenerator.TargetsSchemaFileName, PolicySchemaGenerator.LayerSchemaFileName, PolicySchemaGenerator.PlanSchemaFileName];

    /// <summary>
    /// Given a schema file as released in 1.0,
    /// when the committed one is compared with it,
    /// then the committed one only adds to it.
    /// </summary>
    [Theory(DisplayName = "👍 Schema v1 only grows by optional keys after its release")]
    [MemberData(nameof(SchemaFiles))]
    public void Schema_Committed_IsCompatibleWithReleased(string fileName)
    {
        // Arrange
        string root = RepoRoot();
        JsonNode released = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests", "Setpoint.Cli.Tests", "Policy", "Released", "v1", fileName)))!;
        JsonNode committed = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "schema", "v1", fileName)))!;

        // Act
        IReadOnlyList<string> breaks = SchemaCompatibility.Breaks(released, committed);

        // Assert
        breaks.ShouldBeEmpty($"schema/v1/{fileName} changed in a way that breaks 1.0 users. Add optional keys only, or start schema v2.");
    }

    /// <summary>
    /// Given a schema,
    /// when an optional property, an enum value or a description is added,
    /// then nothing breaks.
    /// </summary>
    [Fact(DisplayName = "👍 Adding optional keys, enum values and descriptions is compatible")]
    public void Breaks_Additions_AreCompatible()
    {
        // Arrange
        JsonNode released = JsonNode.Parse("""{"type":"object","properties":{"a":{"enum":["x"]}},"required":["a"],"additionalProperties":false}""")!;
        JsonNode committed = JsonNode.Parse("""{"type":"object","properties":{"a":{"enum":["x","y"],"description":"A."},"b":{"type":"string"}},"required":["a"],"additionalProperties":false}""")!;

        // Act
        IReadOnlyList<string> breaks = SchemaCompatibility.Breaks(released, committed);

        // Assert
        breaks.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a schema,
    /// when a property is removed, a new one is required, an enum value is dropped, a type changes or a limit is added,
    /// then each is a break, named by its path.
    /// </summary>
    [Theory(DisplayName = "👎 Removing, requiring, narrowing or limiting breaks")]
    [InlineData("""{"type":"object","properties":{},"required":[]}""", "$.properties.a: removed")]
    [InlineData("""{"type":"object","properties":{"a":{"enum":["x","y"]},"b":{"type":"string"}},"required":["b"]}""", "$.required: b is newly required")]
    [InlineData("""{"type":"object","properties":{"a":{"enum":["y"]}},"required":[]}""", "$.properties.a.enum: x removed")]
    [InlineData("""{"type":"array","properties":{"a":{"enum":["x","y"]}},"required":[]}""", "$.type: \"object\" became \"array\"")]
    [InlineData("""{"type":"object","properties":{"a":{"enum":["x","y"],"maxLength":3}},"required":[]}""", "$.properties.a.maxLength: added")]
    public void Breaks_Narrowing_IsReported(string committedJson, string expected)
    {
        // Arrange
        JsonNode released = JsonNode.Parse("""{"type":"object","properties":{"a":{"enum":["x","y"]}},"required":[]}""")!;
        JsonNode committed = JsonNode.Parse(committedJson)!;

        // Act
        IReadOnlyList<string> breaks = SchemaCompatibility.Breaks(released, committed);

        // Assert
        breaks.ShouldContain(expected);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Setpoint.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repo root (Setpoint.slnx) above the test output directory.");
    }
}

/// <summary>
/// Compares a JSON Schema with the one released before it. A key may be added only when it can't reject a document
/// the released schema accepted: a property, a description, a definition. Everything the released schema says must
/// still be there, unchanged, except that enums may grow and fewer properties may be required.
/// </summary>
internal static class SchemaCompatibility
{
    // Keys that never reject a document, so adding them is safe.
    private static readonly HashSet<string> s_annotations = new(StringComparer.Ordinal)
    {
        "properties", "description", "title", "default", "examples", "$defs", "markdownDescription", "deprecated", "$comment",
    };

    /// <summary>Every way <paramref name="committed"/> narrows <paramref name="released"/>, by path; empty when it only adds.</summary>
    public static IReadOnlyList<string> Breaks(JsonNode released, JsonNode committed)
    {
        var breaks = new List<string>();
        Compare(released, committed, "$", breaks);
        return breaks;
    }

    private static void Compare(JsonNode? released, JsonNode? committed, string path, List<string> breaks)
    {
        if (released is JsonObject before)
        {
            if (committed is not JsonObject after)
            {
                breaks.Add($"{path}: is no longer an object");
                return;
            }

            CompareObject(before, after, path, breaks);
        }
        else if (released is JsonArray items)
        {
            if (committed is not JsonArray now || now.Count < items.Count)
            {
                breaks.Add($"{path}: lost items");
                return;
            }

            for (int i = 0; i < items.Count; i++)
            {
                Compare(items[i], now[i], $"{path}[{i}]", breaks);
            }
        }
        else if (!JsonNode.DeepEquals(released, committed))
        {
            breaks.Add($"{path}: {released?.ToJsonString() ?? "null"} became {committed?.ToJsonString() ?? "null"}");
        }
    }

    private static void CompareObject(JsonObject before, JsonObject after, string path, List<string> breaks)
    {
        foreach ((string key, JsonNode? value) in before)
        {
            string at = $"{path}.{key}";
            if (key is "description" or "title" or "examples" or "markdownDescription" or "$comment")
            {
                continue;
            }

            if (!after.ContainsKey(key))
            {
                breaks.Add($"{at}: removed");
                continue;
            }

            switch (key)
            {
                case "required":
                    foreach (string added in Strings(after[key]).Except(Strings(value), StringComparer.Ordinal))
                    {
                        breaks.Add($"{at}: {added} is newly required");
                    }

                    break;
                case "enum":
                    foreach (string removed in Texts(value).Except(Texts(after[key]), StringComparer.Ordinal))
                    {
                        breaks.Add($"{at}: {removed} removed");
                    }

                    break;
                case "properties" or "$defs" when value is JsonObject members:
                    foreach ((string name, JsonNode? member) in members)
                    {
                        if (after[key] is JsonObject now && now.ContainsKey(name))
                        {
                            Compare(member, now[name], $"{path}.{key}.{name}", breaks);
                        }
                        else
                        {
                            breaks.Add($"{path}.{key}.{name}: removed");
                        }
                    }

                    break;
                default:
                    Compare(value, after[key], at, breaks);
                    break;
            }
        }

        foreach ((string key, JsonNode? value) in after)
        {
            if (before.ContainsKey(key) || s_annotations.Contains(key))
            {
                continue;
            }

            if (key == "required" && Strings(value).Any())
            {
                breaks.AddRange(Strings(value).Select(r => $"{path}.required: {r} is newly required"));
            }
            else if (key != "required")
            {
                breaks.Add($"{path}.{key}: added");
            }
        }
    }

    private static IEnumerable<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? string.Empty);

    private static IEnumerable<string> Texts(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : n?.ToJsonString() ?? "null");
}
