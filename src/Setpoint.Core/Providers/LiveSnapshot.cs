using System.Text.Json.Nodes;

namespace Setpoint.Core.Providers;

/// <summary>
/// A repo's live state, normalised by the adapter. Rule values use the same JSON shape as the policy:
/// <c>minimumReviewers</c> is <c>{ "count": 2, "resetOnPush": true }</c>, a boolean rule is <c>true</c>, and a
/// list rule is an array whose order doesn't matter.
/// </summary>
/// <param name="Objects">The live native objects Setpoint could own, each matched to the rules it carries.</param>
/// <param name="Unmanaged">Settings outside Setpoint's ownership, such as another ruleset or classic branch protection.</param>
/// <param name="Ambiguous">Rules more than one live object matches. Setpoint leaves them alone and warns (SP2201).</param>
/// <param name="UnknownRules">Rules whose live value the token can't see. Plan reports them as unknown (SP2202), not as changes.</param>
public sealed record LiveSnapshot(
    IReadOnlyList<LiveObject> Objects,
    IReadOnlyList<UnmanagedSetting> Unmanaged,
    IReadOnlyList<AmbiguousMatch> Ambiguous,
    IReadOnlyList<string> UnknownRules)
{
    /// <summary>A repo with nothing on it.</summary>
    public static readonly LiveSnapshot Empty = new([], [], [], []);
}

/// <summary>One live native object, such as an Azure DevOps policy configuration or the <c>setpoint</c> ruleset.</summary>
/// <param name="Key">The object's key in the plan file, such as <c>ado:policy/17</c> or <c>gh:ruleset/setpoint</c>.</param>
/// <param name="MatchKey">
/// What the object is matched on, such as <c>ado:minimumReviewers/refs/heads/main</c>. A <see cref="DesiredObject"/>
/// with the same match key is the same object; a plan that creates it fingerprints "nothing matches this key".
/// </param>
/// <param name="Native">The object as the platform returned it, minus fields that change on every read. The fingerprint is taken over it.</param>
/// <param name="Rules">The rule values the object carries, keyed by rule (<c>minimumReviewers</c>, <c>github.pullRequest</c>).</param>
/// <param name="Title">What the object is, for people: <c>Minimum number of reviewers #17</c>, <c>ruleset setpoint</c>.</param>
public sealed record LiveObject(string Key, string MatchKey, JsonNode Native, IReadOnlyDictionary<string, JsonNode?> Rules, string? Title = null);

/// <summary>A live setting Setpoint doesn't own.</summary>
/// <param name="Rule">The neutral or provider rule it bears on, such as <c>minimumReviewers</c>.</param>
/// <param name="Source">Where it lives, for people: <c>ruleset 'org-protect'</c>.</param>
/// <param name="Value">Its value in the rule's normalised shape, used to tell whether it is stricter than the policy.</param>
public sealed record UnmanagedSetting(string Rule, string Source, JsonNode? Value);

/// <summary>A rule that several live objects match.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="ObjectKeys">The matching objects.</param>
/// <param name="Detail">What matched, for people: <c>type + branch main</c>.</param>
public sealed record AmbiguousMatch(string Rule, IReadOnlyList<string> ObjectKeys, string Detail);
