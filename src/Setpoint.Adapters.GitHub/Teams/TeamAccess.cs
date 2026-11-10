using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Api;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.GitHub.Teams;

/// <summary>
/// The neutral <c>access</c> rule and <c>github.access</c> on GitHub: each team's permission on the repo is one
/// object, matched by team slug. Only the teams the policy names are written; the rest are left alone.
/// </summary>
/// <remarks>
/// The neutral roles map to GitHub's base permissions: read is <c>pull</c>, write is <c>push</c>, admin is
/// <c>admin</c>. A live <c>triage</c> or <c>maintain</c> (or a custom role) reads as its own name under
/// <c>access</c>, so a policy that says write shows the change from maintain instead of calling it in sync.
/// </remarks>
internal static class TeamAccess
{
    /// <summary>The neutral rule.</summary>
    public const string Access = "access";

    /// <summary>The provider-block rule.</summary>
    public const string GitHubAccess = "github.access";

    /// <summary>The rules team objects carry.</summary>
    public static readonly IReadOnlyList<string> Rules = [Access, GitHubAccess];

    private const string KeyPrefix = "gh:team/";

    /// <summary>A team's match key, and its key once it exists: <c>gh:team/platform</c>.</summary>
    public static string MatchKey(string slug) => KeyPrefix + slug.ToLowerInvariant();

    /// <summary>The team slug in a team match key, or null when the key isn't one.</summary>
    public static string? Slug(string matchKey) =>
        matchKey.StartsWith(KeyPrefix, StringComparison.Ordinal) && matchKey.Length > KeyPrefix.Length ? matchKey[KeyPrefix.Length..] : null;

    /// <summary>The repo's teams as live objects.</summary>
    public static IEnumerable<LiveObject> Read(IEnumerable<GitHubTeam> teams)
    {
        foreach (GitHubTeam team in teams)
        {
            string permission = Permission(team);
            var native = new JsonObject { ["slug"] = team.Slug.ToLowerInvariant(), ["permission"] = permission };
            yield return new LiveObject(MatchKey(team.Slug), MatchKey(team.Slug), native, ReadRules(team.Slug, permission), $"team {team.Slug}");
        }
    }

    /// <summary>The rule values a team with <paramref name="permission"/> carries.</summary>
    public static Dictionary<string, JsonNode?> ReadRules(string slug, string permission)
    {
        string key = slug.ToLowerInvariant();
        var rules = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [Access] = new JsonObject { [key] = Neutral(permission) },
        };
        if (permission is "triage" or "maintain")
        {
            rules[GitHubAccess] = new JsonObject { [key] = permission };
        }

        return rules;
    }

    /// <summary>
    /// Maps the declared access to one object per named team. <c>none</c> means the team must not have the repo.
    /// </summary>
    /// <param name="rules">The repo's desired rules.</param>
    /// <param name="owner">The repo's owner.</param>
    /// <param name="orgTeams">The organization's team slugs, or null when the token may not list them.</param>
    /// <exception cref="PolicyMappingException">A team is in both maps, isn't in the organization, or the owner is a user (SP0207).</exception>
    public static IEnumerable<DesiredObject> Map(IReadOnlyDictionary<string, DesiredRule> rules, GitHubAccount owner, IReadOnlySet<string>? orgTeams)
    {
        Dictionary<string, string> neutral = Entries(rules, Access);
        Dictionary<string, string> github = Entries(rules, GitHubAccess);
        if (neutral.Count == 0 && github.Count == 0)
        {
            yield break;
        }

        if (!owner.MayHaveTeams)
        {
            throw new PolicyMappingException(
                DiagnosticCodes.PrincipalNotFound,
                $"access: {owner.Login} is a user account, and only organizations have teams. Leave access unmanaged (~) for repos owned by users.");
        }

        if (neutral.Keys.Intersect(github.Keys, StringComparer.Ordinal).FirstOrDefault() is { } both)
        {
            throw new PolicyMappingException(
                DiagnosticCodes.PrincipalNotFound,
                $"access: team '{both}' is in both access and github.access. Give it one role: a neutral one in access, or triage or maintain in github.access.");
        }

        foreach (string slug in neutral.Keys.Concat(github.Keys))
        {
            if (orgTeams is not null && !orgTeams.Contains(slug))
            {
                throw new PolicyMappingException(
                    DiagnosticCodes.PrincipalNotFound,
                    $"access: there is no team '{slug}' in {owner.Login}. Use the team's slug, as in github.com/orgs/{owner.Login}/teams/<slug>.");
            }

            string? permission = github.TryGetValue(slug, out string? githubRole) ? githubRole : Native(neutral[slug]);
            var carried = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (permission is null)
            {
                carried[Access] = null;
                yield return new DesiredObject(MatchKey(slug), $"gh:new/team/{slug}", carried, null);
                continue;
            }

            // What the team will carry once written, read back the way a live team is.
            Dictionary<string, JsonNode?> read = ReadRules(slug, permission);
            carried[neutral.ContainsKey(slug) ? Access : GitHubAccess] = read.GetValueOrDefault(neutral.ContainsKey(slug) ? Access : GitHubAccess)?.DeepClone();
            yield return new DesiredObject(MatchKey(slug), $"gh:new/team/{slug}", carried, new JsonObject { ["permission"] = permission }, $"team {slug} (new)");
        }
    }

    // GitHub reports a team's role as a permission (pull, push) or a role name (read, write); custom roles only
    // by role name.
    private static string Permission(GitHubTeam team) => (team.RoleName ?? team.Permission)?.ToLowerInvariant() switch
    {
        "read" or "pull" => "pull",
        "write" or "push" => "push",
        null => "pull",
        string other => other,
    };

    private static string Neutral(string permission) => permission switch
    {
        "pull" => "read",
        "push" => "write",
        _ => permission,
    };

    private static string? Native(string role) => role switch
    {
        "read" => "pull",
        "write" => "push",
        "admin" => "admin",
        _ => null,
    };

    private static Dictionary<string, string> Entries(IReadOnlyDictionary<string, DesiredRule> rules, string rule)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rules.GetValueOrDefault(rule)?.Value is JsonObject map)
        {
            foreach ((string slug, JsonNode? role) in map)
            {
                if (role is JsonValue value && value.TryGetValue(out string? text))
                {
                    entries[slug.ToLowerInvariant()] = text;
                }
            }
        }

        return entries;
    }
}
