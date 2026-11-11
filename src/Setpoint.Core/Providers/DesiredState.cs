using System.Text.Json.Nodes;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Merging;

namespace Setpoint.Core.Providers;

/// <summary>
/// The managed rules for one repo, as an adapter receives them: the neutral rules and the adapter's own provider
/// block, never another adapter's. Unmanaged and undeclared rules are not here.
/// </summary>
/// <param name="Repo">The repo.</param>
/// <param name="Rules">The managed rules, keyed by rule.</param>
public sealed record DesiredState(RepoRef Repo, IReadOnlyDictionary<string, DesiredRule> Rules);

/// <summary>One managed rule.</summary>
/// <param name="Key">The rule key.</param>
/// <param name="Value">The normalised value, or null when the setting must be absent.</param>
/// <param name="Layer">The layer the value came from.</param>
/// <param name="Locked">True when the baseline locks the rule.</param>
public sealed record DesiredRule(string Key, JsonNode? Value, LayerRef Layer, bool Locked)
{
    /// <summary>True when the setting must be absent.</summary>
    public bool MustBeAbsent => Value is null;
}

/// <summary>A native object as the policy wants it.</summary>
/// <param name="MatchKey">The same match key the adapter gives the live object, so the core can pair them.</param>
/// <param name="CreateKey">The object's plan key if it has to be created, such as <c>ado:new/build/payments-ci</c>.</param>
/// <param name="Rules">The rule values it would carry; a null value means the rule is removed from it.</param>
/// <param name="Payload">The full payload apply sends, or null when the object must not exist.</param>
/// <param name="Title">What the object is, for people, when it has to be created: <c>Build validation (new)</c>.</param>
/// <param name="SharedBy">
/// Set when the object isn't the repo's own but shared by several repos, such as an Azure DevOps environment, which
/// belongs to the project: the scope it is shared in, such as <c>project Payments</c>. Repos in one scope must
/// declare it the same way (SP0122), and the plan writes it once.
/// </param>
public sealed record DesiredObject(string MatchKey, string CreateKey, IReadOnlyDictionary<string, JsonNode?> Rules, JsonNode? Payload, string? Title = null, string? SharedBy = null);
