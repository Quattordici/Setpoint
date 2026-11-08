using System.Text.Json;
using System.Text.Json.Serialization;

namespace Setpoint.Core.Policy.Model;

/// <summary>What a layer says about one rule.</summary>
public enum DeclaredState
{
    /// <summary>The layer doesn't mention the rule, so the layer above decides.</summary>
    NotDeclared,

    /// <summary>The layer sets the rule to <c>~</c>: Setpoint leaves it alone from this layer down.</summary>
    Unmanaged,

    /// <summary>The layer gives the rule a value, which replaces any value from the layers above.</summary>
    Value,
}

/// <summary>The non-generic view of <see cref="Declared{T}"/>, for code that walks rules without knowing their types.</summary>
public interface IDeclared
{
    /// <summary>What the layer says about the rule.</summary>
    DeclaredState State { get; }

    /// <summary>The value when <see cref="State"/> is <see cref="DeclaredState.Value"/>; otherwise null.</summary>
    object? BoxedValue { get; }
}

/// <summary>
/// One rule's value in one layer. YAML has three cases a plain nullable can't tell apart:
/// the key is absent (<see cref="DeclaredState.NotDeclared"/>), the key is <c>~</c>
/// (<see cref="DeclaredState.Unmanaged"/>), or the key has a value.
/// </summary>
/// <typeparam name="T">The rule's value type.</typeparam>
[JsonConverter(typeof(DeclaredJsonConverterFactory))]
public readonly record struct Declared<T> : IDeclared
{
    private readonly T? _value;

    private Declared(DeclaredState state, T? value)
    {
        State = state;
        _value = value;
    }

    /// <summary>A rule set to <c>~</c>.</summary>
    public static Declared<T> Unmanaged => new(DeclaredState.Unmanaged, default);

    /// <summary>What the layer says about the rule.</summary>
    public DeclaredState State { get; }

    /// <summary>True when the layer mentions the rule at all, with a value or with <c>~</c>.</summary>
    public bool IsDeclared => State != DeclaredState.NotDeclared;

    /// <summary>True when the layer gives the rule a value.</summary>
    public bool HasValue => State == DeclaredState.Value;

    /// <summary>The value. Only valid when <see cref="HasValue"/> is true.</summary>
    /// <exception cref="InvalidOperationException">The layer gives no value.</exception>
    public T Value => HasValue ? _value! : throw new InvalidOperationException($"The rule is {State}, so it has no value.");

    /// <summary>A rule with a value.</summary>
    public static Declared<T> Of(T value) => new(DeclaredState.Value, value);

    /// <inheritdoc />
    object? IDeclared.BoxedValue => HasValue ? _value : null;

    /// <inheritdoc />
    public override string ToString() => State switch
    {
        DeclaredState.NotDeclared => "(not declared)",
        DeclaredState.Unmanaged => "~",
        _ => _value?.ToString() ?? string.Empty,
    };
}

/// <summary>Reads and writes <see cref="Declared{T}"/>: <c>null</c> is unmanaged, anything else is a value.</summary>
public sealed class DeclaredJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Declared<>);

    /// <inheritdoc />
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type valueType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter?)Activator.CreateInstance(typeof(DeclaredJsonConverter<>).MakeGenericType(valueType));
    }
}

/// <summary>The converter for one <see cref="Declared{T}"/>.</summary>
/// <typeparam name="T">The rule's value type.</typeparam>
public sealed class DeclaredJsonConverter<T> : JsonConverter<Declared<T>>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override Declared<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return Declared<T>.Unmanaged;
        }

        var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        try
        {
            T value = JsonSerializer.Deserialize(ref reader, typeInfo)
                ?? throw new JsonException("A value is required.");
            return Declared<T>.Of(value);
        }
        catch (JsonException ex)
        {
            // The nested call reports a path relative to this value; keep it, and let the outer
            // serializer fill in the path to here.
            throw NestedJsonException.Wrap(ex);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Declared<T> value, JsonSerializerOptions options)
    {
        if (!value.HasValue)
        {
            writer.WriteNullValue();
            return;
        }

        var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        JsonSerializer.Serialize(writer, value.Value, typeInfo);
    }
}
