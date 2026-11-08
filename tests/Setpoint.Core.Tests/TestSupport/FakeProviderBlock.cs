using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Tests.TestSupport;

/// <summary>A provider block for core tests, so they don't depend on a real adapter.</summary>
public sealed record FakeProviderBlock : IProviderBlock
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<FakeOptions> MinimumReviewers { get; init; }
}

/// <summary>Options inside <see cref="FakeProviderBlock"/>.</summary>
public sealed record FakeOptions
{
    public bool Flag { get; init; }
}

/// <summary>Registers <see cref="FakeProviderBlock"/> under the key <c>fake</c>.</summary>
public sealed class FakeProviderBlockDefinition : IProviderBlockDefinition
{
    public static readonly FakeProviderBlockDefinition Instance = new();

    public string Key => "fake";

    public JsonTypeInfo TypeInfo => FakeJsonContext.Default.FakeProviderBlock;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(FakeProviderBlock))]
[JsonSerializable(typeof(FakeOptions))]
internal sealed partial class FakeJsonContext : JsonSerializerContext;
