using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Core.Providers;

/// <summary>One target from targets.yml, by name.</summary>
/// <param name="Name">The target name, which selectors and repo overrides start with.</param>
/// <param name="Definition">Its definition. It never holds a secret; tokens come from environment variables.</param>
public sealed record Target(string Name, TargetDefinition Definition);

/// <summary>What the preflight found out about a target.</summary>
/// <param name="Detected">What the target is, such as <c>Azure DevOps Server 2022.1</c> or <c>github.com</c>.</param>
/// <param name="ApiVersion">The API version Setpoint uses there.</param>
/// <param name="Identity">Who Setpoint signs in as, when the platform says.</param>
public sealed record TargetInfo(string Detected, string ApiVersion, string? Identity = null);

/// <summary>A repo the adapter listed.</summary>
/// <param name="Repo">The repo.</param>
/// <param name="Skipped">Why the repo is skipped (archived, empty, a fork), or null when it is managed.</param>
public sealed record ListedRepo(RepoRef Repo, Diagnostic? Skipped = null);

/// <summary>What can be managed on one repo.</summary>
/// <param name="Skipped">Why the repo can't be managed at all, such as SP3001, or null when it can.</param>
public sealed record RepoCapabilities(Diagnostic? Skipped = null)
{
    /// <summary>Everything can be managed.</summary>
    public static readonly RepoCapabilities Full = new();
}
