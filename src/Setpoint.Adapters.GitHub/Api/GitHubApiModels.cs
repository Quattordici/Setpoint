using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Setpoint.Adapters.GitHub.Api;

/// <summary>The signed-in account, from <c>GET /user</c>.</summary>
internal sealed record GitHubUser(string Login);

/// <summary>An organization or user that owns repos.</summary>
internal sealed record GitHubAccount(string Login);

/// <summary>
/// A repo, from a repo listing or <c>GET /repos/{owner}/{repo}</c>. The merge flags are only in the single-repo
/// response, and GitHub leaves them out when the token can't read the repo's contents, so they are nullable.
/// </summary>
internal sealed record GitHubRepository
{
    public required string Name { get; init; }

    public required GitHubAccount Owner { get; init; }

    public bool Private { get; init; }

    public bool Archived { get; init; }

    public bool Disabled { get; init; }

    public bool Fork { get; init; }

    /// <summary>The repo's size in KB. 0 for an empty repo, though a tiny repo can report 0 too.</summary>
    public long Size { get; init; }

    public string? DefaultBranch { get; init; }

    public bool? AllowMergeCommit { get; init; }

    public bool? AllowSquashMerge { get; init; }

    public bool? AllowRebaseMerge { get; init; }
}

/// <summary>A branch, from <c>GET /repos/{owner}/{repo}/branches</c>. Only its name is used.</summary>
internal sealed record GitHubBranch(string Name);

/// <summary>A ruleset as listed by <c>GET /repos/{owner}/{repo}/rulesets</c>, without its rules.</summary>
internal sealed record GitHubRulesetSummary
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    public string? Target { get; init; }

    /// <summary><c>Repository</c>, <c>Organization</c> or <c>Enterprise</c>.</summary>
    public string? SourceType { get; init; }

    /// <summary>Who owns the ruleset: <c>owner/repo</c> or the organization name.</summary>
    public string? Source { get; init; }

    public string? Enforcement { get; init; }
}

/// <summary>One active rule on a branch, from <c>GET /repos/{owner}/{repo}/rules/branches/{branch}</c>.</summary>
internal sealed record GitHubBranchRule
{
    public required string Type { get; init; }

    public JsonObject? Parameters { get; init; }

    public string? RulesetSourceType { get; init; }

    public string? RulesetSource { get; init; }

    public long? RulesetId { get; init; }
}

/// <summary>The App itself, from <c>GET /app</c>.</summary>
internal sealed record GitHubApp(string Slug);

/// <summary>An installation of the App on an account.</summary>
internal sealed record GitHubInstallation(long Id, GitHubAccount Account);

/// <summary>An installation token, from <c>POST /app/installations/{id}/access_tokens</c>. It expires after an hour.</summary>
internal sealed record GitHubInstallationToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>A page of <c>GET /installation/repositories</c>.</summary>
internal sealed record GitHubInstallationRepositories(List<GitHubRepository> Repositories);

/// <summary>The GitHub REST API's JSON contract: snake_case, and every field Setpoint doesn't read is ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubUser))]
[JsonSerializable(typeof(GitHubRepository))]
[JsonSerializable(typeof(List<GitHubRepository>))]
[JsonSerializable(typeof(List<GitHubBranch>))]
[JsonSerializable(typeof(List<GitHubRulesetSummary>))]
[JsonSerializable(typeof(List<GitHubBranchRule>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(GitHubApp))]
[JsonSerializable(typeof(GitHubInstallation))]
[JsonSerializable(typeof(List<GitHubInstallation>))]
[JsonSerializable(typeof(GitHubInstallationToken))]
[JsonSerializable(typeof(GitHubInstallationRepositories))]
internal sealed partial class GitHubApiJsonContext : JsonSerializerContext;
