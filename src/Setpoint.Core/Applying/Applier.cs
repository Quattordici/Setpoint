using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Applying;

/// <summary>The options an apply run takes.</summary>
/// <param name="MaxChanges">The most repos apply may change (default 25); 0 means no limit.</param>
/// <param name="FailFast">A failed write stops the whole run, instead of only that repo.</param>
/// <param name="Strict">Every warning, in the plan or the run, is an error, and nothing is written.</param>
/// <param name="SkipUnreachable">An unreachable target's repos are left out with a warning instead of failing the run.</param>
public sealed record ApplyOptions(int MaxChanges = PlanOptions.DefaultMaxChanges, bool FailFast = false, bool Strict = false, bool SkipUnreachable = false);

/// <summary>What happened to one repo.</summary>
public enum RepoApplyStatus
{
    /// <summary>Every planned object was written.</summary>
    Applied,

    /// <summary>A write failed; the repo's later objects were not attempted.</summary>
    Failed,

    /// <summary>Not attempted, because <c>--fail-fast</c> stopped the run.</summary>
    NotAttempted,

    /// <summary>Left out, because its target was unreachable and <c>--skip-unreachable</c> was set.</summary>
    Skipped,
}

/// <summary>One repo's apply result.</summary>
/// <param name="Repo">The repo.</param>
/// <param name="Status">What happened.</param>
/// <param name="Objects">The outcome of each object that was attempted.</param>
/// <param name="Planned">How many objects the plan had for it.</param>
public sealed record RepoApplyResult(RepoRef Repo, RepoApplyStatus Status, IReadOnlyList<ObjectApplyOutcome> Objects, int Planned)
{
    /// <summary>How many objects were written.</summary>
    public int Written => Objects.Count(o => o.Succeeded);
}

/// <summary>A finished apply run.</summary>
/// <param name="Repos">A result per repo the plan changes, in plan order. Empty when a guard refused the run.</param>
/// <param name="Diagnostics">Everything to report, after <c>--strict</c>.</param>
/// <param name="Refusal">Why nothing was written, when a check before the first write refused the run.</param>
public sealed record ApplyResultSet(IReadOnlyList<RepoApplyResult> Repos, IReadOnlyList<Diagnostic> Diagnostics, string? Refusal = null)
{
    /// <summary>The exit code: 3 stale, 4 a guard, 2 an invalid policy, 1 any other error, 0 success.</summary>
    public ExitCode ExitCode =>
        Diagnostics.Any(d => d.Code == DiagnosticCodes.StalePlan) ? ExitCode.StalePlan
        : Diagnostics.Any(d => d.Code == DiagnosticCodes.TooManyChanges) ? ExitCode.GuardTripped
        : Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Code[2] == '0') ? ExitCode.InvalidPolicyOrUsage
        : Diagnostics.HasErrors() ? ExitCode.RuntimeFailure
        : ExitCode.Success;
}

/// <summary>
/// Applies a plan. Before the first write it checks, in order: the plan's Setpoint version and policy hash, the
/// change limit, <c>--strict</c>, that every target is reachable, and that every planned object is still exactly as
/// planned. Any failure there refuses the whole run and nothing is written. Then each repo's objects are written as
/// the plan holds them; a failed write stops that repo, and with <c>--fail-fast</c> the run.
/// </summary>
public sealed class Applier
{
    private readonly IReadOnlyDictionary<Platform, IProviderAdapter> _adapters;

    public Applier(IEnumerable<IProviderAdapter> adapters) => _adapters = adapters.ToDictionary(a => a.Platform);

    /// <summary>Applies <paramref name="plan"/>.</summary>
    /// <param name="plan">The plan, from a file or just made.</param>
    /// <param name="policy">The policy as it is now, whose targets are reached and whose hash must match the plan's.</param>
    /// <param name="sources">The policy's files, for the hash.</param>
    /// <param name="options">The guards.</param>
    /// <param name="cancellationToken">Stops the run between requests.</param>
    public async Task<ApplyResultSet> ApplyAsync(PlanDocument plan, PolicyRepo policy, IReadOnlyList<Policy.Loading.PolicySourceFile> sources, ApplyOptions options, CancellationToken cancellationToken)
    {
        if (Stale(plan, sources) is { } stale)
        {
            return Refused(stale);
        }

        PlanRepo[] changed = [.. plan.Repos.Where(r => r.Objects.Count > 0)];
        if (options.MaxChanges > 0 && changed.Length > options.MaxChanges)
        {
            return Refused(Diagnostic.Error(
                DiagnosticCodes.TooManyChanges,
                $"The plan changes {changed.Length} repos, more than --max-changes allows ({options.MaxChanges}). " +
                "Raise the limit with --max-changes N, or remove it with --max-changes 0."));
        }

        if (options.Strict)
        {
            Diagnostic[] warnings = [.. plan.Diagnostics.Concat(plan.Repos.SelectMany(r => r.Diagnostics))
                .Where(d => d.Severity == DiagnosticSeverity.Warning)
                .Select(d => Diagnostic.Error(d.Code, d.Message))];
            if (warnings.Length > 0)
            {
                return new ApplyResultSet([], warnings, "--strict makes the plan's warnings errors, so nothing was written. Fix them and plan again.");
            }
        }

        var diagnostics = new List<Diagnostic>();
        var reached = new Dictionary<string, IProviderAdapter>(StringComparer.Ordinal);
        var skippedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (string targetName in changed.Select(r => r.Target).Distinct(StringComparer.Ordinal))
        {
            IProviderAdapter? adapter = await ReachAsync(policy, targetName, options, diagnostics, cancellationToken).ConfigureAwait(false);
            if (adapter is null)
            {
                skippedTargets.Add(targetName);
            }
            else
            {
                reached[targetName] = adapter;
            }
        }

        IReadOnlyList<Diagnostic> reachDiagnostics = DiagnosticList.ApplyStrict(diagnostics, options.Strict);
        if (reachDiagnostics.HasErrors())
        {
            return new ApplyResultSet([], reachDiagnostics, "A target couldn't be reached, so nothing was written.");
        }

        var drifted = new List<string>();
        foreach (PlanRepo repo in changed.Where(r => reached.ContainsKey(r.Target)))
        {
            RepoRef reference = Ref(repo);
            try
            {
                LiveSnapshot live = await reached[repo.Target].ReadAsync(reference, cancellationToken).ConfigureAwait(false);
                drifted.AddRange(Drift(reference, repo, live));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new ApplyResultSet(
                    [],
                    [.. reachDiagnostics, Diagnostic.Error(DiagnosticCodes.RepoReadFailed, $"{reference}: re-reading the live state to check the plan failed: {ex.Message}")],
                    "The plan couldn't be checked against live state, so nothing was written.");
            }
        }

        if (drifted.Count > 0)
        {
            return Refused(Diagnostic.Error(
                DiagnosticCodes.StalePlan,
                $"The plan is stale: live state changed since it was made. Plan again. {string.Join(" ", drifted)}"));
        }

        var results = new List<RepoApplyResult>();
        bool stopped = false;
        foreach (PlanRepo repo in changed)
        {
            RepoRef reference = Ref(repo);
            if (skippedTargets.Contains(repo.Target))
            {
                results.Add(new RepoApplyResult(reference, RepoApplyStatus.Skipped, [], repo.Objects.Count));
                continue;
            }

            if (stopped)
            {
                results.Add(new RepoApplyResult(reference, RepoApplyStatus.NotAttempted, [], repo.Objects.Count));
                continue;
            }

            ApplyResult written;
            try
            {
                written = await reached[repo.Target].ApplyAsync(reference, new RepoChangeSet(repo.Objects), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                written = new ApplyResult([new ObjectApplyOutcome(repo.Objects[0].Key, false, ex.Message)]);
            }

            bool failed = !written.Succeeded || written.Objects.Count < repo.Objects.Count;
            results.Add(new RepoApplyResult(reference, failed ? RepoApplyStatus.Failed : RepoApplyStatus.Applied, written.Objects, repo.Objects.Count));
            if (failed)
            {
                ObjectApplyOutcome? first = written.Objects.FirstOrDefault(o => !o.Succeeded);
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.WriteFailed,
                    $"{reference}: writing {first?.Key ?? "its changes"} failed: {first?.Error ?? "the adapter wrote fewer objects than planned"}"));
                stopped = options.FailFast;
            }
        }

        return new ApplyResultSet(results, DiagnosticList.ApplyStrict(diagnostics, options.Strict));
    }

    // The plan is stale when another Setpoint version or another policy made it; nothing is contacted then.
    private static Diagnostic? Stale(PlanDocument plan, IReadOnlyList<Policy.Loading.PolicySourceFile> sources)
    {
        if (plan.FormatVersion != PlanDocument.CurrentFormatVersion || !string.Equals(plan.SetpointVersion, SetpointVersion.Current, StringComparison.Ordinal))
        {
            return Diagnostic.Error(
                DiagnosticCodes.StalePlan,
                $"The plan was made by Setpoint {plan.SetpointVersion} and this is {SetpointVersion.Current}, so it can't be applied. Plan again with this version.");
        }

        string hash = PolicyHash.Compute(sources);
        return string.Equals(plan.Policy.Hash, hash, StringComparison.Ordinal)
            ? null
            : Diagnostic.Error(DiagnosticCodes.StalePlan, "The policy files changed since the plan was made. Plan again.");
    }

    // A planned object drifted when it changed since the plan (its fingerprint differs), it is gone, or, for a
    // create, something now matches what it would create.
    private static IEnumerable<string> Drift(RepoRef repo, PlanRepo planned, LiveSnapshot live)
    {
        foreach (PlanObject wanted in planned.Objects)
        {
            if (wanted.Operation == ObjectOperation.Create)
            {
                if (live.Objects.FirstOrDefault(o => o.MatchKey == wanted.MatchKey) is { } appeared)
                {
                    yield return $"{repo}: {appeared.Key} now exists where the plan creates {wanted.Key}.";
                }
                else if (live.Ambiguous.FirstOrDefault(a => a.MatchKey == wanted.MatchKey) is { } several)
                {
                    yield return $"{repo}: {string.Join(", ", several.ObjectKeys)} now exist where the plan creates {wanted.Key}.";
                }

                continue;
            }

            LiveObject? current = live.Objects.FirstOrDefault(o => o.Key == wanted.Key);
            if (current is null)
            {
                yield return $"{repo}: {wanted.Key} no longer exists.";
            }
            else if (!string.Equals(Fingerprint.Of(current.Native), wanted.Fingerprint, StringComparison.Ordinal))
            {
                yield return $"{repo}: {wanted.Key} changed since the plan.";
            }
        }
    }

    private async Task<IProviderAdapter?> ReachAsync(PolicyRepo policy, string targetName, ApplyOptions options, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (!policy.Targets.Content.Targets.TryGetValue(targetName, out TargetDefinition? definition) || !_adapters.TryGetValue(definition.Platform, out IProviderAdapter? adapter))
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.TargetUnreachable, $"Target '{targetName}' from the plan isn't one this build can reach."));
            return null;
        }

        var target = new Target(targetName, definition);
        try
        {
            await adapter.DescribeAsync(target, cancellationToken).ConfigureAwait(false);

            // Listing again tells the adapter the repos' ids, which some platforms write by.
            await foreach (ListedRepo _ in adapter.ListReposAsync(target, cancellationToken).ConfigureAwait(false))
            {
            }

            return adapter;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add(options.SkipUnreachable
                ? Diagnostic.Warning(DiagnosticCodes.TargetUnreachable, $"Target '{targetName}' is unreachable ({ex.Message}); its repos are left out because of --skip-unreachable.")
                : Diagnostic.Error(DiagnosticCodes.TargetUnreachable, $"Target '{targetName}' is unreachable ({ex.Message}). --skip-unreachable leaves its repos out instead."));
            return null;
        }
    }

    private static RepoRef Ref(PlanRepo repo)
    {
        int slash = repo.Path.IndexOf('/', StringComparison.Ordinal);
        return new RepoRef(repo.Target, repo.Path[..slash], repo.Path[(slash + 1)..]);
    }

    private static ApplyResultSet Refused(Diagnostic diagnostic) => new([], [diagnostic], "Nothing was written.");
}
