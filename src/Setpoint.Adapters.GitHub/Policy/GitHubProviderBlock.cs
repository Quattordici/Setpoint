using System.ComponentModel;
using System.Text.Json.Serialization;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.GitHub.Policy;

/// <summary>
/// The <c>github:</c> block of a layer: options that only exist on GitHub. Keys extending a neutral rule sit
/// under that rule's name; the rest sit under a GitHub area. Each entry is replaced wholesale by a lower layer.
/// </summary>
[Description("Options that exist only on GitHub. They apply to the setpoint ruleset.")]
public sealed record GitHubProviderBlock : IProviderBlock
{
    /// <summary>Pull request options with no neutral equivalent.</summary>
    [Description("Pull request options with no neutral equivalent.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<GitHubPullRequestOptions> PullRequest { get; init; }

    /// <summary>GitHub-only options for the neutral requiredChecks rule.</summary>
    [Description("GitHub-only options for the neutral requiredChecks rule.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<GitHubRequiredChecksOptions> RequiredChecks { get; init; }

    /// <summary>Who may bypass the setpoint ruleset. Nobody may unless this is declared.</summary>
    [Description("Who may bypass the setpoint ruleset. Nobody may unless this is declared.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<IReadOnlyList<GitHubBypassActor>> BypassActors { get; init; }

    /// <summary>GitHub-only roles for the neutral access rule: a team slug to triage or maintain.</summary>
    [Description("GitHub-only roles for the neutral access rule: a team slug to triage or maintain. A team can't be in both access and github.access.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Declared<IReadOnlyDictionary<string, GitHubTeamRole>> Access { get; init; }
}

/// <summary>A GitHub repository role with no neutral equivalent.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<GitHubTeamRole>))]
public enum GitHubTeamRole
{
    /// <summary>Read, plus managing issues and pull requests without write access.</summary>
    Triage,

    /// <summary>Write, plus managing the repo without access to sensitive or destructive actions.</summary>
    Maintain,
}

/// <summary>The <c>github.pullRequest</c> options.</summary>
public sealed record GitHubPullRequestOptions
{
    /// <summary>A code owner must approve changes to files they own.</summary>
    [Description("A code owner must approve changes to files they own.")]
    public bool RequireCodeOwnerReview { get; init; }

    /// <summary>The most recent push must be approved by someone other than its author.</summary>
    [Description("The most recent push must be approved by someone other than the person who pushed it.")]
    public bool RequireLastPushApproval { get; init; }
}

/// <summary>The <c>github.requiredChecks</c> options.</summary>
public sealed record GitHubRequiredChecksOptions
{
    /// <summary>The branch must be up to date with the default branch before merging.</summary>
    [Description("The branch must be up to date with the default branch before merging.")]
    public bool Strict { get; init; }

    /// <summary>Pins a check to the GitHub App that must report it, by check name.</summary>
    [Description("Pins a check to the GitHub App that must report it: check name to App id.")]
    public IReadOnlyDictionary<string, long>? IntegrationIds { get; init; }
}

/// <summary>One actor allowed to bypass the setpoint ruleset.</summary>
public sealed record GitHubBypassActor
{
    /// <summary>What kind of actor this is.</summary>
    [Description("What kind of actor this is.")]
    public required GitHubBypassActorType ActorType { get; init; }

    /// <summary>The actor's id. Not used for organizationAdmin.</summary>
    [Description("The team, App, role or deploy key id. Not used for organizationAdmin.")]
    public long? ActorId { get; init; }

    /// <summary>When the actor may bypass. Defaults to always.</summary>
    [Description("When the actor may bypass: always, or only through a pull request.")]
    // A setter, not init: the source-generated reader would otherwise overwrite the initializer when the key is absent.
    public GitHubBypassMode BypassMode { get; set; } = GitHubBypassMode.Always;
}

/// <summary>A kind of ruleset bypass actor.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<GitHubBypassActorType>))]
public enum GitHubBypassActorType
{
    /// <summary>A GitHub App.</summary>
    Integration,

    /// <summary>Organization owners.</summary>
    OrganizationAdmin,

    /// <summary>A repository role.</summary>
    RepositoryRole,

    /// <summary>A team.</summary>
    Team,

    /// <summary>A deploy key.</summary>
    DeployKey,
}

/// <summary>When a bypass actor may bypass.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<GitHubBypassMode>))]
public enum GitHubBypassMode
{
    /// <summary>Always.</summary>
    Always,

    /// <summary>Only when merging a pull request.</summary>
    PullRequest,
}
