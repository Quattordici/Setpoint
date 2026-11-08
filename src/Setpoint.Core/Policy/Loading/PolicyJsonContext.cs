using System.Text.Json.Serialization;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Core.Policy.Loading;

/// <summary>
/// The serialization contract for the core policy model. Adapters declare their provider-block
/// contexts with the same options, so every part of a policy file is read the same strict way.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(TargetsFile))]
[JsonSerializable(typeof(PolicyLayer))]
[JsonSerializable(typeof(MinimumReviewersRule))]
[JsonSerializable(typeof(IReadOnlyList<RequiredCheck>))]
[JsonSerializable(typeof(IReadOnlyList<MergeType>))]
[JsonSerializable(typeof(BooleanRule))]
internal sealed partial class PolicyJsonContext : JsonSerializerContext;
