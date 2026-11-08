using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.AzureDevOps.Policy;

/// <summary>Tells the core about the <c>azuredevops:</c> provider block.</summary>
public sealed class AzureDevOpsProviderBlockDefinition : IProviderBlockDefinition
{
    /// <summary>The one instance.</summary>
    public static readonly AzureDevOpsProviderBlockDefinition Instance = new();

    private AzureDevOpsProviderBlockDefinition()
    {
    }

    /// <inheritdoc />
    public string Key => "azuredevops";

    /// <inheritdoc />
    public JsonTypeInfo TypeInfo => AzureDevOpsPolicyJsonContext.Default.AzureDevOpsProviderBlock;
}

/// <summary>The serialization contract for the Azure DevOps provider block, with the same options as the core policy contract.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(AzureDevOpsProviderBlock))]
[JsonSerializable(typeof(AzureDevOpsMinimumReviewersOptions))]
[JsonSerializable(typeof(AzureDevOpsRequiredChecksOptions))]
[JsonSerializable(typeof(AzureDevOpsMergeTypesOptions))]
internal sealed partial class AzureDevOpsPolicyJsonContext : JsonSerializerContext;
