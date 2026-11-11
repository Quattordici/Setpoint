using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Merging;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Policy.Selection;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Policy.Validation;

/// <summary>
/// The offline checks <c>validate</c> runs on a loaded policy repo: everything that doesn't need a live platform.
/// Checks that need live repos (a glob that matches nothing, a repo that doesn't exist) belong to <c>plan</c>.
/// </summary>
public sealed class PolicyValidator(IReadOnlyList<IProviderBlockDefinition> providers)
{
    /// <summary>The most reviewers a GitHub ruleset can require.</summary>
    public const int GitHubMaxReviewers = 10;

    /// <summary>The most required reviewers, teams and users together, a GitHub environment allows.</summary>
    public const int GitHubMaxEnvironmentReviewers = 6;

    /// <summary>The longest wait an environment allows: 30 days.</summary>
    public const int MaxWaitMinutes = 43200;

    /// <summary>Runs every offline check.</summary>
    public IReadOnlyList<Diagnostic> Validate(PolicyRepo policy)
    {
        var diagnostics = new List<Diagnostic>();
        IReadOnlyDictionary<string, TargetDefinition> targets = policy.Targets.Content.Targets;

        CheckTargets(policy.Targets, diagnostics);
        CheckLocked(policy.Baseline, diagnostics);

        IEnumerable<PolicyFile<PolicyLayer>> lowerLayers = policy.Groups.Concat(policy.RepoOverrides);
        foreach (PolicyFile<PolicyLayer> layer in lowerLayers)
        {
            CheckNotOverridingLocked(layer, policy.Baseline, diagnostics);
        }

        foreach (PolicyFile<PolicyLayer> group in policy.Groups)
        {
            CheckSelectors(group, targets, diagnostics);
        }

        foreach (PolicyFile<PolicyLayer> repoOverride in policy.RepoOverrides)
        {
            CheckRepoOverrideTarget(repoOverride, targets, diagnostics);
        }

        foreach (PolicyFile<PolicyLayer> layer in lowerLayers.Prepend(policy.Baseline))
        {
            CheckRuleValues(layer, ReachableTargets(layer, targets), diagnostics);
        }

        // Group conflicts need a repo; offline, the repos we know of are the ones named explicitly.
        var merger = new PolicyMerger(policy, providers);
        foreach (RepoRef repo in merger.RepoOverrideRepos.Concat(merger.ExactlySelectedRepos).Distinct())
        {
            merger.Merge(repo, diagnostics);
        }

        return DiagnosticList.InSourceOrder(diagnostics.Distinct());
    }

    private static void CheckTargets(PolicyFile<TargetsFile> file, List<Diagnostic> diagnostics)
    {
        foreach ((string name, TargetDefinition target) in file.Content.Targets)
        {
            string path = JsonPath.Property("$.targets", name);
            SourceLocation at = file.Source.Find(path);

            if (target.Platform == Platform.GitHub && target.Scope is null)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.IncompleteTarget,
                    $"GitHub target '{name}' needs 'scope:', such as 'scope: [my-org/*]'. GitHub has no collection to default to.",
                    at));
            }

            if (target.Platform == Platform.AzureDevOps && target.Url is null)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.IncompleteTarget,
                    $"Azure DevOps target '{name}' needs 'url:', the organization or collection URL.",
                    at));
            }

            CheckAuth(name, target, file.Source, path, diagnostics);

            foreach ((string key, IReadOnlyList<string>? globs) in new[] { ("scope", target.Scope), ("exclude", target.Exclude) })
            {
                for (int i = 0; i < (globs?.Count ?? 0); i++)
                {
                    if (RepoGlob.TryParse(globs![i], 2, out string? error) is null)
                    {
                        diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidGlob, error!, file.Source.Find(JsonPath.Index($"{path}.{key}", i))));
                    }
                }
            }
        }
    }

    private static void CheckAuth(string name, TargetDefinition target, SourceMap source, string path, List<Diagnostic> diagnostics)
    {
        AuthMethod auth = target.Auth ?? AuthMethod.Pat;
        SourceLocation at = source.Find($"{path}.auth");
        string? problem = (target.Platform, auth) switch
        {
            (Platform.GitHub, AuthMethod.AccessToken or AuthMethod.Integrated or AuthMethod.Entra) =>
                $"auth: {Name(auth)} is for Azure DevOps. GitHub targets use pat or app.",
            (Platform.AzureDevOps, AuthMethod.App) =>
                "auth: app is for GitHub. Azure DevOps targets use pat, accessToken, integrated or entra.",
            (Platform.AzureDevOps, AuthMethod.Entra) when target.Url is { } url && !IsAzureDevOpsServices(url) =>
                $"auth: entra works on Azure DevOps Services only, and '{url}' is an Azure DevOps Server. Use pat, accessToken or integrated.",
            (Platform.AzureDevOps, AuthMethod.Integrated) when target.Url is { } url && IsAzureDevOpsServices(url) =>
                $"auth: integrated (Windows auth) works on Azure DevOps Server only, and '{url}' is Azure DevOps Services. Use pat, accessToken or entra.",
            _ => null,
        };

        if (problem is not null)
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.UnsupportedAuth, $"Target '{name}': {problem}", at));
        }

        if (auth == AuthMethod.App && target.Platform == Platform.GitHub && target.AppId is null)
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.IncompleteTarget, $"Target '{name}' uses auth: app, so it needs 'appId:'.", at));
        }

        if (target.AppId is not null && auth != AuthMethod.App)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.IncompleteTarget,
                $"Target '{name}' sets 'appId:' but not 'auth: app', so the App would never be used.",
                source.Find($"{path}.appId")));
        }
    }

    private static void CheckLocked(PolicyFile<PolicyLayer> baseline, List<Diagnostic> diagnostics)
    {
        IReadOnlyList<string> locked = baseline.Content.Locked ?? [];
        for (int i = 0; i < locked.Count; i++)
        {
            if (!NeutralRules.Names.Contains(locked[i]))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.UnknownLockedRule,
                    $"'{locked[i]}' is not a rule. locked: lists neutral rules: {string.Join(", ", NeutralRules.Names)}.",
                    baseline.Source.Find(JsonPath.Index("$.locked", i))));
            }
        }
    }

    private void CheckNotOverridingLocked(PolicyFile<PolicyLayer> layer, PolicyFile<PolicyLayer> baseline, List<Diagnostic> diagnostics)
    {
        IReadOnlyList<string> locked = baseline.Content.Locked ?? [];
        foreach (RuleDeclaration declaration in RuleDeclaration.From(layer, providers))
        {
            if (locked.Contains(declaration.Key))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.LockedRuleOverridden,
                    $"'{declaration.Key}' is locked by {baseline.Path} and cannot be overridden.",
                    declaration.Layer.Location));
            }
        }
    }

    private static void CheckSelectors(PolicyFile<PolicyLayer> group, IReadOnlyDictionary<string, TargetDefinition> targets, List<Diagnostic> diagnostics)
    {
        IReadOnlyList<string> selectors = group.Content.Selectors ?? [];
        for (int i = 0; i < selectors.Count; i++)
        {
            SourceLocation at = group.Source.Find(JsonPath.Index("$.selectors", i));
            RepoGlob? glob = RepoGlob.TryParse(selectors[i], 3, out string? error);
            if (glob is null)
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidGlob, error!, at));
                continue;
            }

            string target = glob.Parts[0];
            if (target.IndexOfAny(['*', '?']) >= 0)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidGlob,
                    $"'{selectors[i]}' must start with a target name, not a wildcard: selectors are scoped to one target.",
                    at));
            }
            else if (!targets.TryGetValue(target, out TargetDefinition? definition))
            {
                diagnostics.Add(UnknownTarget(target, targets, at));
            }
            else if (glob.IsExact && !InScope(definition, glob.Parts[1], glob.Parts[2]))
            {
                diagnostics.Add(Diagnostic.Warning(
                    DiagnosticCodes.OutsideScope,
                    $"'{selectors[i]}' is outside the scope of target '{target}', so this selector never applies.",
                    at));
            }
        }
    }

    private static void CheckRepoOverrideTarget(PolicyFile<PolicyLayer> file, IReadOnlyDictionary<string, TargetDefinition> targets, List<Diagnostic> diagnostics)
    {
        if (file.Content.Repo is not { } name || RepoRef.TryParse(name) is not { } repo)
        {
            return;
        }

        SourceLocation at = file.Source.Find("$.repo");
        if (!targets.TryGetValue(repo.Target, out TargetDefinition? target))
        {
            diagnostics.Add(UnknownTarget(repo.Target, targets, at));
        }
        else if (!InScope(target, repo.Owner, repo.Name))
        {
            diagnostics.Add(Diagnostic.Warning(
                DiagnosticCodes.OutsideScope,
                $"{repo} is outside the scope of target '{repo.Target}', so this override never applies.",
                at));
        }
    }

    private static void CheckRuleValues(PolicyFile<PolicyLayer> layer, IReadOnlyList<TargetDefinition> reachable, List<Diagnostic> diagnostics)
    {
        NeutralRules rules = layer.Content.Rules;
        SourceMap source = layer.Source;

        if (rules.AllowedMergeTypes is { HasValue: true, Value.Count: 0 })
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.NoMergeTypes,
                "allowedMergeTypes is empty, which would block every pull request. List at least one of merge, squash, rebase, or use ~ to leave it unmanaged.",
                source.Find("$.rules.allowedMergeTypes")));
        }

        if (rules.MinimumReviewers is { HasValue: true, Value: { Enabled: true } reviewers })
        {
            SourceLocation at = source.Find("$.rules.minimumReviewers");
            if (reviewers.Count is null or < 1)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidReviewerCount,
                    "minimumReviewers needs 'count:' of at least 1, or 'enabled: false' to remove the setting.",
                    at));
            }
            else if (reviewers.Count > GitHubMaxReviewers && reachable.Any(t => t.Platform == Platform.GitHub))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.TooManyReviewersForGitHub,
                    $"minimumReviewers count {reviewers.Count} applies to GitHub repos here, and GitHub rulesets allow at most {GitHubMaxReviewers}.",
                    at));
            }
        }

        if (rules.Environments is { HasValue: true, Value: { } environments })
        {
            foreach ((string name, EnvironmentRule environment) in environments.Where(e => e.Value.Enabled))
            {
                SourceLocation at = source.Find(JsonPath.Property("$.rules.environments", name));
                if (environment.WaitMinutes is < 0 or > MaxWaitMinutes)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.InvalidEnvironment,
                        $"environments: '{name}' waits {environment.WaitMinutes} minutes; waitMinutes must be from 0 to {MaxWaitMinutes} (30 days).",
                        at));
                }

                if (environment.Reviewers is { Count: > GitHubMaxEnvironmentReviewers } many && reachable.Any(t => t.Platform == Platform.GitHub))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.InvalidEnvironment,
                        $"environments: '{name}' names {many.Count} reviewers and applies to GitHub repos here, where an environment allows at most {GitHubMaxEnvironmentReviewers}.",
                        at));
                }
            }
        }

        foreach ((string name, Declared<BooleanRule> rule) in new[]
        {
            ("requireCommentResolution", rules.RequireCommentResolution),
            ("protectFromForcePushAndDelete", rules.ProtectFromForcePushAndDelete),
        })
        {
            if (rule is { HasValue: true, Value.WrittenAsObject: true })
            {
                diagnostics.Add(Diagnostic.Warning(
                    DiagnosticCodes.RedundantEnabledForm,
                    $"'{name}' is a boolean rule, so write '{name}: {(rule.Value.Enabled ? "true" : "false")}' instead of '{{ enabled: ... }}'.",
                    source.Find(JsonPath.Property("$.rules", name))));
            }
        }
    }

    /// <summary>The targets a layer can apply to: all of them for the baseline, the selectors' targets for a group.</summary>
    private static IReadOnlyList<TargetDefinition> ReachableTargets(PolicyFile<PolicyLayer> layer, IReadOnlyDictionary<string, TargetDefinition> targets)
    {
        IEnumerable<string> names = layer.Content.Layer switch
        {
            LayerKind.Baseline => targets.Keys,
            LayerKind.Group => (layer.Content.Selectors ?? []).Select(s => s.Split('/')[0]),
            _ => [layer.Content.Repo?.Split('/')[0] ?? string.Empty],
        };

        return [.. names.Distinct().Select(n => targets.GetValueOrDefault(n)).OfType<TargetDefinition>()];
    }

    private static bool InScope(TargetDefinition target, string owner, string repo)
    {
        bool included = target.Scope is null
            || target.Scope.Select(s => RepoGlob.TryParse(s, 2, out _)).OfType<RepoGlob>().Any(g => g.Matches(owner, repo));
        bool excluded = (target.Exclude ?? []).Select(s => RepoGlob.TryParse(s, 2, out _)).OfType<RepoGlob>().Any(g => g.Matches(owner, repo));
        return included && !excluded;
    }

    private static Diagnostic UnknownTarget(string target, IReadOnlyDictionary<string, TargetDefinition> targets, SourceLocation at) =>
        Diagnostic.Error(
            DiagnosticCodes.UnknownTarget,
            $"There is no target named '{target}' in targets.yml. Known targets: {string.Join(", ", targets.Keys.Order(StringComparer.Ordinal))}.",
            at);

    /// <summary>True for Azure DevOps Services URLs; anything else is treated as Azure DevOps Server.</summary>
    internal static bool IsAzureDevOpsServices(Uri url) =>
        url.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    private static string Name(AuthMethod auth) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(auth.ToString());
}
