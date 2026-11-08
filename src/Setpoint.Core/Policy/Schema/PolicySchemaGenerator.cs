using System.ComponentModel;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Policy.Schema;

/// <summary>
/// Generates the JSON Schemas for policy files and plan files from the same serialization contract the loader uses,
/// so the schema can't drift from what Setpoint accepts. Editors use them through
/// <c># yaml-language-server: $schema=...</c> at the top of a policy file.
/// </summary>
public static class PolicySchemaGenerator
{
    /// <summary>Where the published schemas live. The files are committed under <c>schema/v1/</c> in the repo.</summary>
    public const string BaseUri = "https://raw.githubusercontent.com/Quattordici/Setpoint/main/schema/v1/";

    public const string TargetsSchemaFileName = "targets.json";
    public const string LayerSchemaFileName = "layer.json";
    public const string PlanSchemaFileName = "plan.json";

    private const string Draft = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The schema for <c>targets.yml</c>.</summary>
    public static JsonObject TargetsSchema()
    {
        JsonObject schema = Generate(PolicyJsonContext.Default.TargetsFile);
        return Finish(schema, TargetsSchemaFileName, "Setpoint targets", "The named platform endpoints a Setpoint policy applies to.");
    }

    /// <summary>The schema for a layer file (baseline, group or repo override), with each adapter's provider block.</summary>
    public static JsonObject LayerSchema(IEnumerable<IProviderBlockDefinition> providers)
    {
        JsonObject schema = Generate(PolicyJsonContext.Default.PolicyLayer);
        var properties = (JsonObject)schema["properties"]!;
        foreach (IProviderBlockDefinition provider in providers.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            JsonObject block = Generate(provider.TypeInfo);
            block["description"] ??= $"Options that exist only on {provider.Key}.";
            properties[provider.Key] = block;
        }

        return Finish(schema, LayerSchemaFileName, "Setpoint policy layer", "A Setpoint layer file: baseline.yml, groups/<name>.yml or repos/<target>/<project-or-owner>/<repo>.yml.");
    }

    /// <summary>The schema for plan files, which <c>plan --out</c> writes and <c>plan --json</c> prints.</summary>
    public static JsonObject PlanSchema()
    {
        JsonObject schema = Generate(Planning.PlanJsonContext.Default.PlanDocument);
        return Finish(schema, PlanSchemaFileName, "Setpoint plan", "A Setpoint plan: the native objects apply will write, their plan-time fingerprints, and the neutral changes for people.");
    }

    /// <summary>Writes a schema the way the committed files are written: indented, with readable characters.</summary>
    public static string Serialize(JsonObject schema) =>
        schema.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).ReplaceLineEndings("\n") + "\n";

    private static JsonObject Generate(JsonTypeInfo typeInfo)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };

        return (JsonObject)typeInfo.GetJsonSchemaAsNode(exporterOptions);
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        Type type = context.TypeInfo.Type;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Declared<>))
        {
            // A rule in a layer: its value, or ~ for unmanaged.
            JsonObject inner = Generate(context.TypeInfo.Options.GetTypeInfo(type.GetGenericArguments()[0]));
            schema = AllowNull(inner);
        }
        else if (type == typeof(BooleanRule))
        {
            schema = new JsonObject
            {
                ["oneOf"] = new JsonArray
                {
                    new JsonObject { ["type"] = "boolean" },
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["enabled"] = new JsonObject { ["type"] = "boolean" } },
                        ["required"] = new JsonArray("enabled"),
                        ["additionalProperties"] = false,
                    },
                },
            };
        }

        if (schema is JsonObject obj)
        {
            if (context.PropertyInfo?.Name == "version" && context.Path.Length <= 2)
            {
                obj.Remove("type");
                obj["const"] = PolicyFileParser.SupportedVersion;
            }

            if (context.PropertyInfo?.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
                    .OfType<DescriptionAttribute>().FirstOrDefault() is { } description)
            {
                obj["description"] = description.Description;
            }
            else if (context.PropertyInfo is null && type.GetCustomAttribute<DescriptionAttribute>() is { } typeDescription)
            {
                obj["description"] = typeDescription.Description;
            }
        }

        return schema;
    }

    private static JsonObject AllowNull(JsonObject schema)
    {
        switch (schema["type"])
        {
            case JsonValue single when single.GetValue<string>() != "null":
                schema["type"] = new JsonArray(single.GetValue<string>(), "null");
                if (schema["enum"] is JsonArray values && !values.Any(v => v is null))
                {
                    values.Add(null);
                }

                return schema;
            case JsonArray types:
                if (!types.Any(t => t?.GetValue<string>() == "null"))
                {
                    types.Add("null");
                }

                return schema;
            default:
                return new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
        }
    }

    private static JsonObject Finish(JsonObject schema, string fileName, string title, string description)
    {
        var result = new JsonObject
        {
            ["$schema"] = Draft,
            ["$id"] = BaseUri + fileName,
            ["title"] = title,
            ["description"] = description,
        };

        foreach ((string key, JsonNode? value) in schema.ToList())
        {
            schema.Remove(key);
            result[key] = value;
        }

        return result;
    }
}
