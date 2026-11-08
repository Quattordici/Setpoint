using System.Text.Json.Nodes;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Planning;

/// <summary>What the diff found for one repo.</summary>
/// <param name="Objects">The objects apply writes.</param>
/// <param name="Changes">The neutral view, pointing at the objects.</param>
/// <param name="Unmanaged">The live settings left alone.</param>
/// <param name="Diagnostics">Ambiguous matches, unknown values and stricter unmanaged settings.</param>
public sealed record RepoDiff(
    IReadOnlyList<PlanObject> Objects,
    IReadOnlyList<PlanChange> Changes,
    IReadOnlyList<PlanUnmanaged> Unmanaged,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Compares one repo's desired state with its live state, on the normalised model. Only managed rules are diffed.
/// Each desired object is paired with the live object that has its match key: none means create, a desired object
/// without a payload means delete, and any rule that differs means update. Objects whose rules all match are dropped.
/// </summary>
public static class RepoDiffer
{
    /// <summary>Diffs one repo.</summary>
    public static RepoDiff Diff(DesiredState desired, LiveSnapshot live, IReadOnlyList<DesiredObject> desiredObjects)
    {
        var diagnostics = new List<Diagnostic>();
        var leftAlone = new HashSet<string>(StringComparer.Ordinal);

        foreach (AmbiguousMatch ambiguous in live.Ambiguous.Where(a => desired.Rules.ContainsKey(a.Rule)))
        {
            leftAlone.Add(ambiguous.Rule);
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCodes.AmbiguousMatch,
                $"{ambiguous.Rule}: {ambiguous.ObjectKeys.Count} live objects match {ambiguous.Detail} ({string.Join(", ", ambiguous.ObjectKeys)}); left alone."));
        }

        foreach (string unknown in live.UnknownRules.Where(desired.Rules.ContainsKey))
        {
            leftAlone.Add(unknown);
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCodes.LiveValueUnknown,
                $"{unknown}: the token can't see the live value, so it is reported as {ValueText.Unknown} and not changed. Grant the token read access to see it."));
        }

        foreach (RuleWarning warning in live.Warnings ?? [])
        {
            if (desired.Rules.TryGetValue(warning.Rule, out DesiredRule? declared) && !declared.MustBeAbsent)
            {
                diagnostics.Add(warning.Diagnostic);
            }
        }

        Dictionary<string, LiveObject> liveByMatch = live.Objects
            .GroupBy(o => o.MatchKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var objects = new List<PlanObject>();
        var changes = new List<PlanChange>();
        foreach (DesiredObject wanted in desiredObjects)
        {
            LiveObject? current = liveByMatch.GetValueOrDefault(wanted.MatchKey);
            string key = current?.Key ?? wanted.CreateKey;

            var objectChanges = new List<PlanChange>();
            foreach ((string rule, JsonNode? after) in wanted.Rules)
            {
                if (!desired.Rules.TryGetValue(rule, out DesiredRule? declared) || leftAlone.Contains(rule))
                {
                    continue;
                }

                JsonNode? before = current?.Rules.GetValueOrDefault(rule);
                objectChanges.AddRange(Compare(rule, before, after, declared, key));
            }

            ObjectOperation? operation = (current, wanted.Payload) switch
            {
                (null, null) => null,
                (null, _) when objectChanges.Count > 0 => ObjectOperation.Create,
                (not null, null) when objectChanges.Count > 0 => ObjectOperation.Delete,
                (not null, not null) when objectChanges.Count > 0 => ObjectOperation.Update,
                _ => null,
            };

            if (operation is not { } op)
            {
                continue;
            }

            string fingerprint = current is null ? Fingerprint.NothingMatches(wanted.MatchKey) : Fingerprint.Of(current.Native);
            objects.Add(new PlanObject(
                key,
                op,
                fingerprint,
                wanted.MatchKey,
                op == ObjectOperation.Delete ? null : wanted.Payload?.DeepClone(),
                current?.Title ?? wanted.Title));
            changes.AddRange(objectChanges);
        }

        IReadOnlyList<PlanUnmanaged> unmanaged = Unmanaged(desired, live, diagnostics);
        return new RepoDiff(objects, changes, unmanaged, diagnostics);
    }

    private static IEnumerable<PlanChange> Compare(string rule, JsonNode? before, JsonNode? after, DesiredRule declared, string key)
    {
        if (CanonicalJson.RuleEquals(before, after))
        {
            yield break;
        }

        string layer = declared.Layer.Label;
        if (before is null)
        {
            yield return new PlanChange(rule, ChangeAction.Create, ValueText.None, ValueText.Format(after), layer, declared.Locked, key);
        }
        else if (after is null)
        {
            yield return new PlanChange(rule, ChangeAction.Remove, ValueText.Format(before), ValueText.Removed, layer, declared.Locked, key);
        }
        else if (before is JsonObject from && after is JsonObject to)
        {
            // An object rule changes field by field, so the plan says which field moved.
            foreach (string field in from.Select(p => p.Key).Union(to.Select(p => p.Key)).Order(StringComparer.Ordinal))
            {
                JsonNode? fieldBefore = from[field];
                JsonNode? fieldAfter = to[field];
                if (!CanonicalJson.RuleEquals(fieldBefore, fieldAfter))
                {
                    yield return new PlanChange(
                        $"{rule}.{field}",
                        ChangeAction.Update,
                        ValueText.Format(fieldBefore),
                        ValueText.Format(fieldAfter, ValueText.Removed),
                        layer,
                        declared.Locked,
                        key);
                }
            }
        }
        else
        {
            yield return new PlanChange(rule, ChangeAction.Update, ValueText.Format(before), ValueText.Format(after), layer, declared.Locked, key);
        }
    }

    private static List<PlanUnmanaged> Unmanaged(DesiredState desired, LiveSnapshot live, List<Diagnostic> diagnostics)
    {
        var unmanaged = new List<PlanUnmanaged>();

        // Rule values on live objects that the policy leaves unmanaged.
        foreach (LiveObject liveObject in live.Objects)
        {
            foreach ((string rule, JsonNode? value) in liveObject.Rules)
            {
                if (!desired.Rules.ContainsKey(rule) && value is not null)
                {
                    unmanaged.Add(new PlanUnmanaged(rule, liveObject.Key, ValueText.Format(value)));
                }
            }
        }

        // Settings outside Setpoint's ownership, and whether they make the effective result stricter than declared.
        foreach (UnmanagedSetting setting in live.Unmanaged)
        {
            unmanaged.Add(new PlanUnmanaged(setting.Rule, setting.Source, ValueText.Format(setting.Value)));
            if (desired.Rules.TryGetValue(setting.Rule, out DesiredRule? declared) && Strictness.IsStricter(setting.Rule, setting.Value, declared.Value))
            {
                diagnostics.Add(Diagnostic.Warning(
                    DiagnosticCodes.UnmanagedStricter,
                    $"{setting.Rule}: unmanaged {setting.Source} sets {ValueText.Format(setting.Value)}, stricter than the policy's " +
                    $"{ValueText.Format(declared.Value, ValueText.Removed)}; the stricter setting is what takes effect."));
            }
        }

        CheckMergeMethodsLeft(desired, live, diagnostics);
        return unmanaged;
    }

    // Unmanaged merge restrictions, such as GitHub's repo-level merge flags or another ruleset, add to the policy's.
    // When together they allow no merge method at all, no pull request can complete, so that is an error.
    private static void CheckMergeMethodsLeft(DesiredState desired, LiveSnapshot live, List<Diagnostic> diagnostics)
    {
        const string rule = "allowedMergeTypes";
        if (!desired.Rules.TryGetValue(rule, out DesiredRule? declared) || declared.Value is not JsonArray declaredTypes)
        {
            return;
        }

        List<UnmanagedSetting> restrictions = [.. live.Unmanaged.Where(s => s.Rule == rule && s.Value is JsonArray)];
        if (restrictions.Count == 0)
        {
            return;
        }

        var left = new HashSet<string>(MergeTypes(declaredTypes), StringComparer.Ordinal);
        foreach (UnmanagedSetting restriction in restrictions)
        {
            left.IntersectWith(MergeTypes(restriction.Value));
        }

        if (left.Count == 0)
        {
            string others = string.Join("; ", restrictions.Select(r => $"{r.Source} allows {ValueText.Format(r.Value)}"));
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.NoMergeMethodLeft,
                $"{rule}: the policy allows {ValueText.Format(declaredTypes)}, but {others}. Together they leave no merge method, " +
                "so pull requests couldn't complete. Change the policy or the unmanaged setting so at least one method is in both."));
        }
    }

    private static IEnumerable<string> MergeTypes(JsonNode? node) =>
        node is JsonArray array ? array.Select(e => e?.GetValue<string>()).OfType<string>() : [];
}
