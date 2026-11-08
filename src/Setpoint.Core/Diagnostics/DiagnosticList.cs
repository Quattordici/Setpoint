namespace Setpoint.Core.Diagnostics;

/// <summary>Operations over a run's diagnostics.</summary>
public static class DiagnosticList
{
    /// <summary>
    /// Applies <c>--strict</c>: every warning becomes an error. Info lines stay as they are.
    /// </summary>
    public static IReadOnlyList<Diagnostic> ApplyStrict(IEnumerable<Diagnostic> diagnostics, bool strict) =>
        strict
            ? [.. diagnostics.Select(d => d.Severity == DiagnosticSeverity.Warning ? d with { Severity = DiagnosticSeverity.Error } : d)]
            : [.. diagnostics];

    /// <summary>True when any diagnostic is an error.</summary>
    public static bool HasErrors(this IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>Orders diagnostics by file, line and column, with diagnostics that have no location first.</summary>
    public static IReadOnlyList<Diagnostic> InSourceOrder(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics
            .OrderBy(d => d.Location?.File ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Location?.Line ?? 0)
            .ThenBy(d => d.Location?.Column ?? 0)];
}
