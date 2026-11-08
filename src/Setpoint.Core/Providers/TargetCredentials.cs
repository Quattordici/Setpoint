using System.Text;

namespace Setpoint.Core.Providers;

/// <summary>
/// Where a target's secrets come from. Secrets are only ever read from environment variables, never from policy
/// files: <c>SETPOINT_&lt;TARGET&gt;_TOKEN</c> by default, or the variable the target names in <c>tokenEnv:</c>.
/// </summary>
public static class TargetCredentials
{
    /// <summary>The variable holding the target's token: its <c>tokenEnv:</c>, or <c>SETPOINT_&lt;TARGET&gt;_TOKEN</c>.</summary>
    public static string TokenVariable(Target target) => target.Definition.TokenEnv ?? $"SETPOINT_{EnvironmentName(target.Name)}_TOKEN";

    /// <summary>The variable Azure Pipelines puts the job's access token in, for <c>auth: accessToken</c>.</summary>
    public const string AccessTokenVariable = "SYSTEM_ACCESSTOKEN";

    /// <summary>The variable holding a GitHub App's private key: <c>SETPOINT_&lt;TARGET&gt;_APP_KEY</c>.</summary>
    public static string AppKeyVariable(Target target) => $"SETPOINT_{EnvironmentName(target.Name)}_APP_KEY";

    /// <summary>
    /// The target name as it appears in a variable name: upper case, with every character other than a letter or
    /// digit replaced by an underscore, so <c>ado-onprem</c> becomes <c>ADO_ONPREM</c>.
    /// </summary>
    public static string EnvironmentName(string targetName)
    {
        var name = new StringBuilder(targetName.Length);
        foreach (char c in targetName)
        {
            name.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        return name.ToString();
    }
}
