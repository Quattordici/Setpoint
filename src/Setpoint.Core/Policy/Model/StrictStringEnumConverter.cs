using System.Text.Json;
using System.Text.Json.Serialization;

namespace Setpoint.Core.Policy.Model;

/// <summary>
/// Reads an enum from its camelCase name only. Numbers are refused, so <c>allowedMergeTypes: [1]</c>
/// is an error instead of quietly meaning <c>squash</c>.
/// </summary>
/// <typeparam name="TEnum">The enum type.</typeparam>
public sealed class StrictStringEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;
