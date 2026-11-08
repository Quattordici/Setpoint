using System.Text.Json.Serialization.Metadata;

namespace Setpoint.Core.Providers;

/// <summary>
/// What an adapter tells the core about its provider block: the YAML key it lives under,
/// and the serialization contract the loader reads it with and the schema is generated from.
/// </summary>
public interface IProviderBlockDefinition
{
    /// <summary>The top-level key in a layer file, such as <c>github</c>.</summary>
    string Key { get; }

    /// <summary>The contract for the block type, which implements <see cref="IProviderBlock"/>.</summary>
    JsonTypeInfo TypeInfo { get; }
}
