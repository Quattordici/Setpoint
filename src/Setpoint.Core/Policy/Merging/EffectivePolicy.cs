using System.Text.Json.Nodes;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Core.Policy.Merging;

/// <summary>One rule's effective value for one repo, after merging the layers.</summary>
/// <param name="Key">The rule key, such as <c>minimumReviewers</c> or <c>azuredevops.requiredChecks</c>.</param>
/// <param name="State">Unmanaged, or a value.</param>
/// <param name="Value">The value, or null when unmanaged.</param>
/// <param name="Source">The layer the value came from.</param>
/// <param name="Locked">True when the baseline locks this rule.</param>
/// <param name="Canonical">The value as JSON, as the layer declared it; null when unmanaged.</param>
public sealed record EffectiveRule(string Key, DeclaredState State, object? Value, LayerRef Source, bool Locked, JsonNode? Canonical = null)
{
    /// <summary>True when the rule says the setting must be absent: <c>false</c> or <c>enabled: false</c>.</summary>
    public bool MustBeAbsent => Value is IToggleableRule { Enabled: false };

    /// <summary>True when Setpoint leaves this setting alone.</summary>
    public bool IsUnmanaged => State == DeclaredState.Unmanaged;
}

/// <summary>The effective policy for one repo: every rule some layer declares, with where it came from.</summary>
/// <param name="Repo">The repo.</param>
/// <param name="Rules">The declared rules, keyed by rule key. Rules no layer mentions are not here.</param>
/// <param name="Groups">The groups whose selectors matched, in file order.</param>
/// <param name="HasRepoOverride">True when the repo has a repo override file.</param>
public sealed record EffectivePolicy(RepoRef Repo, IReadOnlyDictionary<string, EffectiveRule> Rules, IReadOnlyList<string> Groups, bool HasRepoOverride = false)
{
    /// <summary>The layers that apply to the repo, as plan output labels them: <c>baseline</c>, group names, <c>repo</c>.</summary>
    public IReadOnlyList<string> Layers => ["baseline", .. Groups, .. HasRepoOverride ? new[] { "repo" } : []];

    /// <summary>The effective value of a rule, or null when it is not declared or is unmanaged.</summary>
    public T? Get<T>(string key)
        where T : class =>
        Rules.TryGetValue(key, out EffectiveRule? rule) ? rule.Value as T : null;

    /// <summary>The effective rule, or null when no layer declares it.</summary>
    public EffectiveRule? Find(string key) => Rules.GetValueOrDefault(key);
}
