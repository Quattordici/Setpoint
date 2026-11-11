using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Setpoint.Core.Policy.Model;

/// <summary>
/// The neutral rules. Each means the same on every platform. The first five apply to the repo's default branch;
/// <c>access</c> and <c>environments</c> apply to the repo.
/// A layer that sets a rule replaces the value from the layers above wholesale; lists are replaced, never appended.
/// </summary>
public sealed record NeutralRules
{
    /// <summary>The rule names as they appear in policy files, in the order the spec lists them.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "minimumReviewers",
        "requiredChecks",
        "requireCommentResolution",
        "allowedMergeTypes",
        "protectFromForcePushAndDelete",
        "access",
        "environments",
    ];

    /// <summary>Approvals required before a pull request can complete.</summary>
    [Description("Approvals required before a pull request can complete. ~ leaves it unmanaged; enabled: false removes it.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<MinimumReviewersRule> MinimumReviewers { get; init; }

    /// <summary>Checks that must pass. On Azure DevOps each name is a pipeline, enforced as build validation.</summary>
    [Description("Checks that must pass before a pull request can complete. On Azure DevOps each name is a pipeline, enforced as build validation. An empty list removes them.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<IReadOnlyList<RequiredCheck>> RequiredChecks { get; init; }

    /// <summary>All review comments must be resolved before a pull request can complete.</summary>
    [Description("All review comments must be resolved before a pull request can complete. false removes the setting.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<BooleanRule> RequireCommentResolution { get; init; }

    /// <summary>The merge types a pull request may complete with.</summary>
    [Description("The merge types a pull request may complete with. Must not be empty.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<IReadOnlyList<MergeType>> AllowedMergeTypes { get; init; }

    /// <summary>The default branch can't be force-pushed or deleted.</summary>
    [Description("The default branch can't be force-pushed or deleted. false removes the protection.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<BooleanRule> ProtectFromForcePushAndDelete { get; init; }

    /// <summary>
    /// Team and group permissions on the repo: a principal name, as the platform knows it, to a role. Only the
    /// principals listed are managed; everyone else's access is left alone. A lower layer replaces the whole map.
    /// </summary>
    [Description("Team and group permissions on the repo: a team slug (GitHub) or group name (Azure DevOps) to read, write, admin, or none to remove the access Setpoint manages. Principals not listed are left alone. A lower layer replaces the whole map.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<IReadOnlyDictionary<string, AccessRole>> Access { get; init; }

    /// <summary>
    /// Deployment environments and their approvals, by environment name. Only the environments listed are managed;
    /// a missing one is created, and none is ever deleted. A lower layer replaces the whole map.
    /// </summary>
    [Description("Deployment environments and their approvals, by environment name. Environments not listed are left alone; a missing one is created, and none is ever deleted. A lower layer replaces the whole map.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<IReadOnlyDictionary<string, EnvironmentRule>> Environments { get; init; }
}

/// <summary>One environment in the <c>environments</c> rule. A field left out keeps its live value.</summary>
public sealed record EnvironmentRule
{
    /// <summary>False removes the protection Setpoint manages and keeps the environment.</summary>
    [Description("false removes the approvals, wait and branch restriction, and keeps the environment.")]
    // A setter, not init: the source-generated reader would otherwise overwrite the initializer when the key is absent.
    public bool Enabled { get; set; } = true;

    /// <summary>Who must approve a deployment: team slugs on GitHub, group or user names on Azure DevOps.</summary>
    [Description("Who must approve a deployment: team slugs on GitHub (users go in github.environments), group or user names on Azure DevOps. An empty list removes the approvals.")]
    public IReadOnlyList<string>? Reviewers { get; init; }

    /// <summary>Minutes to wait before a deployment starts, 0 for none.</summary>
    [Description("Minutes to wait before a deployment starts, from 0 (none) to 43200 (30 days).")]
    public int? WaitMinutes { get; init; }

    /// <summary>Which branches may deploy.</summary>
    [Description("Which branches may deploy: default (the default branch, or protected branches on GitHub) or any.")]
    public DeploymentBranches? Branches { get; init; }
}

/// <summary>The branches that may deploy to an environment.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<DeploymentBranches>))]
public enum DeploymentBranches
{
    /// <summary>The default branch; on GitHub, protected branches.</summary>
    Default,

    /// <summary>Any branch.</summary>
    Any,
}

/// <summary>A neutral role on a repo, for the <c>access</c> rule.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<AccessRole>))]
public enum AccessRole
{
    /// <summary>No access granted by Setpoint: the principal's managed access is removed.</summary>
    None,

    /// <summary>Clone and pull; on GitHub <c>pull</c>.</summary>
    Read,

    /// <summary>Push and contribute to pull requests; on GitHub <c>push</c>.</summary>
    Write,

    /// <summary>Everything, including the repo's settings and permissions.</summary>
    Admin,
}

/// <summary>The <c>minimumReviewers</c> rule.</summary>
public sealed record MinimumReviewersRule : IToggleableRule
{
    /// <summary>False means the setting must be absent; the other fields are then ignored.</summary>
    [Description("false means the setting must be absent.")]
    // A setter, not init: the source-generated reader would otherwise overwrite the initializer when the key is absent.
    public bool Enabled { get; set; } = true;

    /// <summary>How many approvals are needed.</summary>
    [Description("How many approvals are needed. GitHub allows at most 10.")]
    public int? Count { get; init; }

    /// <summary>Whether a new push resets existing approvals.</summary>
    [Description("Whether a new push to the source branch resets existing approvals.")]
    public bool ResetOnPush { get; init; }
}

/// <summary>One required check.</summary>
public sealed record RequiredCheck
{
    /// <summary>The check name on GitHub, or the pipeline name on Azure DevOps.</summary>
    [Description("The status check context on GitHub, or the pipeline name on Azure DevOps.")]
    public required string Name { get; init; }
}

/// <summary>A way to complete a pull request.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<MergeType>))]
public enum MergeType
{
    /// <summary>A merge commit.</summary>
    Merge,

    /// <summary>Squash into one commit.</summary>
    Squash,

    /// <summary>Rebase onto the target branch (fast-forward).</summary>
    Rebase,
}
