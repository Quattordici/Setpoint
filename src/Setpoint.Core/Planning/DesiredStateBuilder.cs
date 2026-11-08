using System.Text.Json.Nodes;
using Setpoint.Core.Policy.Merging;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Planning;

/// <summary>Turns a repo's effective policy into the desired state one adapter receives.</summary>
public static class DesiredStateBuilder
{
    /// <summary>
    /// The managed rules for <paramref name="adapter"/>: neutral rules and its own provider block. Unmanaged rules
    /// are left out. Values are normalised: a boolean rule is <c>true</c>, an object rule drops <c>enabled</c>,
    /// and a rule that must be absent is null.
    /// </summary>
    public static DesiredState Build(EffectivePolicy effective, IProviderAdapter adapter)
    {
        string? providerPrefix = adapter.ProviderBlock is { } block ? block.Key + "." : null;
        var rules = new Dictionary<string, DesiredRule>(StringComparer.Ordinal);
        foreach (EffectiveRule rule in effective.Rules.Values)
        {
            bool mine = !rule.Key.Contains('.', StringComparison.Ordinal)
                || (providerPrefix is not null && rule.Key.StartsWith(providerPrefix, StringComparison.Ordinal));
            if (!mine || rule.IsUnmanaged)
            {
                continue;
            }

            rules[rule.Key] = new DesiredRule(rule.Key, Normalise(rule), rule.Source, rule.Locked);
        }

        return new DesiredState(effective.Repo, rules);
    }

    /// <summary>The normalised value of one rule, or null when the setting must be absent.</summary>
    public static JsonNode? Normalise(EffectiveRule rule)
    {
        if (rule.MustBeAbsent)
        {
            return null;
        }

        if (rule.Value is BooleanRule)
        {
            return JsonValue.Create(true);
        }

        JsonNode? value = CanonicalJson.ForRule(rule.Canonical);
        if (rule.Value is IToggleableRule && value is JsonObject obj)
        {
            obj.Remove("enabled");
        }

        return value;
    }
}
