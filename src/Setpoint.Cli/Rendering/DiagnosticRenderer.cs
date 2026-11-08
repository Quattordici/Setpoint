using Setpoint.Core.Diagnostics;
using Spectre.Console;

namespace Setpoint.Cli.Rendering;

/// <summary>
/// Writes diagnostics. Plain output uses the compiler format (<c>file:line:col: error SP0102: message</c>),
/// which editors and CI logs turn into links; tree output adds colour and puts the message on its own line.
/// </summary>
internal static class DiagnosticRenderer
{
    public static void Write(CliConsole console, IEnumerable<Diagnostic> diagnostics, OutputFormat format)
    {
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (format == OutputFormat.Plain)
            {
                // Straight to the writer: plain lines never wrap, so log parsers see one line per diagnostic.
                console.OutWriter.WriteLine(Plain(diagnostic));
                continue;
            }

            string colour = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => "red",
                DiagnosticSeverity.Warning => "yellow",
                _ => "blue",
            };
            string severity = diagnostic.Severity.ToString().ToLowerInvariant();
            string location = diagnostic.Location is null ? string.Empty : $" [dim]{Markup.Escape(diagnostic.Location.ToString())}[/]";
            console.Out.MarkupLine($"[{colour} bold]{severity}[/] [bold]{diagnostic.Code}[/]{location}");
            console.Out.MarkupLine($"  {Markup.Escape(diagnostic.Message)}");
        }
    }

    public static string Plain(Diagnostic diagnostic)
    {
        string severity = diagnostic.Severity.ToString().ToLowerInvariant();
        return diagnostic.Location is null
            ? $"{severity} {diagnostic.Code}: {diagnostic.Message}"
            : $"{diagnostic.Location}: {severity} {diagnostic.Code}: {diagnostic.Message}";
    }
}
