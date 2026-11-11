using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.GitHub.Policy;

/// <summary>Tells the core about the <c>github:</c> provider block.</summary>
public sealed class GitHubProviderBlockDefinition : IProviderBlockDefinition
{
    /// <summary>The one instance.</summary>
    public static readonly GitHubProviderBlockDefinition Instance = new();

    private GitHubProviderBlockDefinition()
    {
    }

    /// <inheritdoc />
    public string Key => "github";

    /// <inheritdoc />
    public JsonTypeInfo TypeInfo => GitHubPolicyJsonContext.Default.GitHubProviderBlock;
}

/// <summary>The serialization contract for the GitHub provider block, with the same options as the core policy contract.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(GitHubProviderBlock))]
[JsonSerializable(typeof(GitHubPullRequestOptions))]
[JsonSerializable(typeof(GitHubRequiredChecksOptions))]
[JsonSerializable(typeof(IReadOnlyList<GitHubBypassActor>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, GitHubTeamRole>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, GitHubEnvironmentOptions>))]
internal sealed partial class GitHubPolicyJsonContext : JsonSerializerContext;
