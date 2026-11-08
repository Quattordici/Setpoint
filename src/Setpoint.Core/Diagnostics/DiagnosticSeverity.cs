using System.Text.Json.Serialization;

namespace Setpoint.Core.Diagnostics;

/// <summary>How serious a <see cref="Diagnostic"/> is. <c>--strict</c> turns every warning into an error.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    /// <summary>Something worth knowing that needs no action, such as a skipped archived repo.</summary>
    [JsonStringEnumMemberName("info")]
    Info,

    /// <summary>Something that is probably a mistake but does not stop the run.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>Something that stops the run.</summary>
    [JsonStringEnumMemberName("error")]
    Error,
}
