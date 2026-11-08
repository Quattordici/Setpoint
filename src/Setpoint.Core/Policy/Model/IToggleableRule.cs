namespace Setpoint.Core.Policy.Model;

/// <summary>
/// A rule value that can say "this setting must be absent": <c>false</c> on a boolean rule,
/// or <c>enabled: false</c> on an object rule.
/// </summary>
public interface IToggleableRule
{
    /// <summary>False means the setting must be absent on the platform.</summary>
    bool Enabled { get; }
}
