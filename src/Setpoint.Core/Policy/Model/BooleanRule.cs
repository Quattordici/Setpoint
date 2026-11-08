using System.Text.Json;
using System.Text.Json.Serialization;

namespace Setpoint.Core.Policy.Model;

/// <summary>
/// A rule that is on or off, such as <c>requireCommentResolution</c>. <c>false</c> means the setting must be absent.
/// It can also be written as <c>{ enabled: false }</c>, which means the same and which <c>validate</c> flags as redundant.
/// </summary>
/// <param name="Enabled">Whether the setting must be present.</param>
[JsonConverter(typeof(BooleanRuleJsonConverter))]
public sealed record BooleanRule(bool Enabled) : IToggleableRule
{
    /// <summary>The YAML keys the object form accepts.</summary>
    public static readonly IReadOnlyList<string> ObjectFormKeys = ["enabled"];

    /// <summary>True when the file used <c>{ enabled: ... }</c> instead of a plain boolean.</summary>
    public bool WrittenAsObject { get; init; }
}

/// <summary>Reads <see cref="BooleanRule"/> from <c>true</c>, <c>false</c> or <c>{ enabled: bool }</c>.</summary>
public sealed class BooleanRuleJsonConverter : JsonConverter<BooleanRule>
{
    /// <inheritdoc />
    public override BooleanRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return new BooleanRule(true);
            case JsonTokenType.False:
                return new BooleanRule(false);
            case JsonTokenType.StartObject:
                bool? enabled = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string? name = reader.GetString();
                    reader.Read();
                    if (name != "enabled")
                    {
                        throw new JsonException($"Unknown key '{name}'. The only key here is 'enabled'.");
                    }

                    enabled = reader.TokenType switch
                    {
                        JsonTokenType.True => true,
                        JsonTokenType.False => false,
                        _ => throw new JsonException("'enabled' must be true or false."),
                    };
                }

                return new BooleanRule(enabled ?? throw new JsonException("'enabled' is required."))
                {
                    WrittenAsObject = true,
                };
            default:
                throw new JsonException("Expected true, false or { enabled: true|false }.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BooleanRule value, JsonSerializerOptions options)
    {
        if (value.WrittenAsObject)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("enabled", value.Enabled);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteBooleanValue(value.Enabled);
        }
    }
}
