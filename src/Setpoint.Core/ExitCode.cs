namespace Setpoint.Core;

/// <summary>
/// The process exit codes Setpoint returns. The values are part of the public contract,
/// because pipelines branch on them, so they never change once released.
/// </summary>
public enum ExitCode
{
    /// <summary>The command completed successfully.</summary>
    Success = 0,

    /// <summary>A runtime failure: an unreachable target, a failed write or a partial apply.</summary>
    RuntimeFailure = 1,

    /// <summary>The policy is invalid, or the command line is wrong. Everything <c>validate</c> catches.</summary>
    InvalidPolicyOrUsage = 2,

    /// <summary>The plan file no longer matches live state, the policy or the Setpoint version.</summary>
    StalePlan = 3,

    /// <summary>A safety guard tripped, such as <c>--max-changes</c>.</summary>
    GuardTripped = 4,

    /// <summary><c>plan --exit-code</c> found changes.</summary>
    ChangesFound = 5,
}
