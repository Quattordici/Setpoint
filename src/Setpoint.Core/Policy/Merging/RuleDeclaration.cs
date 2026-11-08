using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Policy.Merging;

/// <summary>
/// One rule as one layer declares it. Rules are keyed by name: <c>minimumReviewers</c> for a neutral rule,
/// <c>github.pullRequest</c> for a provider-block entry.
/// </summary>
/// <param name="Key">The rule key.</param>
/// <param name="State">Unmanaged or a value; layers that don't declare a rule have no declaration.</param>
/// <param name="Value">The value, or null when unmanaged.</param>
/// <param name="Canonical">The value as JSON, used to tell whether two layers declare the same thing.</param>
/// <param name="Layer">Where it was declared.</param>
public sealed record RuleDeclaration(string Key, DeclaredState State, object? Value, JsonNode? Canonical, LayerRef Layer)
{
    /// <summary>True when the two declarations mean the same, whichever layers they come from.</summary>
    public bool SameAs(RuleDeclaration other) => State == other.State && JsonNode.DeepEquals(Canonical, other.Canonical);

    /// <summary>Reads every declared rule out of one layer file.</summary>
    internal static IEnumerable<RuleDeclaration> From(
        PolicyFile<PolicyLayer> file,
        IReadOnlyList<IProviderBlockDefinition> providers)
    {
        PolicyLayer layer = file.Content;
        foreach (RuleDeclaration declaration in FromObject(layer.Rules, PolicyJsonContext.Default.NeutralRules, prefix: null, "$.rules", file))
        {
            yield return declaration;
        }

        foreach ((string key, IProviderBlock block) in layer.Providers)
        {
            IProviderBlockDefinition? definition = providers.FirstOrDefault(p => p.Key == key);
            if (definition is null)
            {
                continue;
            }

            foreach (RuleDeclaration declaration in FromObject(block, definition.TypeInfo, key, $"$.{key}", file))
            {
                yield return declaration;
            }
        }
    }

    private static IEnumerable<RuleDeclaration> FromObject(
        object container,
        JsonTypeInfo typeInfo,
        string? prefix,
        string basePath,
        PolicyFile<PolicyLayer> file)
    {
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.Get?.Invoke(container) is not IDeclared { State: not DeclaredState.NotDeclared } declared)
            {
                continue;
            }

            string key = prefix is null ? property.Name : $"{prefix}.{property.Name}";
            JsonNode? canonical = declared.State == DeclaredState.Value
                ? JsonSerializer.SerializeToNode(declared, typeInfo.Options.GetTypeInfo(property.PropertyType))
                : null;
            var layer = new LayerRef(
                file.Content.Layer,
                file.Content.Layer == LayerKind.Group ? file.Content.Name : null,
                file.Source.Find($"{basePath}.{property.Name}"));
            yield return new RuleDeclaration(key, declared.State, declared.BoxedValue, canonical, layer);
        }
    }
}
