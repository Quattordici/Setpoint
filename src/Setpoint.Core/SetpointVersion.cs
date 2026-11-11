using System.Reflection;

namespace Setpoint.Core;

/// <summary>The running Setpoint version, as plan files record it.</summary>
public static class SetpointVersion
{
    /// <summary>The version from the git tag, such as <c>1.0.0</c>, or <c>1.0.1-alpha.0.3</c> for a build after it.</summary>
    public static string Current { get; } =
        typeof(SetpointVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SetpointVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
