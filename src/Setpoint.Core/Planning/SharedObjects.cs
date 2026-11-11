using Setpoint.Core.Diagnostics;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Planning;

/// <summary>
/// Reconciles objects several repos share, such as an Azure DevOps environment, across one target's planned repos.
/// Repos that declare a shared object differently fail with SP0122, naming each other, because neither value can be
/// written. When they agree, the object and its changes stay in the first repo by path, so apply writes it once.
/// </summary>
internal static class SharedObjects
{
    /// <summary>One repo's plan, its diagnostics, and the shared objects it wants.</summary>
    public sealed record Planned(PlanRepo Repo, List<Diagnostic> Diagnostics, IReadOnlyList<DesiredObject> Shared);

    /// <summary>The repos with shared objects reconciled, in the order given.</summary>
    public static List<Planned> Reconcile(IReadOnlyList<Planned> planned)
    {
        var result = planned.ToList();
        var groups = planned
            .Select((p, index) => (p, index))
            .SelectMany(x => x.p.Shared.Select(d => (Object: d, x.index)))
            .GroupBy(x => (x.Object.SharedBy!, x.Object.MatchKey))
            .Where(g => g.Select(x => x.index).Distinct().Count() > 1);

        foreach (var group in groups)
        {
            var wants = group
                .OrderBy(x => planned[x.index].Repo.Path, StringComparer.Ordinal)
                .Select(x => (x.index, Value: Value(x.Object)))
                .ToList();
            string title = group.First().Object.Title?.Replace(" (new)", string.Empty, StringComparison.Ordinal) ?? group.Key.MatchKey;

            if (wants.Select(w => w.Value).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                string all = string.Join("; ", wants.Select(w => $"{planned[w.index].Repo.Path}: {w.Value}"));
                foreach ((int index, _) in wants)
                {
                    string others = string.Join(", ", wants.Where(w => w.index != index).Select(w => planned[w.index].Repo.Path));
                    var error = Diagnostic.Error(
                        DiagnosticCodes.SharedObjectConflict,
                        $"{planned[index].Repo.Target}/{planned[index].Repo.Path}: {title} belongs to {group.Key.Item1}, and {others} declare it differently ({all}). " +
                        "Declare it the same way for every repo there.");
                    result[index] = Fail(result[index], error);
                }

                continue;
            }

            // They agree: the first repo by path writes it, and the others leave it out.
            foreach ((int index, _) in wants.Skip(1))
            {
                result[index] = Without(result[index], group.Key.MatchKey);
            }
        }

        return result;
    }

    private static string Value(DesiredObject wanted) =>
        string.Join(", ", wanted.Rules.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key} {CanonicalJson.ToText(CanonicalJson.ForRule(r.Value))}"));

    private static Planned Fail(Planned planned, Diagnostic error)
    {
        List<Diagnostic> diagnostics = [.. planned.Diagnostics, error];
        return planned with
        {
            Repo = planned.Repo with
            {
                Status = PlanRepoStatus.Error,
                Objects = [],
                Changes = [],
                Diagnostics = [.. diagnostics.Select(PlanDiagnostic.From)],
            },
            Diagnostics = diagnostics,
        };
    }

    private static Planned Without(Planned planned, string matchKey)
    {
        PlanObject[] removed = [.. planned.Repo.Objects.Where(o => o.MatchKey == matchKey)];
        if (removed.Length == 0)
        {
            return planned;
        }

        var keys = removed.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        PlanObject[] objects = [.. planned.Repo.Objects.Where(o => o.MatchKey != matchKey)];
        return planned with
        {
            Repo = planned.Repo with
            {
                Objects = objects,
                Changes = [.. planned.Repo.Changes.Where(c => !keys.Contains(c.Object))],
                Status = planned.Repo.Status == PlanRepoStatus.Changes && objects.Length == 0 ? PlanRepoStatus.InSync : planned.Repo.Status,
            },
        };
    }
}
