using System.Text.Json.Serialization;

namespace Setpoint.Core.Planning;

/// <summary>The serialization contract for plan files. Reading is strict, so a hand-edited plan with an unknown key is refused.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    WriteIndented = true)]
[JsonSerializable(typeof(PlanDocument))]
internal sealed partial class PlanJsonContext : JsonSerializerContext;
