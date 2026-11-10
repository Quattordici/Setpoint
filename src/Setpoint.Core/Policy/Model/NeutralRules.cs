using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Setpoint.Core.Policy.Model;

/// <summary>
/// The neutral rules. Each means the same on every platform. The first five apply to the repo's default branch;
/// <c>access</c> applies to the repo.
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
