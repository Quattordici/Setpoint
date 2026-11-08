namespace Setpoint.Core.Diagnostics;

/// <summary>One entry in the diagnostic catalogue.</summary>
/// <param name="Code">The stable code.</param>
/// <param name="DefaultSeverity">The severity before <c>--strict</c>.</param>
/// <param name="Title">A one-line summary, as in docs/diagnostics.md.</param>
public sealed record DiagnosticDescriptor(string Code, DiagnosticSeverity DefaultSeverity, string Title);

/// <summary>Every diagnostic Setpoint can report. docs/diagnostics.md lists the same entries; a test keeps them in step.</summary>
public static class DiagnosticCatalogue
{
    public static readonly IReadOnlyList<DiagnosticDescriptor> All =
    [
        new(DiagnosticCodes.InvalidYaml, DiagnosticSeverity.Error, "Invalid YAML"),
        new(DiagnosticCodes.UnknownKey, DiagnosticSeverity.Error, "Unknown key"),
        new(DiagnosticCodes.UnsupportedVersion, DiagnosticSeverity.Error, "Missing or unsupported version"),
        new(DiagnosticCodes.CustomTag, DiagnosticSeverity.Error, "Custom YAML tag"),
        new(DiagnosticCodes.Interpolation, DiagnosticSeverity.Error, "Variable interpolation"),
        new(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Error, "Invalid value"),
        new(DiagnosticCodes.LockedRuleOverridden, DiagnosticSeverity.Error, "Locked rule overridden"),
        new(DiagnosticCodes.FileLayout, DiagnosticSeverity.Error, "Policy file out of place"),
        new(DiagnosticCodes.KeyNotAllowedInLayer, DiagnosticSeverity.Error, "Key not allowed in this layer"),
        new(DiagnosticCodes.UnknownLockedRule, DiagnosticSeverity.Error, "Unknown rule in locked"),
        new(DiagnosticCodes.GroupConflict, DiagnosticSeverity.Error, "Groups conflict"),
        new(DiagnosticCodes.InvalidGlob, DiagnosticSeverity.Error, "Invalid glob"),
        new(DiagnosticCodes.UnknownTarget, DiagnosticSeverity.Error, "Unknown target"),
        new(DiagnosticCodes.NoMergeTypes, DiagnosticSeverity.Error, "No merge types allowed"),
        new(DiagnosticCodes.InvalidReviewerCount, DiagnosticSeverity.Error, "Invalid reviewer count"),
        new(DiagnosticCodes.TooManyReviewersForGitHub, DiagnosticSeverity.Error, "Too many reviewers for GitHub"),
        new(DiagnosticCodes.UnsupportedAuth, DiagnosticSeverity.Error, "Auth method not supported here"),
        new(DiagnosticCodes.IncompleteTarget, DiagnosticSeverity.Error, "Incomplete target"),
        new(DiagnosticCodes.RedundantEnabledForm, DiagnosticSeverity.Warning, "Redundant enabled form"),
        new(DiagnosticCodes.OutsideScope, DiagnosticSeverity.Warning, "Repo outside target scope"),
        new(DiagnosticCodes.SelectorMatchesNothing, DiagnosticSeverity.Warning, "Selector matches nothing"),
        new(DiagnosticCodes.RepoNotFound, DiagnosticSeverity.Error, "Repo not found"),
        new(DiagnosticCodes.ArchivedRepoSkipped, DiagnosticSeverity.Info, "Archived repo skipped"),
        new(DiagnosticCodes.EmptyRepoSkipped, DiagnosticSeverity.Warning, "Empty repo skipped"),
        new(DiagnosticCodes.ForkSkipped, DiagnosticSeverity.Info, "Fork skipped"),
        new(DiagnosticCodes.PipelineNotFound, DiagnosticSeverity.Error, "Pipeline not found"),
        new(DiagnosticCodes.TargetUnreachable, DiagnosticSeverity.Error, "Target unreachable"),
        new(DiagnosticCodes.RepoReadFailed, DiagnosticSeverity.Error, "Repo read failed"),
        new(DiagnosticCodes.UnmanagedStricter, DiagnosticSeverity.Warning, "Unmanaged setting is stricter"),
        new(DiagnosticCodes.NoMergeMethodLeft, DiagnosticSeverity.Error, "No merge method left"),
        new(DiagnosticCodes.ForcePushAllowed, DiagnosticSeverity.Warning, "Force push explicitly allowed"),
        new(DiagnosticCodes.AmbiguousMatch, DiagnosticSeverity.Warning, "Ambiguous live match"),
        new(DiagnosticCodes.LiveValueUnknown, DiagnosticSeverity.Warning, "Live value unknown"),
        new(DiagnosticCodes.PrivateRepoOnGitHubFree, DiagnosticSeverity.Warning, "Private repo on GitHub Free skipped"),
    ];

    /// <summary>The entry for <paramref name="code"/>.</summary>
    /// <exception cref="KeyNotFoundException">The code is not in the catalogue.</exception>
    public static DiagnosticDescriptor Get(string code) =>
        All.FirstOrDefault(d => d.Code == code) ?? throw new KeyNotFoundException($"{code} is not in the diagnostic catalogue.");
}
