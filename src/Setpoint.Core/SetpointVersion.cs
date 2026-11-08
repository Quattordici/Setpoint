using System.Reflection;

namespace Setpoint.Core;

/// <summary>The running Setpoint version, as plan files record it.</summary>
public static class SetpointVersion
{
    /// <summary>The informational version, such as <c>0.1.0-alpha+3f9c2a1</c>.</summary>
    public static string Current { get; } =
        typeof(SetpointVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SetpointVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
