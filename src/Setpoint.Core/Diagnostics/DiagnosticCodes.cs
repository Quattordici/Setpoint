namespace Setpoint.Core.Diagnostics;

/// <summary>
/// The stable diagnostic codes. Codes never change meaning once released.
/// 01xx are policy file problems that <c>validate</c> finds offline.
/// </summary>
public static class DiagnosticCodes
{
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

    /// <summary>A group or repo override changes a rule the baseline locks. Reported by layer merge.</summary>
    public const string LockedRuleOverridden = "SP0107";

    /// <summary>A policy file is missing, misplaced or misnamed, or its contents don't match where it lives.</summary>
    public const string FileLayout = "SP0108";

    /// <summary>A key that exists in the format but is not allowed in this kind of layer, such as <c>locked</c> in a group.</summary>
    public const string KeyNotAllowedInLayer = "SP0109";
}
