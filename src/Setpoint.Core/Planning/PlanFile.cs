using System.Text.Encodings.Web;
using System.Text.Json;

namespace Setpoint.Core.Planning;

/// <summary>Reads and writes plan files.</summary>
public static class PlanFile
{
    // Readable characters (' and + unescaped): the file is read by people in a pull request, never embedded in HTML.
    private static readonly PlanJsonContext s_context = new(new JsonSerializerOptions(PlanJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>The plan as JSON, the way <c>plan --out</c> and <c>plan --json</c> write it.</summary>
    public static string Serialize(PlanDocument plan) =>
        JsonSerializer.Serialize(plan, s_context.PlanDocument).ReplaceLineEndings("\n") + "\n";

    /// <summary>Reads a plan file.</summary>
    /// <exception cref="JsonException">The text is not a plan file in this format.</exception>
    public static PlanDocument Deserialize(string json) =>
        JsonSerializer.Deserialize(json, s_context.PlanDocument)
        ?? throw new JsonException("The plan file is empty.");
}
