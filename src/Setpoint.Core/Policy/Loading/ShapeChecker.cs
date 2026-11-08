using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Core.Policy.Loading;

/// <summary>
/// Walks a policy file's JSON tree against the serialization contract and reports every unknown key,
/// with its line and a suggestion. The serializer alone would stop at the first one.
/// </summary>
internal sealed class ShapeChecker(SourceMap source, List<Diagnostic> diagnostics)
{
    public void Check(JsonNode? node, JsonTypeInfo typeInfo, string path)
    {
        if (node is null)
        {
            return;
        }

        Type type = typeInfo.Type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Declared<>))
        {
            Check(node, typeInfo.Options.GetTypeInfo(type.GetGenericArguments()[0]), path);
            return;
        }

        if (type == typeof(BooleanRule))
        {
            if (node is JsonObject booleanObject)
            {
                CheckKeys(booleanObject, BooleanRule.ObjectFormKeys, path);
            }

            return;
        }

        switch (typeInfo.Kind)
        {
            case JsonTypeInfoKind.Object when node is JsonObject obj:
                foreach ((string key, JsonNode? value) in obj)
                {
                    JsonPropertyInfo? property = FindProperty(typeInfo, key);
                    if (property is null)
                    {
                        ReportUnknownKey(key, typeInfo.Properties.Select(p => p.Name), path);
                    }
                    else
                    {
                        Check(value, typeInfo.Options.GetTypeInfo(property.PropertyType), JsonPath.Property(path, key));
                    }
                }

                break;

            case JsonTypeInfoKind.Enumerable when node is JsonArray array && typeInfo.ElementType is { } elementType:
                for (int i = 0; i < array.Count; i++)
                {
                    Check(array[i], typeInfo.Options.GetTypeInfo(elementType), JsonPath.Index(path, i));
                }

                break;

            case JsonTypeInfoKind.Dictionary when node is JsonObject dictionary && typeInfo.ElementType is { } valueType:
                foreach ((string key, JsonNode? value) in dictionary)
                {
                    Check(value, typeInfo.Options.GetTypeInfo(valueType), JsonPath.Property(path, key));
                }

                break;
        }
    }

    public void ReportUnknownKey(string key, IEnumerable<string> validKeys, string parentPath)
    {
        string where = parentPath == JsonPath.Root ? string.Empty : $" in '{JsonPath.ForDisplay(parentPath)}'";
        string? suggestion = Suggest(key, validKeys);
        string message = suggestion is null
            ? $"Unknown key '{key}'{where}."
            : $"Unknown key '{key}'{where}. Did you mean '{suggestion}'?";
        diagnostics.Add(Diagnostic.Error(DiagnosticCodes.UnknownKey, message, source.Find(JsonPath.Property(parentPath, key))));
    }

    private void CheckKeys(JsonObject obj, IReadOnlyList<string> validKeys, string path)
    {
        foreach ((string key, _) in obj)
        {
            if (!validKeys.Contains(key))
            {
                ReportUnknownKey(key, validKeys, path);
            }
        }
    }

    private static JsonPropertyInfo? FindProperty(JsonTypeInfo typeInfo, string key)
    {
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.Name == key && property.Set is not null)
            {
                return property;
            }
        }

        return null;
    }

    private static string? Suggest(string key, IEnumerable<string> validKeys) =>
        validKeys
            .Select(candidate => (candidate, distance: EditDistance(key.ToLowerInvariant(), candidate.ToLowerInvariant())))
            .Where(x => x.distance <= Math.Max(2, x.candidate.Length / 4))
            .OrderBy(x => x.distance)
            .Select(x => x.candidate)
            .FirstOrDefault();

    private static int EditDistance(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
