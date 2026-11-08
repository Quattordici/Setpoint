using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Setpoint.Core.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Setpoint.Core.Policy.Loading;

/// <summary>
/// Turns one YAML policy file into a JSON tree and a <see cref="SourceMap"/>, enforcing the format's YAML rules:
/// one document, scalar keys, no duplicate keys, no custom tags and no interpolation. Anchors, aliases and
/// <c>&lt;&lt;</c> merge keys work. Scalars resolve by the YAML 1.2 core schema, so <c>yes</c> stays a string.
/// </summary>
internal sealed partial class YamlJsonReader
{
    private const int MaxDepth = 64;
    private const string StandardTagPrefix = "tag:yaml.org,2002:";

    private readonly string _file;
    private readonly List<Diagnostic> _diagnostics;
    private readonly SourceMap _map;

    private YamlJsonReader(string file, List<Diagnostic> diagnostics)
    {
        _file = file;
        _diagnostics = diagnostics;
        _map = new SourceMap(file);
    }

    /// <summary>The result of reading one file.</summary>
    /// <param name="Root">The JSON tree, or null when the file could not be read at all or is empty.</param>
    /// <param name="Source">Key locations.</param>
    public sealed record Result(JsonNode? Root, SourceMap Source);

    /// <summary>Reads <paramref name="text"/>, adding any problems to <paramref name="diagnostics"/>.</summary>
    public static Result Read(string file, string text, List<Diagnostic> diagnostics)
    {
        var reader = new YamlJsonReader(file, diagnostics);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidYaml, CleanYamlMessage(ex), reader.At(ex.Start)));
            return new Result(null, reader._map);
        }

        switch (stream.Documents.Count)
        {
            case 0:
                return new Result(null, reader._map);
            case > 1:
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidYaml,
                    "A policy file holds one YAML document. Remove the extra '---' separators.",
                    reader.At(stream.Documents[1].RootNode.Start)));
                return new Result(null, reader._map);
        }

        YamlNode rootNode = stream.Documents[0].RootNode;
        reader._map.Record(JsonPath.Root, reader.At(rootNode.Start));
        JsonNode? root = reader.Convert(rootNode, JsonPath.Root, depth: 0);
        return new Result(root, reader._map);
    }

    private JsonNode? Convert(YamlNode node, string path, int depth)
    {
        if (depth > MaxDepth)
        {
            _diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.InvalidYaml,
                $"The YAML is nested more than {MaxDepth} levels deep, which usually means an alias refers to itself.",
                At(node.Start)));
            return null;
        }

        CheckTag(node);

        return node switch
        {
            YamlMappingNode mapping => ConvertMapping(mapping, path, depth),
            YamlSequenceNode sequence => ConvertSequence(sequence, path, depth),
            YamlScalarNode scalar => ConvertScalar(scalar),
            _ => null,
        };
    }

    private JsonObject ConvertMapping(YamlMappingNode mapping, string path, int depth)
    {
        var result = new JsonObject();
        var merges = new List<YamlNode>();

        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key } scalarKey)
            {
                _diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidYaml, "Keys must be plain text.", At(keyNode.Start)));
                continue;
            }

            if (key == "<<" && scalarKey.Style == ScalarStyle.Plain)
            {
                merges.Add(valueNode);
                continue;
            }

            string childPath = JsonPath.Property(path, key);
            _map.Record(childPath, At(keyNode.Start));
            result[key] = Convert(valueNode, childPath, depth + 1);
        }

        // Merge keys: explicit keys win, and among several merged maps the first one wins (YAML merge key spec).
        foreach (YamlNode merge in merges)
        {
            IEnumerable<YamlNode> sources = merge is YamlSequenceNode list ? list.Children : [merge];
            foreach (YamlNode source in sources)
            {
                if (source is not YamlMappingNode sourceMapping)
                {
                    _diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.InvalidYaml,
                        "A '<<' merge key must refer to a map, or a list of maps.",
                        At(source.Start)));
                    continue;
                }

                JsonObject merged = ConvertMapping(sourceMapping, path, depth + 1);
                foreach ((string key, JsonNode? value) in merged.ToList())
                {
                    if (!result.ContainsKey(key))
                    {
                        merged.Remove(key);
                        result[key] = value;
                    }
                }
            }
        }

        return result;
    }

    private JsonArray ConvertSequence(YamlSequenceNode sequence, string path, int depth)
    {
        var result = new JsonArray();
        for (int i = 0; i < sequence.Children.Count; i++)
        {
            YamlNode child = sequence.Children[i];
            string childPath = JsonPath.Index(path, i);
            _map.Record(childPath, At(child.Start));
            result.Add(Convert(child, childPath, depth + 1));
        }

        return result;
    }

    private JsonValue? ConvertScalar(YamlScalarNode scalar)
    {
        string value = scalar.Value ?? string.Empty;
        bool forceString = scalar.Style != ScalarStyle.Plain
            || (!scalar.Tag.IsEmpty && !scalar.Tag.IsNonSpecific && scalar.Tag.Value == StandardTagPrefix + "str");

        if (!forceString)
        {
            if (value is "" or "~" or "null" or "Null" or "NULL")
            {
                return null;
            }

            if (value is "true" or "True" or "TRUE")
            {
                return JsonValue.Create(true);
            }

            if (value is "false" or "False" or "FALSE")
            {
                return JsonValue.Create(false);
            }

            if (IntegerPattern().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
            {
                return JsonValue.Create(integer);
            }

            if (FloatPattern().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                return JsonValue.Create(number);
            }
        }

        if (InterpolationPattern().IsMatch(value))
        {
            _diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.Interpolation,
                $"'{value}' looks like variable interpolation, which Setpoint never expands. " +
                "Write the value itself; secrets come only from environment variables named in targets.yml.",
                At(scalar.Start)));
        }

        return JsonValue.Create(value);
    }

    private void CheckTag(YamlNode node)
    {
        if (node.Tag.IsEmpty || node.Tag.IsNonSpecific || node.Tag.Value.StartsWith(StandardTagPrefix, StringComparison.Ordinal))
        {
            return;
        }

        _diagnostics.Add(Diagnostic.Error(
            DiagnosticCodes.CustomTag,
            $"Custom YAML tags such as '{node.Tag.Value}' are not supported. Policy files are plain YAML; anchors and aliases are fine.",
            At(node.Start)));
    }

    private SourceLocation At(Mark mark) => new(_file, (int)mark.Line, (int)mark.Column);

    private static string CleanYamlMessage(YamlException ex)
    {
        // YamlDotNet prefixes messages with "(Line: 3, Col: 5, Idx: 20) - (Line: ...): "; the location is reported separately.
        string message = ex.Message;
        int separator = message.IndexOf("): ", StringComparison.Ordinal);
        return separator >= 0 && message.StartsWith('(') ? message[(separator + 3)..] : message;
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatPattern();

    // ${VAR}, ${{ expr }} (GitHub Actions, Azure Pipelines templates) and $(Var) (Azure Pipelines macros).
    [GeneratedRegex(@"\$\{[^}]*\}|\$\([A-Za-z_][A-Za-z0-9_.]*\)")]
    private static partial Regex InterpolationPattern();
}
