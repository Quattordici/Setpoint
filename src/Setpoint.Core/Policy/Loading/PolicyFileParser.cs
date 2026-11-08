using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Policy.Loading;

/// <summary>
/// Parses one policy file in stages: YAML, then <c>version</c>, then unknown keys, then value types.
/// A stage with errors stops the file, so one mistake doesn't cascade into a wall of follow-on errors.
/// </summary>
internal static partial class PolicyFileParser
{
    public const int SupportedVersion = 1;

    public static PolicyFile<TargetsFile>? ParseTargets(string path, string text, List<Diagnostic> diagnostics)
    {
        if (ReadRoot(path, text, diagnostics) is not ({ } root, { } source))
        {
            return null;
        }

        var errorsBefore = diagnostics.Count;
        new ShapeChecker(source, diagnostics).Check(root, PolicyJsonContext.Default.TargetsFile, JsonPath.Root);
        if (diagnostics.Count > errorsBefore)
        {
            return null;
        }

        TargetsFile? targets = Deserialize(root, PolicyJsonContext.Default.TargetsFile, JsonPath.Root, source, diagnostics);
        return targets is null ? null : new PolicyFile<TargetsFile>(path, targets, source);
    }

    public static PolicyFile<PolicyLayer>? ParseLayer(
        string path,
        string text,
        IReadOnlyList<IProviderBlockDefinition> providers,
        List<Diagnostic> diagnostics)
    {
        if (ReadRoot(path, text, diagnostics) is not ({ } root, { } source))
        {
            return null;
        }

        // Unknown keys first, across the core keys and every provider block.
        int errorsBefore = diagnostics.Count;
        var checker = new ShapeChecker(source, diagnostics);
        JsonTypeInfo<PolicyLayer> layerInfo = PolicyJsonContext.Default.PolicyLayer;
        var coreObject = new JsonObject();
        var providerNodes = new List<(IProviderBlockDefinition Definition, JsonNode? Node)>();

        foreach ((string key, JsonNode? value) in root)
        {
            IProviderBlockDefinition? provider = providers.FirstOrDefault(p => p.Key == key);
            if (provider is not null)
            {
                checker.Check(value, provider.TypeInfo, JsonPath.Property(JsonPath.Root, key));
                providerNodes.Add((provider, value));
            }
            else if (layerInfo.Properties.Any(p => p.Name == key && p.Set is not null))
            {
                coreObject[key] = value?.DeepClone();
            }
            else
            {
                checker.ReportUnknownKey(
                    key,
                    layerInfo.Properties.Where(p => p.Set is not null).Select(p => p.Name).Concat(providers.Select(p => p.Key)),
                    JsonPath.Root);
            }
        }

        checker.Check(coreObject, layerInfo, JsonPath.Root);
        if (diagnostics.Count > errorsBefore)
        {
            return null;
        }

        // Then value types.
        PolicyLayer? layer = Deserialize(coreObject, layerInfo, JsonPath.Root, source, diagnostics);
        var blocks = new Dictionary<string, IProviderBlock>(StringComparer.Ordinal);
        foreach ((IProviderBlockDefinition definition, JsonNode? node) in providerNodes)
        {
            string blockPath = JsonPath.Property(JsonPath.Root, definition.Key);
            if (node is null)
            {
                continue;
            }

            if (Deserialize(node, definition.TypeInfo, blockPath, source, diagnostics) is IProviderBlock block)
            {
                blocks[definition.Key] = block;
            }
        }

        if (layer is null || diagnostics.Count > errorsBefore)
        {
            return null;
        }

        return new PolicyFile<PolicyLayer>(path, layer with { Providers = blocks }, source);
    }

    private static (JsonObject? Root, SourceMap? Source) ReadRoot(string path, string text, List<Diagnostic> diagnostics)
    {
        int errorsBefore = diagnostics.Count;
        YamlJsonReader.Result result = YamlJsonReader.Read(path, text, diagnostics);
        if (diagnostics.Count > errorsBefore)
        {
            return (null, null);
        }

        if (result.Root is not JsonObject root)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.UnsupportedVersion,
                $"A policy file must be a map that starts with 'version: {SupportedVersion}'.",
                result.Source.Find(JsonPath.Root)));
            return (null, null);
        }

        string versionPath = JsonPath.Property(JsonPath.Root, "version");
        if (!root.TryGetPropertyValue("version", out JsonNode? version))
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.UnsupportedVersion,
                $"Every policy file must declare 'version: {SupportedVersion}'.",
                SourceLocation.StartOf(path)));
            return (null, null);
        }

        if (version is not JsonValue value || !value.TryGetValue(out long number) || number != SupportedVersion)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.UnsupportedVersion,
                $"'version: {version?.ToJsonString() ?? "~"}' is not supported. This Setpoint reads version {SupportedVersion}.",
                result.Source.Find(versionPath)));
            return (null, null);
        }

        return (root, result.Source);
    }

    private static T? Deserialize<T>(JsonNode node, JsonTypeInfo<T> typeInfo, string basePath, SourceMap source, List<Diagnostic> diagnostics) =>
        (T?)Deserialize(node, (JsonTypeInfo)typeInfo, basePath, source, diagnostics);

    private static object? Deserialize(JsonNode node, JsonTypeInfo typeInfo, string basePath, SourceMap source, List<Diagnostic> diagnostics)
    {
        try
        {
            return node.Deserialize(typeInfo);
        }
        catch (JsonException ex)
        {
            string relative = ex is NestedJsonException nested ? nested.FullPath : ex.Path ?? JsonPath.Root;
            string path = basePath + relative.TrimStart('$');
            string message = ex is NestedJsonException n ? n.OriginalMessage : ex.Message;
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.InvalidValue,
                DescribeJsonError(message, path, ExpectedType(typeInfo, relative)),
                source.Find(path)));
            return null;
        }
    }

    /// <summary>Turns a serializer message, written for developers, into one for a policy author.</summary>
    private static string DescribeJsonError(string message, string path, Type? expected)
    {
        string where = $"'{JsonPath.ForDisplay(path)}'";

        if (MissingRequiredPattern().Match(message) is { Success: true } missing)
        {
            return $"{where} is missing required key(s): {missing.Groups["names"].Value}.";
        }

        if (NullNotAllowedPattern().IsMatch(message))
        {
            return $"{where} can't be ~ (empty).";
        }

        if (ConvertPattern().IsMatch(message) && expected is not null)
        {
            return $"{where} must be {DescribeType(expected)}.";
        }

        // Our own converters write messages for people already; strip the serializer's position suffix.
        int suffix = message.IndexOf(" Path:", StringComparison.Ordinal);
        return $"{where}: {(suffix >= 0 ? message[..suffix] : message)}";
    }

    /// <summary>Finds the model type at <paramref name="path"/> by walking the contract from <paramref name="root"/>.</summary>
    private static Type? ExpectedType(JsonTypeInfo root, string path)
    {
        JsonTypeInfo? current = root;
        foreach (Match segment in PathSegmentPattern().Matches(path))
        {
            current = Unwrap(current);
            if (current is null)
            {
                return null;
            }

            string? name = segment.Groups["name"].Success ? segment.Groups["name"].Value
                : segment.Groups["quoted"].Success ? segment.Groups["quoted"].Value
                : null;

            current = current.Kind switch
            {
                JsonTypeInfoKind.Object when name is not null =>
                    current.Properties.FirstOrDefault(p => p.Name == name) is { } property
                        ? current.Options.GetTypeInfo(property.PropertyType)
                        : null,
                JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary when current.ElementType is { } element =>
                    current.Options.GetTypeInfo(element),
                _ => null,
            };
        }

        return Unwrap(current)?.Type;
    }

    private static JsonTypeInfo? Unwrap(JsonTypeInfo? typeInfo)
    {
        if (typeInfo is null)
        {
            return null;
        }

        Type type = typeInfo.Type;
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Declared<>) || type.GetGenericTypeDefinition() == typeof(Nullable<>)))
        {
            return Unwrap(typeInfo.Options.GetTypeInfo(type.GetGenericArguments()[0]));
        }

        return typeInfo;
    }

    private static string DescribeType(Type type)
    {
        if (type == typeof(int) || type == typeof(long))
        {
            return "a whole number";
        }

        if (type == typeof(bool))
        {
            return "true or false";
        }

        if (type == typeof(string))
        {
            return "text";
        }

        if (type == typeof(Uri))
        {
            return "a URL";
        }

        if (type == typeof(BooleanRule))
        {
            return "true, false or { enabled: true|false }";
        }

        if (type.IsEnum)
        {
            IEnumerable<string> names = type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(field =>
                field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                    ?? JsonNamingPolicy.CamelCase.ConvertName(field.Name));
            return $"one of {string.Join(", ", names)}";
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() is var definition
            && (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(Dictionary<,>)))
        {
            return "a map";
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() is var list
            && (list == typeof(IReadOnlyList<>) || list == typeof(List<>)))
        {
            return "a list";
        }

        return "a map with the documented keys";
    }

    [GeneratedRegex(@"missing required properties(?: including)?: (?<names>.+?)\.?$", RegexOptions.Multiline)]
    private static partial Regex MissingRequiredPattern();

    [GeneratedRegex(@"doesn't allow (?:setting|getting) null values|cannot be null|was null")]
    private static partial Regex NullNotAllowedPattern();

    [GeneratedRegex(@"could not be converted to")]
    private static partial Regex ConvertPattern();

    [GeneratedRegex(@"\.(?<name>[^.\[]+)|\['(?<quoted>(?:[^']|\\')*)'\]|\[(?<index>[0-9]+)\]")]
    private static partial Regex PathSegmentPattern();
}
