using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Api;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Validation;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.GitHub.Environments;

/// <summary>
/// The neutral <c>environments</c> rule and <c>github.environments</c> on GitHub: each deployment environment is one
/// object, matched by name, holding its required reviewers (teams and users), wait timer and deployment branch
/// policy. Only the environments the policy names are written; a missing one is created, and none is deleted.
/// </summary>
/// <remarks>
/// A field the policy leaves out keeps its live value, so an environment's custom branch policies or its user
/// reviewers survive a policy that only sets the teams. <c>enabled: false</c> clears every reviewer, the wait timer
/// and the branch policy, and keeps the environment.
/// </remarks>
internal static class EnvironmentProtection
{
    /// <summary>The neutral rule.</summary>
    public const string Environments = "environments";

    /// <summary>The provider-block rule.</summary>
    public const string GitHubEnvironments = "github.environments";

    /// <summary>The rules environment objects carry.</summary>
    public static readonly IReadOnlyList<string> Rules = [Environments, GitHubEnvironments];

    private const string MatchPrefix = "gh:environment/";
    private const string CreatePrefix = "gh:new/environment/";

    /// <summary>An environment's match key. GitHub's environment names ignore case.</summary>
    public static string MatchKey(string name) => MatchPrefix + name.ToLowerInvariant();

    /// <summary>True when the match key is an environment's.</summary>
    public static bool IsMatchKey(string matchKey) => matchKey.StartsWith(MatchPrefix, StringComparison.Ordinal);

    /// <summary>The environment name in a plan key (<c>gh:environment/Production</c>, <c>gh:new/environment/Production</c>), or null.</summary>
    public static string? NameInKey(string key) =>
        key.StartsWith(CreatePrefix, StringComparison.Ordinal) && key.Length > CreatePrefix.Length ? key[CreatePrefix.Length..]
        : key.StartsWith(MatchPrefix, StringComparison.Ordinal) && key.Length > MatchPrefix.Length ? key[MatchPrefix.Length..]
        : null;

    /// <summary>The repo's environments as live objects.</summary>
    public static IEnumerable<LiveObject> Read(IEnumerable<JsonObject> environments)
    {
        foreach (JsonObject environment in environments)
        {
            JsonObject native = Native(environment);
            string name = native["name"]!.GetValue<string>();
            yield return new LiveObject($"{MatchPrefix}{name}", MatchKey(name), native, ReadRules(native), $"environment {name}");
        }
    }

    /// <summary>
    /// An environment as Setpoint keeps it: its name, wait timer, self-review flag, reviewers sorted by kind and name,
    /// and its deployment branch policy. Ids and timestamps of the protection rules are left out, so they don't
    /// change the fingerprint.
    /// </summary>
    public static JsonObject Native(JsonObject environment)
    {
        int wait = 0;
        bool preventSelfReview = false;
        var reviewers = new List<(string Type, long Id, string Name)>();
        foreach (JsonObject rule in (environment["protection_rules"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (Text(rule["type"]))
            {
                case "wait_timer":
                    wait = (int)Number(rule["wait_timer"]);
                    break;
                case "required_reviewers":
                    preventSelfReview = rule["prevent_self_review"] is JsonValue flag && flag.TryGetValue(out bool set) && set;
                    foreach (JsonObject reviewer in (rule["reviewers"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        string type = Text(reviewer["type"]) ?? "User";
                        JsonNode? who = reviewer["reviewer"];
                        string name = (type == "Team" ? Text(who?["slug"]) : Text(who?["login"])) ?? string.Empty;
                        reviewers.Add((type, Number(who?["id"]), name.ToLowerInvariant()));
                    }

                    break;
            }
        }

        return new JsonObject
        {
            ["name"] = Text(environment["name"]) ?? string.Empty,
            ["wait_timer"] = wait,
            ["prevent_self_review"] = preventSelfReview,
            ["reviewers"] = Reviewers(reviewers),
            ["deployment_branch_policy"] = environment["deployment_branch_policy"] is JsonObject policy
                ? new JsonObject
                {
                    ["protected_branches"] = policy["protected_branches"] is JsonValue p && p.TryGetValue(out bool protectedOnly) && protectedOnly,
                    ["custom_branch_policies"] = policy["custom_branch_policies"] is JsonValue c && c.TryGetValue(out bool custom) && custom,
                }
                : null,
        };
    }

    /// <summary>The rule values an environment in its native shape carries.</summary>
    public static Dictionary<string, JsonNode?> ReadRules(JsonObject native)
    {
        string key = native["name"]!.GetValue<string>().ToLowerInvariant();
        (string Type, long Id, string Name)[] reviewers = [.. ReviewersOf(native)];
        string[] users = [.. reviewers.Where(r => r.Type == "User").Select(r => r.Name)];
        bool preventSelfReview = native["prevent_self_review"]!.GetValue<bool>();
        var rules = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [Environments] = new JsonObject
            {
                [key] = new JsonObject
                {
                    ["branches"] = Branches(native["deployment_branch_policy"]),
                    ["reviewers"] = new JsonArray([.. reviewers.Where(r => r.Type == "Team").Select(r => (JsonNode)r.Name)]),
                    ["waitMinutes"] = native["wait_timer"]!.GetValue<int>(),
                },
            },
            [GitHubEnvironments] = users.Length == 0 && !preventSelfReview
                ? null
                : new JsonObject
                {
                    [key] = new JsonObject
                    {
                        ["preventSelfReview"] = preventSelfReview,
                        ["users"] = new JsonArray([.. users.Select(u => (JsonNode)u)]),
                    },
                },
        };
        return rules;
    }

    /// <summary>
    /// Maps the declared environments to one object per named environment: the live environment, or an empty one,
    /// with the declared fields replaced.
    /// </summary>
    /// <param name="rules">The repo's desired rules.</param>
    /// <param name="live">The live snapshot.</param>
    /// <param name="owner">The repo's owner.</param>
    /// <param name="teamIds">The organization's teams by slug, or null when they couldn't be listed.</param>
    /// <param name="userIds">Each user <c>github.environments</c> names, by lower-case login, as <c>PrepareAsync</c> found them.</param>
    /// <exception cref="PolicyMappingException">A reviewer isn't found (SP0207), or there are more than six (SP0121).</exception>
    public static IEnumerable<DesiredObject> Map(
        IReadOnlyDictionary<string, DesiredRule> rules,
        LiveSnapshot live,
        GitHubAccount owner,
        IReadOnlyDictionary<string, long>? teamIds,
        IReadOnlyDictionary<string, long> userIds)
    {
        Dictionary<string, (string Name, JsonObject Value)> neutral = Entries(rules, Environments);
        Dictionary<string, (string Name, JsonObject Value)> github = Entries(rules, GitHubEnvironments);
        foreach (string key in neutral.Keys.Concat(github.Keys.Where(k => !neutral.ContainsKey(k))))
        {
            JsonObject? declared = neutral.TryGetValue(key, out var n) ? n.Value : null;
            JsonObject? options = github.TryGetValue(key, out var g) ? g.Value : null;
            string name = declared is not null ? n.Name : g.Name;
            LiveObject? current = live.Objects.FirstOrDefault(o => o.MatchKey == MatchKey(key));
            bool disabled = declared?["enabled"] is JsonValue enabled && enabled.TryGetValue(out bool on) && !on;

            JsonObject native = current?.Native.DeepClone() as JsonObject ?? Empty(name);
            List<(string Type, long Id, string Name)> reviewers = [.. ReviewersOf(native)];
            if (disabled)
            {
                reviewers.Clear();
                native["wait_timer"] = 0;
                native["prevent_self_review"] = false;
                native["deployment_branch_policy"] = null;
            }
            else
            {
                if (declared?["reviewers"] is JsonArray teams)
                {
                    reviewers.RemoveAll(r => r.Type == "Team");
                    reviewers.AddRange(teams.Select(t => Team(Text(t) ?? string.Empty, name, owner, teamIds)));
                }

                if (declared?["waitMinutes"] is JsonValue wait)
                {
                    native["wait_timer"] = wait.GetValue<int>();
                }

                if (Text(declared?["branches"]) is { } branches)
                {
                    native["deployment_branch_policy"] = branches == "default"
                        ? new JsonObject { ["protected_branches"] = true, ["custom_branch_policies"] = false }
                        : null;
                }

                if (options?["users"] is JsonArray users)
                {
                    reviewers.RemoveAll(r => r.Type == "User");
                    reviewers.AddRange(users.Select(u => User(Text(u) ?? string.Empty, name, userIds)));
                }

                if (options?["preventSelfReview"] is JsonValue prevent)
                {
                    native["prevent_self_review"] = prevent.GetValue<bool>();
                }
            }

            if (reviewers.Count > PolicyValidator.GitHubMaxEnvironmentReviewers)
            {
                throw new PolicyMappingException(
                    DiagnosticCodes.InvalidEnvironment,
                    $"environments: '{name}' would have {reviewers.Count} reviewers ({string.Join(", ", reviewers.Select(r => r.Name))}), " +
                    $"and GitHub allows at most {PolicyValidator.GitHubMaxEnvironmentReviewers}, teams and users together.");
            }

            native["reviewers"] = Reviewers(reviewers);

            // What the environment will carry once written, read back the way a live one is.
            Dictionary<string, JsonNode?> read = ReadRules(native);
            var carried = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (declared is not null)
            {
                carried[Environments] = read[Environments];
            }

            if (options is not null)
            {
                carried[GitHubEnvironments] = read[GitHubEnvironments];
            }

            // A disabled environment that doesn't exist stays that way: environments are created only to protect them.
            JsonObject? payload = current is null && disabled ? null : Payload(native);
            yield return new DesiredObject(MatchKey(key), CreatePrefix + name, carried, payload, $"environment {name} (new)");
        }
    }

    /// <summary>The body of <c>PUT /repos/{owner}/{repo}/environments/{name}</c> for an environment in its native shape.</summary>
    public static JsonObject Payload(JsonObject native) => new()
    {
        ["wait_timer"] = native["wait_timer"]!.GetValue<int>(),
        ["prevent_self_review"] = native["prevent_self_review"]!.GetValue<bool>(),
        ["reviewers"] = new JsonArray([.. ReviewersOf(native).Select(r => (JsonNode)new JsonObject { ["type"] = r.Type, ["id"] = r.Id })]),
        ["deployment_branch_policy"] = native["deployment_branch_policy"]?.DeepClone(),
    };

    private static JsonObject Empty(string name) => new()
    {
        ["name"] = name,
        ["wait_timer"] = 0,
        ["prevent_self_review"] = false,
        ["reviewers"] = new JsonArray(),
        ["deployment_branch_policy"] = null,
    };

    private static (string Type, long Id, string Name) Team(string slug, string environment, GitHubAccount owner, IReadOnlyDictionary<string, long>? teamIds)
    {
        string key = slug.ToLowerInvariant();
        if (!owner.MayHaveTeams)
        {
            throw new PolicyMappingException(
                DiagnosticCodes.PrincipalNotFound,
                $"environments: '{environment}' names team '{slug}', but {owner.Login} is a user account and only organizations have teams. Name users in github.environments.{environment}.users instead.");
        }

        if (teamIds is null)
        {
            throw new PolicyMappingException(
                DiagnosticCodes.PrincipalNotFound,
                $"environments: the token may not list {owner.Login}'s teams, so team '{slug}' can't be resolved. It needs Members: read on the organization (read:org for a classic token).");
        }

        return teamIds.TryGetValue(key, out long id)
            ? ("Team", id, key)
            : throw new PolicyMappingException(
                DiagnosticCodes.PrincipalNotFound,
                $"environments: '{environment}' names team '{slug}', and there is no such team in {owner.Login}. Use the team's slug, as in github.com/orgs/{owner.Login}/teams/<slug>.");
    }

    private static (string Type, long Id, string Name) User(string login, string environment, IReadOnlyDictionary<string, long> userIds)
    {
        string key = login.ToLowerInvariant();
        return userIds.TryGetValue(key, out long id)
            ? ("User", id, key)
            : throw new PolicyMappingException(
                DiagnosticCodes.PrincipalNotFound,
                $"github.environments: '{environment}' names user '{login}', and GitHub has no such user.");
    }

    private static IEnumerable<(string Type, long Id, string Name)> ReviewersOf(JsonObject native) =>
        (native["reviewers"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => (Text(r["type"]) ?? "User", Number(r["id"]), Text(r["name"]) ?? string.Empty));

    private static JsonArray Reviewers(IEnumerable<(string Type, long Id, string Name)> reviewers) =>
        new([.. reviewers
            .DistinctBy(r => (r.Type, r.Name))
            .OrderBy(r => r.Type, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => (JsonNode)new JsonObject { ["type"] = r.Type, ["id"] = r.Id, ["name"] = r.Name })]);

    // The neutral branches value: any branch, protected branches only, or GitHub's named branch and tag patterns.
    private static string Branches(JsonNode? policy) => policy switch
    {
        JsonObject p when p["custom_branch_policies"] is JsonValue c && c.TryGetValue(out bool custom) && custom => "custom",
        JsonObject p when p["protected_branches"] is JsonValue b && b.TryGetValue(out bool protectedOnly) && protectedOnly => "default",
        _ => "any",
    };

    private static Dictionary<string, (string Name, JsonObject Value)> Entries(IReadOnlyDictionary<string, DesiredRule> rules, string rule)
    {
        var entries = new Dictionary<string, (string, JsonObject)>(StringComparer.Ordinal);
        if (rules.GetValueOrDefault(rule)?.Value is JsonObject map)
        {
            foreach ((string name, JsonNode? value) in map)
            {
                if (value is JsonObject entry)
                {
                    entries[name.ToLowerInvariant()] = (name, entry);
                }
            }
        }

        return entries;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static long Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out long number) ? number : 0;
}
