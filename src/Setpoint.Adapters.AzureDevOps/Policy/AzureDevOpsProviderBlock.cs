using System.ComponentModel;
using System.Text.Json.Serialization;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.AzureDevOps.Policy;

/// <summary>
/// The <c>azuredevops:</c> block of a layer: options that only exist on Azure DevOps, grouped under the
/// neutral rule they extend. Each entry is replaced wholesale by a lower layer.
/// </summary>
[Description("Options that exist only on Azure DevOps, grouped under the neutral rule they extend.")]
public sealed record AzureDevOpsProviderBlock : IProviderBlock
{
    /// <summary>Azure DevOps options for the neutral minimumReviewers rule.</summary>
    [Description("Azure DevOps options for the neutral minimumReviewers rule.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<AzureDevOpsMinimumReviewersOptions> MinimumReviewers { get; init; }

    /// <summary>Azure DevOps options for the neutral requiredChecks rule.</summary>
    [Description("Azure DevOps options for the neutral requiredChecks rule.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<AzureDevOpsRequiredChecksOptions> RequiredChecks { get; init; }

    /// <summary>Azure DevOps options for the neutral allowedMergeTypes rule.</summary>
    [Description("Azure DevOps options for the neutral allowedMergeTypes rule.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<AzureDevOpsMergeTypesOptions> AllowedMergeTypes { get; init; }
}

/// <summary>The <c>azuredevops.minimumReviewers</c> options.</summary>
public sealed record AzureDevOpsMinimumReviewersOptions
{
    /// <summary>The pull request creator's own vote counts toward the minimum.</summary>
    [Description("The pull request creator's own vote counts toward the minimum.")]
    public bool CreatorVoteCounts { get; init; }

    /// <summary>A pull request can complete even when someone votes 'waiting' or 'rejected'.</summary>
    [Description("A pull request can complete even when some reviewers vote 'waiting' or 'rejected'.")]
    public bool AllowDownvotes { get; init; }

    /// <summary>The most recent pusher's vote does not count.</summary>
    [Description("The vote of the person who pushed most recently does not count.")]
    public bool BlockLastPusherVote { get; init; }
}

/// <summary>The <c>azuredevops.requiredChecks</c> options.</summary>
public sealed record AzureDevOpsRequiredChecksOptions
{
    /// <summary>Minutes a build result stays valid; 0 means it never expires.</summary>
    [Description("Minutes a build validation result stays valid. 0 means it never expires.")]
    public int? ValidDuration { get; init; }

    /// <summary>Paths that trigger build validation, in Azure DevOps filter syntax.</summary>
    [Description("Paths that trigger build validation, in Azure DevOps filter syntax, such as /src/*.")]
    public IReadOnlyList<string>? PathFilters { get; init; }

    /// <summary>External status checks that must succeed, posted by other services.</summary>
    [Description("External status checks that must succeed, posted by other services rather than Azure Pipelines.")]
    public IReadOnlyList<AzureDevOpsStatusCheck>? StatusChecks { get; init; }
}

/// <summary>One external status check.</summary>
public sealed record AzureDevOpsStatusCheck
{
    /// <summary>The status name.</summary>
    [Description("The status name the external service posts.")]
    public required string Name { get; init; }

    /// <summary>The status genre, if the service sets one.</summary>
    [Description("The status genre, if the service sets one.")]
    public string? Genre { get; init; }
}

/// <summary>The <c>azuredevops.allowedMergeTypes</c> options.</summary>
public sealed record AzureDevOpsMergeTypesOptions
{
    /// <summary>Allow rebase with a merge commit (semi-linear).</summary>
    [Description("Also allow rebase with a merge commit (semi-linear merge).")]
    public bool AllowRebaseMerge { get; init; }
}
