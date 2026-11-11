using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Setpoint.Adapters.AzureDevOps.Api;

/// <summary>A list response: Azure DevOps wraps every collection in <c>{ "count": n, "value": [...] }</c>.</summary>
internal sealed record AdoList<T>(List<T> Value);

/// <summary>One REST resource location from <c>OPTIONS _apis/{area}</c>, with the API versions the server has for it.</summary>
internal sealed record AdoResourceLocation(string? Area, string? ResourceName, string? MaxVersion, string? ReleasedVersion);

/// <summary>A team project, from <c>GET _apis/projects</c>.</summary>
internal sealed record AdoProject(string Id, string Name);

/// <summary>A policy type the server knows, from <c>GET {project}/_apis/policy/types</c>.</summary>
internal sealed record AdoPolicyType(string Id, string? DisplayName);

/// <summary>A Git repo, from <c>GET _apis/git/repositories</c>. An empty repo has no default branch.</summary>
internal sealed record AdoRepository
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required AdoProject Project { get; init; }

    /// <summary>The default branch as a full ref, such as <c>refs/heads/main</c>. Missing on an empty repo.</summary>
    public string? DefaultBranch { get; init; }

    public bool IsDisabled { get; init; }

    public bool IsFork { get; init; }

    public bool IsInMaintenance { get; init; }
}

/// <summary>A pipeline (build definition), from <c>GET {project}/_apis/build/definitions</c>.</summary>
internal sealed record AdoBuildDefinition(int Id, string Name, string? Path);

/// <summary>An identity, from <c>GET _apis/identities</c>. The descriptor is the one ACLs are keyed on.</summary>
internal sealed record AdoIdentity(string? Descriptor, string? ProviderDisplayName, string? CustomDisplayName, string? Id = null);

/// <summary>The Azure DevOps REST API's JSON contract: camelCase, and every field Setpoint doesn't read is ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AdoList<AdoResourceLocation>))]
[JsonSerializable(typeof(AdoList<AdoProject>))]
[JsonSerializable(typeof(AdoList<AdoPolicyType>))]
[JsonSerializable(typeof(AdoList<AdoRepository>))]
[JsonSerializable(typeof(AdoList<AdoBuildDefinition>))]
[JsonSerializable(typeof(AdoList<AdoIdentity>))]
[JsonSerializable(typeof(AdoList<JsonObject>))]
[JsonSerializable(typeof(JsonObject))]
internal sealed partial class AzureDevOpsApiJsonContext : JsonSerializerContext;
