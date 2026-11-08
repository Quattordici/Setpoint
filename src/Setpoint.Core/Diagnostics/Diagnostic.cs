namespace Setpoint.Core.Diagnostics;

/// <summary>
/// One problem Setpoint found, with a stable <c>SPxxxx</c> code that docs and pipelines can refer to.
/// </summary>
/// <param name="Code">The stable code, such as <c>SP0102</c>. See <see cref="DiagnosticCodes"/>.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What is wrong, written for the person who has to fix it.</param>
/// <param name="Location">Where it is, when it belongs to a place in a policy file.</param>
public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, SourceLocation? Location = null)
{
    /// <summary>Creates an error.</summary>
    public static Diagnostic Error(string code, string message, SourceLocation? location = null) =>
        new(code, DiagnosticSeverity.Error, message, location);

    /// <summary>Creates a warning.</summary>
    public static Diagnostic Warning(string code, string message, SourceLocation? location = null) =>
        new(code, DiagnosticSeverity.Warning, message, location);

    /// <summary>Creates an info message, such as a repo skipped on purpose.</summary>
    public static Diagnostic Info(string code, string message, SourceLocation? location = null) =>
        new(code, DiagnosticSeverity.Info, message, location);

    /// <inheritdoc />
    public override string ToString()
    {
        string severity = Severity.ToString().ToLowerInvariant();
        return Location is null
            ? $"{severity} {Code}: {Message}"
            : $"{severity} {Code}: {Location}: {Message}";
    }
}
