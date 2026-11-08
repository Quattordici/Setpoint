using System.Text.Json.Nodes;
using Setpoint.Core.Policy.Schema;
using Setpoint.Core.Tests.TestSupport;

namespace Setpoint.Core.Tests.Policy;

/// <summary>
/// Tests for <see cref="PolicySchemaGenerator"/>: the provider fragments, the unmanaged (~) form,
/// the version constant, strictness and descriptions in the generated schemas.
/// </summary>
public sealed class PolicySchemaGeneratorTests
{
    /// <summary>
    /// Given a provider block definition,
    /// when the layer schema is generated,
    /// then the provider's block appears under its key, generated from its own model.
    /// </summary>
    [Fact(DisplayName = "👍 Layer schema includes each provider's block")]
    public void LayerSchema_ProviderDefinition_IncludesFragment()
    {
        // Arrange
        FakeProviderBlockDefinition provider = FakeProviderBlockDefinition.Instance;

        // Act
        JsonObject schema = PolicySchemaGenerator.LayerSchema([provider]);

        // Assert
        JsonNode? fragment = schema["properties"]?["fake"]?["properties"]?["minimumReviewers"];
        fragment.ShouldNotBeNull();
        fragment["properties"]?["flag"]?["type"]?.GetValue<string>().ShouldBe("boolean");
    }

    /// <summary>
    /// Given the layer model,
    /// when the layer schema is generated,
    /// then every neutral rule accepts null, which is how ~ (unmanaged) reaches the schema.
    /// </summary>
    [Fact(DisplayName = "🖐️ Every neutral rule accepts ~ in the schema")]
    public void LayerSchema_NeutralRules_AcceptNull()
    {
        // Arrange
        JsonObject schema = PolicySchemaGenerator.LayerSchema([]);

        // Act
        var rules = (JsonObject)schema["properties"]!["rules"]!["properties"]!;

        // Assert
        foreach ((string name, JsonNode? rule) in rules)
        {
            rule!.ToJsonString().ShouldContain("\"null\"", customMessage: $"rule {name} should accept ~");
        }
    }

    /// <summary>
    /// Given both schemas,
    /// when they are generated,
    /// then each pins version to 1, requires it, rejects unknown top-level keys and has an $id under the published base URI.
    /// </summary>
    [Fact(DisplayName = "👍 Schemas pin version 1, require it and reject unknown keys")]
    public void Schemas_Root_PinVersionAndAreStrict()
    {
        // Arrange
        JsonObject[] schemas = [PolicySchemaGenerator.TargetsSchema(), PolicySchemaGenerator.LayerSchema([])];

        // Act
        var roots = schemas.Select(s => (
            Const: s["properties"]?["version"]?["const"]?.GetValue<int>(),
            Required: s["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray(),
            Additional: s["additionalProperties"]?.GetValue<bool>(),
            Id: s["$id"]!.GetValue<string>()));

        // Assert
        roots.ShouldAllBe(r => r.Const == 1 && r.Required.Contains("version") && r.Additional == false
            && r.Id.StartsWith(PolicySchemaGenerator.BaseUri));
    }

    /// <summary>
    /// Given a model property with a description,
    /// when the schema is generated,
    /// then the description is in the schema, so editors show it on hover.
    /// </summary>
    [Fact(DisplayName = "🖐️ Property descriptions reach the schema")]
    public void LayerSchema_DescribedProperty_HasDescription()
    {
        // Arrange
        JsonObject schema = PolicySchemaGenerator.LayerSchema([]);

        // Act
        string? description = schema["properties"]?["locked"]?["description"]?.GetValue<string>();

        // Assert
        description.ShouldNotBeNullOrWhiteSpace();
    }
}
