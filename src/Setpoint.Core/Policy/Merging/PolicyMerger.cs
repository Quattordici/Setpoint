using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Policy.Selection;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Policy.Merging;

/// <summary>
/// Builds a repo's effective policy from baseline, then the groups whose selectors match it, then its repo override.
/// A lower layer replaces a rule's value wholesale, and <c>~</c> makes it unmanaged. Two matching groups that
/// declare the same rule differently are an error, and a locked baseline rule ignores lower layers.
/// </summary>
public sealed class PolicyMerger
{
    private readonly IReadOnlyList<RuleDeclaration> _baseline;
    private readonly HashSet<string> _locked;
    private readonly IReadOnlyList<(PolicyFile<PolicyLayer> File, IReadOnlyList<RepoGlob> Selectors, IReadOnlyList<RuleDeclaration> Rules)> _groups;
    private readonly Dictionary<RepoRef, IReadOnlyList<RuleDeclaration>> _overrides;

    public PolicyMerger(PolicyRepo policy, IReadOnlyList<IProviderBlockDefinition> providers)
    {
        _baseline = [.. RuleDeclaration.From(policy.Baseline, providers)];
        _locked = new HashSet<string>(policy.Baseline.Content.Locked ?? [], StringComparer.Ordinal);
        _groups =
        [
            .. policy.Groups.Select(g => (
                g,
                (IReadOnlyList<RepoGlob>)[.. (g.Content.Selectors ?? []).Select(s => RepoGlob.TryParse(s, 3, out _)).OfType<RepoGlob>()],
                (IReadOnlyList<RuleDeclaration>)[.. RuleDeclaration.From(g, providers)])),
        ];
        _overrides = [];
        foreach (PolicyFile<PolicyLayer> file in policy.RepoOverrides)
        {
            if (file.Content.Repo is { } name && RepoRef.TryParse(name) is { } repo)
            {
                _overrides[repo] = [.. RuleDeclaration.From(file, providers)];
            }
        }
    }

    /// <summary>The repos named by repo overrides, which can be merged without live access.</summary>
    public IEnumerable<RepoRef> RepoOverrideRepos => _overrides.Keys;

    /// <summary>The repos named by exact (wildcard-free) group selectors.</summary>
    public IEnumerable<RepoRef> ExactlySelectedRepos =>
        _groups.SelectMany(g => g.Selectors).Where(s => s.IsExact).Select(s => new RepoRef(s.Parts[0], s.Parts[1], s.Parts[2])).Distinct();

    /// <summary>Merges the layers for <paramref name="repo"/>, adding any conflicts to <paramref name="diagnostics"/>.</summary>
    public EffectivePolicy Merge(RepoRef repo, List<Diagnostic> diagnostics)
    {
        var effective = new Dictionary<string, EffectiveRule>(StringComparer.Ordinal);
        foreach (RuleDeclaration declaration in _baseline)
        {
            effective[declaration.Key] = ToEffective(declaration);
        }

        var matchingGroups = _groups.Where(g => g.Selectors.Any(s => s.Matches(repo))).ToList();
        foreach (IGrouping<string, RuleDeclaration> rule in matchingGroups.SelectMany(g => g.Rules).GroupBy(r => r.Key))
        {
            RuleDeclaration first = rule.First();
            if (rule.Skip(1).FirstOrDefault(other => !other.SameAs(first)) is { } conflicting)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.GroupConflict,
                    $"Groups '{first.Layer.Name}' and '{conflicting.Layer.Name}' both set '{rule.Key}' for {repo}, to different values. " +
                    "Set it in a repo override, or tighten the selectors so only one group matches.",
                    conflicting.Layer.Location));
                continue;
            }

            Apply(effective, first);
        }

        if (_overrides.TryGetValue(repo, out IReadOnlyList<RuleDeclaration>? overrides))
        {
            foreach (RuleDeclaration declaration in overrides)
            {
                Apply(effective, declaration);
            }
        }

        return new EffectivePolicy(repo, effective, [.. matchingGroups.Select(g => g.File.Content.Name!)]);
    }

    private void Apply(Dictionary<string, EffectiveRule> effective, RuleDeclaration declaration)
    {
        // A locked rule keeps the baseline's value. validate reports the attempt as SP0107.
        if (!_locked.Contains(declaration.Key))
        {
            effective[declaration.Key] = ToEffective(declaration);
        }
    }

    private EffectiveRule ToEffective(RuleDeclaration declaration) =>
        new(declaration.Key, declaration.State, declaration.Value, declaration.Layer, _locked.Contains(declaration.Key));
}
