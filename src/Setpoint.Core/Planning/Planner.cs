using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Merging;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Policy.Selection;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Planning;

/// <summary>The options a plan run takes.</summary>
/// <param name="Strict">Every warning becomes an error.</param>
/// <param name="SkipUnreachable">An unreachable target is left out with a warning instead of failing the run.</param>
/// <param name="AllowUnsupportedServer">Servers older than the minimum are accepted; the adapter reads it.</param>
/// <param name="MaxChanges">The most repos apply may change (default 25); 0 means no limit. Plan shows how much of it is used.</param>
public sealed record PlanOptions(bool Strict = false, bool SkipUnreachable = false, bool AllowUnsupportedServer = false, int MaxChanges = PlanOptions.DefaultMaxChanges)
{
    /// <summary>The default <c>--max-changes</c>.</summary>
    public const int DefaultMaxChanges = 25;
}

/// <summary>The policy a plan is made from, with what the plan file records about it.</summary>
/// <param name="Repo">The loaded policy repo, which has passed validation.</param>
/// <param name="Sources">The files it was loaded from, for the policy hash.</param>
/// <param name="Commit">The policy repo commit, when known.</param>
public sealed record PlanInput(PolicyRepo Repo, IReadOnlyList<PolicySourceFile> Sources, string? Commit);

/// <summary>A finished plan run.</summary>
/// <param name="Document">The plan, ready to save or print.</param>
/// <param name="Diagnostics">Every diagnostic, for the run and each repo, after <c>--strict</c>.</param>
/// <param name="RunDiagnostics">The ones that belong to the run rather than one repo, such as an unreachable target.</param>
public sealed record PlanResult(PlanDocument Document, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<Diagnostic> RunDiagnostics)
{
    /// <summary>True when any diagnostic is an error. Such a plan is not saved.</summary>
    public bool HasErrors => Diagnostics.HasErrors();
}

/// <summary>
/// Makes a plan: for each target, the preflight, the repo listing and the scope filter, the live selector checks,
/// and then for each repo its merge, live read, mapping and diff. Targets run one after another; repos within a
/// target run a few at a time, and the HTTP core caps requests per target on top of that.
/// </summary>
public sealed class Planner
{
    /// <summary>How many repos of one target are read at once.</summary>
    public const int RepoConcurrency = 4;

    private readonly IReadOnlyDictionary<Platform, IProviderAdapter> _adapters;
    private readonly IReadOnlyList<IProviderBlockDefinition> _providers;
    private readonly TimeProvider _time;

    public Planner(IEnumerable<IProviderAdapter> adapters, IReadOnlyList<IProviderBlockDefinition> providers, TimeProvider? time = null)
    {
        _adapters = adapters.ToDictionary(a => a.Platform);
        _providers = providers;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Plans every target in the policy.</summary>
    public async Task<PlanResult> PlanAsync(PlanInput input, PlanOptions options, CancellationToken cancellationToken)
    {
        var merger = new PolicyMerger(input.Repo, _providers);
        var runDiagnostics = new List<Diagnostic>();
        var targets = new List<PlanTarget>();
        var repos = new List<PlanRepo>();
        var repoDiagnostics = new List<Diagnostic>();

        foreach ((string name, TargetDefinition definition) in input.Repo.Targets.Content.Targets)
        {
            var target = new Target(name, definition);
            TargetRun? run = await ReachAsync(target, options, runDiagnostics, cancellationToken);
            if (run is null)
            {
                continue;
            }

            CheckSelectors(name, run.Listed, merger, runDiagnostics);
            targets.Add(new PlanTarget(name, definition.Platform, run.Info.Detected, run.Info.ApiVersion, definition.Auth ?? AuthMethod.Pat, run.Info.Identity, run.Listed.Count));

            var planned = new (PlanRepo Repo, List<Diagnostic> Diagnostics)[run.Listed.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, run.Listed.Count),
                new ParallelOptions { MaxDegreeOfParallelism = RepoConcurrency, CancellationToken = cancellationToken },
                async (index, ct) => planned[index] = await PlanRepoAsync(run.Adapter, run.Listed[index], merger, options, ct));

            foreach ((PlanRepo repo, List<Diagnostic> diagnostics) in planned)
            {
                repos.Add(repo);
                repoDiagnostics.AddRange(diagnostics);
            }
        }

        IReadOnlyList<Diagnostic> run1 = DiagnosticList.ApplyStrict(runDiagnostics.Distinct(), options.Strict);
        var all = run1.Concat(repoDiagnostics).ToList();

        var document = new PlanDocument
        {
            FormatVersion = PlanDocument.CurrentFormatVersion,
            SetpointVersion = SetpointVersion.Current,
            CreatedAt = _time.GetUtcNow(),
            Policy = new PlanPolicy(PolicyHash.Compute(input.Sources), input.Sources.Count, input.Commit),
            Options = new PlanOptionsRecord(options.Strict, options.SkipUnreachable, options.AllowUnsupportedServer, options.MaxChanges),
            Targets = targets,
            Repos = repos,
            Diagnostics = [.. run1.Select(PlanDiagnostic.From)],
            Summary = Summarise(repos, all),
        };

        return new PlanResult(document, all, run1);
    }

    private async Task<TargetRun?> ReachAsync(Target target, PlanOptions options, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(target.Definition.Platform, out IProviderAdapter? adapter))
        {
            diagnostics.Add(Unreachable(target, options, "this build of Setpoint has no adapter for its platform yet"));
            return null;
        }

        try
        {
            TargetInfo info = await adapter.DescribeAsync(target, cancellationToken);
            var listed = new List<ListedRepo>();
            await foreach (ListedRepo repo in adapter.ListReposAsync(target, cancellationToken))
            {
                if (InScope(target, repo.Repo))
                {
                    listed.Add(repo);
                }
            }

            listed.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Repo.ToString(), b.Repo.ToString()));
            return new TargetRun(adapter, info, listed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add(Unreachable(target, options, ex.Message));
            return null;
        }
    }

    private static Diagnostic Unreachable(Target target, PlanOptions options, string reason) => options.SkipUnreachable
        ? Diagnostic.Warning(DiagnosticCodes.TargetUnreachable, $"Target '{target.Name}' is unreachable ({reason}); it is left out of the plan because of --skip-unreachable.")
        : Diagnostic.Error(DiagnosticCodes.TargetUnreachable, $"Target '{target.Name}' is unreachable ({reason}). No plan file is written; --skip-unreachable leaves the target out instead.");

    /// <summary>True when the repo is in the target's <c>scope:</c> and not in its <c>exclude:</c>.</summary>
    internal static bool InScope(Target target, RepoRef repo)
    {
        if (!string.Equals(repo.Target, target.Name, StringComparison.Ordinal))
        {
            return false;
        }

        bool Matches(IReadOnlyList<string>? globs) =>
            (globs ?? []).Select(g => RepoGlob.TryParse(g, 2, out _)).OfType<RepoGlob>().Any(g => g.Matches(repo.Owner, repo.Name));

        bool included = target.Definition.Scope is null
            ? target.Definition.Platform == Platform.AzureDevOps
            : Matches(target.Definition.Scope);
        return included && !Matches(target.Definition.Exclude);
    }

    private static void CheckSelectors(string target, IReadOnlyList<ListedRepo> listed, PolicyMerger merger, List<Diagnostic> diagnostics)
    {
        foreach ((PolicyFile<PolicyLayer> file, IReadOnlyList<RepoGlob> selectors) in merger.GroupSelectors)
        {
            foreach (RepoGlob selector in selectors.Where(s => string.Equals(s.Parts[0], target, StringComparison.Ordinal)))
            {
                if (listed.Any(r => selector.Matches(r.Repo)))
                {
                    continue;
                }

                int index = (file.Content.Selectors ?? []).ToList().IndexOf(selector.Pattern);
                SourceLocation at = file.Source.Find(JsonPath.Index("$.selectors", Math.Max(index, 0)));
                diagnostics.Add(selector.IsExact
                    ? Diagnostic.Error(DiagnosticCodes.RepoNotFound, $"Selector '{selector.Pattern}' names a repo that doesn't exist in scope. Check for a typo or a rename.", at)
                    : Diagnostic.Warning(DiagnosticCodes.SelectorMatchesNothing, $"Selector '{selector.Pattern}' matches no repo in scope.", at));
            }
        }

        foreach ((PolicyFile<PolicyLayer> file, RepoRef repo) in merger.RepoOverrideFiles.Where(o => string.Equals(o.Repo.Target, target, StringComparison.Ordinal)))
        {
            if (!listed.Any(r => r.Repo.Equals(repo)))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.RepoNotFound,
                    $"Repo override for '{repo}' names a repo that doesn't exist in scope. Check for a typo or a rename.",
                    file.Source.Find("$.repo")));
            }
        }
    }

    private static async Task<(PlanRepo, List<Diagnostic>)> PlanRepoAsync(
        IProviderAdapter adapter,
        ListedRepo listed,
        PolicyMerger merger,
        PlanOptions options,
        CancellationToken cancellationToken)
    {
        RepoRef repo = listed.Repo;
        var mergeDiagnostics = new List<Diagnostic>();
        EffectivePolicy effective = merger.Merge(repo, mergeDiagnostics);

        PlanRepo Entry(PlanRepoStatus status, RepoDiff? diff, IReadOnlyList<Diagnostic> diagnostics) => new()
        {
            Target = repo.Target,
            Path = $"{repo.Owner}/{repo.Name}",
            Status = status,
            Layers = effective.Layers,
            Objects = diff?.Objects ?? [],
            Changes = diff?.Changes ?? [],
            Unmanaged = diff?.Unmanaged ?? [],
            Diagnostics = [.. diagnostics.Select(PlanDiagnostic.From)],
        };

        (PlanRepo, List<Diagnostic>) Finish(PlanRepoStatus status, RepoDiff? diff, IEnumerable<Diagnostic> found)
        {
            List<Diagnostic> diagnostics = [.. DiagnosticList.ApplyStrict(mergeDiagnostics.Concat(found).Select(d => Prefix(repo, d)), options.Strict)];
            return (Entry(status, diff, diagnostics), diagnostics);
        }

        if (listed.Skipped is { } skipped)
        {
            return Finish(PlanRepoStatus.Skipped, null, [skipped]);
        }

        try
        {
            RepoCapabilities capabilities = await adapter.GetCapabilitiesAsync(repo, cancellationToken);
            if (capabilities.Skipped is { } unsupported)
            {
                return Finish(PlanRepoStatus.Skipped, null, [unsupported]);
            }

            LiveSnapshot live = await adapter.ReadAsync(repo, cancellationToken);
            DesiredState desired = DesiredStateBuilder.Build(effective, adapter);
            RepoDiff diff = RepoDiffer.Diff(desired, live, adapter.MapObjects(desired, live));
            return Finish(diff.Objects.Count > 0 ? PlanRepoStatus.Changes : PlanRepoStatus.InSync, diff, diff.Diagnostics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Finish(PlanRepoStatus.Error, null, [Diagnostic.Error(DiagnosticCodes.RepoReadFailed, $"Reading the live state failed: {ex.Message}")]);
        }
    }

    // Repo diagnostics name their repo, so they read on their own in a pipeline log.
    private static Diagnostic Prefix(RepoRef repo, Diagnostic diagnostic) =>
        diagnostic.Location is not null || diagnostic.Message.StartsWith(repo.ToString(), StringComparison.OrdinalIgnoreCase)
            ? diagnostic
            : diagnostic with { Message = $"{repo}: {diagnostic.Message}" };

    private static PlanSummary Summarise(IReadOnlyList<PlanRepo> repos, IReadOnlyList<Diagnostic> diagnostics) => new(
        ReposChanged: repos.Count(r => r.Status == PlanRepoStatus.Changes),
        Create: repos.Sum(r => r.Changes.Count(c => c.Action == ChangeAction.Create)),
        Update: repos.Sum(r => r.Changes.Count(c => c.Action == ChangeAction.Update)),
        Remove: repos.Sum(r => r.Changes.Count(c => c.Action == ChangeAction.Remove)),
        InSync: repos.Count(r => r.Status == PlanRepoStatus.InSync),
        Skipped: repos.Count(r => r.Status == PlanRepoStatus.Skipped),
        Errors: repos.Count(r => r.Status == PlanRepoStatus.Error),
        Unmanaged: repos.Sum(r => r.UnmanagedCount),
        Warnings: diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning));

    private sealed record TargetRun(IProviderAdapter Adapter, TargetInfo Info, IReadOnlyList<ListedRepo> Listed);
}
