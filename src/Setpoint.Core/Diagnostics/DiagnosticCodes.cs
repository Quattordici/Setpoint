namespace Setpoint.Core.Diagnostics;

/// <summary>
/// The stable diagnostic codes. A code never changes meaning once released; retired codes are not reused.
/// Every code is listed with its default severity in <see cref="DiagnosticCatalogue"/> and in docs/diagnostics.md.
/// </summary>
/// <remarks>
/// Ranges: 01xx policy files (found offline by <c>validate</c>), 02xx selection (scope and selectors against
/// live repos), 10xx reaching targets and reading repos, 21xx ownership of managed settings, 22xx matching live objects, 30xx platform capabilities.
/// </remarks>
public static class DiagnosticCodes
{
    // 01xx: policy files.

    /// <summary>The file is not valid YAML, or uses YAML Setpoint does not accept (several documents, complex keys).</summary>
    public const string InvalidYaml = "SP0101";

    /// <summary>A key that the policy format does not define.</summary>
    public const string UnknownKey = "SP0102";

    /// <summary>The file has no <c>version: 1</c>, or a version this Setpoint does not support.</summary>
    public const string UnsupportedVersion = "SP0103";

    /// <summary>A custom YAML tag such as <c>!include</c>. Only the standard <c>!!</c> tags are accepted.</summary>
    public const string CustomTag = "SP0104";

    /// <summary>A value that looks like environment or pipeline variable interpolation, which Setpoint never expands.</summary>
    public const string Interpolation = "SP0105";

    /// <summary>A value of the wrong type or shape, such as a string where a number belongs.</summary>
    public const string InvalidValue = "SP0106";

    /// <summary>A group or repo override declares a rule the baseline locks.</summary>
    public const string LockedRuleOverridden = "SP0107";

    /// <summary>A policy file is missing, misplaced or misnamed, or its contents don't match where it lives.</summary>
    public const string FileLayout = "SP0108";

    /// <summary>A key that exists in the format but is not allowed in this kind of layer, such as <c>locked</c> in a group.</summary>
    public const string KeyNotAllowedInLayer = "SP0109";

    /// <summary><c>locked:</c> names a rule that doesn't exist.</summary>
    public const string UnknownLockedRule = "SP0110";

    /// <summary>Two groups that match the same repo set the same rule to different values.</summary>
    public const string GroupConflict = "SP0111";

    /// <summary>A selector, scope or exclude glob has the wrong shape or unsupported syntax.</summary>
    public const string InvalidGlob = "SP0112";

    /// <summary>A selector or repo override names a target that targets.yml doesn't define.</summary>
    public const string UnknownTarget = "SP0113";

    /// <summary><c>allowedMergeTypes</c> is empty, which would block every pull request.</summary>
    public const string NoMergeTypes = "SP0114";

    /// <summary><c>minimumReviewers</c> is enabled without a count of at least 1.</summary>
    public const string InvalidReviewerCount = "SP0115";

    /// <summary>More than 10 reviewers for a GitHub repo, which rulesets don't allow.</summary>
    public const string TooManyReviewersForGitHub = "SP0116";

    /// <summary>An <c>auth:</c> method the target's platform doesn't support, such as <c>entra</c> on Azure DevOps Server.</summary>
    public const string UnsupportedAuth = "SP0117";

    /// <summary>A target is missing something it needs, such as <c>scope</c> on GitHub or <c>url</c> on Azure DevOps.</summary>
    public const string IncompleteTarget = "SP0118";

    /// <summary>A boolean rule written as <c>{ enabled: ... }</c>; plain <c>true</c> or <c>false</c> means the same.</summary>
    public const string RedundantEnabledForm = "SP0119";

    /// <summary>A repo override or exact selector names a repo outside its target's scope, so it can never apply.</summary>
    public const string OutsideScope = "SP0120";

    // 02xx: selection.

    /// <summary>A selector glob matches no repo in scope.</summary>
    public const string SelectorMatchesNothing = "SP0201";

    /// <summary>An exact selector or repo override names a repo that doesn't exist.</summary>
    public const string RepoNotFound = "SP0202";

    /// <summary>An archived or disabled repo in scope. It is skipped.</summary>
    public const string ArchivedRepoSkipped = "SP0203";

    /// <summary>An empty repo in scope, which has no default branch to protect. It is skipped.</summary>
    public const string EmptyRepoSkipped = "SP0204";

    /// <summary>A GitHub fork in scope on a target without <c>includeForks: true</c>. It is skipped.</summary>
    public const string ForkSkipped = "SP0205";

    /// <summary>A required check names a pipeline that doesn't exist in the repo's Azure DevOps project.</summary>
    public const string PipelineNotFound = "SP0206";

    // 10xx: reaching targets and reading repos.

    /// <summary>A target can't be reached or refused the preflight, so none of its repos are planned.</summary>
    public const string TargetUnreachable = "SP1001";

    /// <summary>Reading one repo's live state failed. The other repos are still planned.</summary>
    public const string RepoReadFailed = "SP1002";

    /// <summary>Writing one repo's changes failed. That repo stops; the others carry on unless <c>--fail-fast</c>.</summary>
    public const string WriteFailed = "SP1003";

    // 21xx: ownership.

    /// <summary>An unmanaged setting is stricter than the policy, so the effective result differs from the declared one.</summary>
    public const string UnmanagedStricter = "SP2101";

    /// <summary>Unmanaged merge settings and the policy together leave no merge method, so pull requests can't complete.</summary>
    public const string NoMergeMethodLeft = "SP2102";

    /// <summary>An explicit Allow for Force push on the default branch, such as its creator's, outranks the deny Setpoint manages.</summary>
    public const string ForcePushAllowed = "SP2103";

    // 22xx: matching live objects.

    /// <summary>More than one live Azure DevOps policy matches a rule, so Setpoint leaves them alone.</summary>
    public const string AmbiguousMatch = "SP2201";

    /// <summary>The token can't see a live setting, so plan reports it as unknown instead of as a change.</summary>
    public const string LiveValueUnknown = "SP2202";

    // 30xx: platform capabilities.

    /// <summary>A private repo on GitHub Free, where rulesets don't apply. It is skipped.</summary>
    public const string PrivateRepoOnGitHubFree = "SP3001";

    // 40xx: apply guards.

    /// <summary>The plan no longer matches live state, the policy or the Setpoint version, so apply writes nothing (exit 3).</summary>
    public const string StalePlan = "SP4001";

    /// <summary>More repos would change than <c>--max-changes</c> allows, so apply writes nothing (exit 4).</summary>
    public const string TooManyChanges = "SP4002";
}
