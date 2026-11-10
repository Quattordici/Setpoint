using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Setpoint.Adapters.GitHub.Auth;
using Setpoint.Http;

namespace Setpoint.Adapters.GitHub.Api;

/// <summary>
/// The thin, typed wrapper over one target's GitHub REST API. It only knows endpoints and paging; what the answers
/// mean for Setpoint is the adapter's business. Every request pins the API version.
/// </summary>
internal sealed class GitHubApiClient(HttpClient http) : IDisposable
{
    /// <summary>The REST API version every call asks for.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>The header GitHub Enterprise Server adds with its version. github.com doesn't send it.</summary>
    public const string EnterpriseVersionHeader = "X-GitHub-Enterprise-Version";

    private const int PageSize = 100;

    /// <summary>The signed-in account, and the GitHub Enterprise Server version when the target is one.</summary>
    public async Task<(GitHubUser User, string? EnterpriseVersion)> GetUserAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync("user", cancellationToken).ConfigureAwait(false);
        GitHubUser user = await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.GitHubUser, cancellationToken).ConfigureAwait(false);
        string? version = response.Headers.TryGetValues(EnterpriseVersionHeader, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
        return (user, version);
    }

    /// <summary>
    /// The repos one account owns. An organization lists all its repos the token can see; for a user, only the
    /// signed-in user's own listing includes private repos.
    /// </summary>
    public async IAsyncEnumerable<GitHubRepository> ListOwnerReposAsync(string owner, bool isSignedInUser, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string first = isSignedInUser
            ? $"user/repos?affiliation=owner&per_page={PageSize}"
            : $"orgs/{Segment(owner)}/repos?type=all&per_page={PageSize}";

        bool isOrganization = true;
        if (!isSignedInUser)
        {
            using HttpResponseMessage probe = await SendAsync($"orgs/{Segment(owner)}", cancellationToken).ConfigureAwait(false);
            isOrganization = probe.StatusCode != HttpStatusCode.NotFound;
            if (isOrganization)
            {
                await HttpApiException.ThrowIfFailedAsync(probe, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!isOrganization)
        {
            first = $"users/{Segment(owner)}/repos?type=owner&per_page={PageSize}";
        }

        await foreach (GitHubRepository repo in PagesAsync(first, GitHubApiJsonContext.Default.ListGitHubRepository, cancellationToken).ConfigureAwait(false))
        {
            yield return repo;
        }
    }

    /// <summary>Every repo the signed-in account can reach, for scopes whose owner part is a wildcard.</summary>
    public IAsyncEnumerable<GitHubRepository> ListAccessibleReposAsync(CancellationToken cancellationToken) =>
        PagesAsync($"user/repos?affiliation=owner,collaborator,organization_member&per_page={PageSize}", GitHubApiJsonContext.Default.ListGitHubRepository, cancellationToken);

    /// <summary>One repo, with its merge settings when the token may see them.</summary>
    public async Task<GitHubRepository> GetRepoAsync(string owner, string name, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(RepoPath(owner, name), cancellationToken).ConfigureAwait(false);
        return await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.GitHubRepository, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the repo has at least one branch, which tells an empty repo from a tiny one.</summary>
    public async Task<bool> HasBranchesAsync(string owner, string name, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync($"{RepoPath(owner, name)}/branches?per_page=1", cancellationToken).ConfigureAwait(false);
        List<GitHubBranch> branches = await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.ListGitHubBranch, cancellationToken).ConfigureAwait(false);
        return branches.Count > 0;
    }

    /// <summary>
    /// The rulesets that apply to the repo, including its organization's, or null when rulesets aren't available
    /// on the repo's plan (a private repo on GitHub Free answers 403 and asks to upgrade).
    /// </summary>
    public async Task<List<GitHubRulesetSummary>?> ListRulesetsAsync(string owner, string name, CancellationToken cancellationToken)
    {
        string? next = $"{RepoPath(owner, name)}/rulesets?includes_parents=true&per_page={PageSize}";
        var rulesets = new List<GitHubRulesetSummary>();
        while (next is not null)
        {
            using HttpResponseMessage response = await SendAsync(next, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && await IsPlanLimitAsync(response, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            rulesets.AddRange(await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.ListGitHubRulesetSummary, cancellationToken).ConfigureAwait(false));
            next = NextPage(response);
        }

        return rulesets;
    }

    /// <summary>One repo ruleset in full, exactly as GitHub returns it.</summary>
    public async Task<JsonObject> GetRulesetAsync(string owner, string name, long id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync($"{RepoPath(owner, name)}/rulesets/{id}", cancellationToken).ConfigureAwait(false);
        return await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.JsonObject, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every active rule that applies to the branch, from any ruleset, with the ruleset it comes from.</summary>
    public async Task<List<GitHubBranchRule>> GetBranchRulesAsync(string owner, string name, string branch, CancellationToken cancellationToken)
    {
        string branchPath = string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
        var rules = new List<GitHubBranchRule>();
        await foreach (GitHubBranchRule rule in PagesAsync($"{RepoPath(owner, name)}/rules/branches/{branchPath}?per_page={PageSize}", GitHubApiJsonContext.Default.ListGitHubBranchRule, cancellationToken).ConfigureAwait(false))
        {
            rules.Add(rule);
        }

        return rules;
    }

    /// <summary>
    /// The teams with access to a repo, with their permission, or null when the token may not list them (403) or the
    /// owner has no teams (404).
    /// </summary>
    public Task<List<GitHubTeam>?> ListRepoTeamsAsync(string owner, string name, CancellationToken cancellationToken) =>
        TryPagesAsync($"{RepoPath(owner, name)}/teams?per_page={PageSize}", cancellationToken);

    /// <summary>Every team in an organization, or null when the token may not list them (403, 404).</summary>
    public Task<List<GitHubTeam>?> ListOrgTeamsAsync(string org, CancellationToken cancellationToken) =>
        TryPagesAsync($"orgs/{Segment(org)}/teams?per_page={PageSize}", cancellationToken);

    /// <summary>Gives a team a permission on a repo, adding the repo to the team if needed. The payload is sent as given.</summary>
    public Task<HttpResponseMessage> SetTeamRepoAsync(string org, string slug, string owner, string name, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, TeamRepoPath(org, slug, owner, name), payload, cancellationToken);

    /// <summary>Removes a repo from a team, which takes the team's access to it away.</summary>
    public Task<HttpResponseMessage> RemoveTeamRepoAsync(string org, string slug, string owner, string name, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, TeamRepoPath(org, slug, owner, name), null, cancellationToken);

    /// <summary>Creates a repo ruleset from the payload, exactly as given.</summary>
    public Task<HttpResponseMessage> CreateRulesetAsync(string owner, string name, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"{RepoPath(owner, name)}/rulesets", payload, cancellationToken);

    /// <summary>Replaces a repo ruleset with the payload, exactly as given.</summary>
    public Task<HttpResponseMessage> UpdateRulesetAsync(string owner, string name, long id, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, $"{RepoPath(owner, name)}/rulesets/{id}", payload, cancellationToken);

    /// <summary>Deletes a repo ruleset.</summary>
    public Task<HttpResponseMessage> DeleteRulesetAsync(string owner, string name, long id, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, $"{RepoPath(owner, name)}/rulesets/{id}", null, cancellationToken);

    /// <summary>The App, as itself.</summary>
    public async Task<(GitHubApp App, string? EnterpriseVersion)> GetAppAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "app", null, null, asApp: true, cancellationToken).ConfigureAwait(false);
        GitHubApp app = await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.GitHubApp, cancellationToken).ConfigureAwait(false);
        string? version = response.Headers.TryGetValues(EnterpriseVersionHeader, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
        return (app, version);
    }

    /// <summary>The App's installation on an organization or user, or null when it isn't installed there.</summary>
    public async Task<GitHubInstallation?> FindInstallationAsync(string owner, CancellationToken cancellationToken)
    {
        foreach (string path in new[] { $"orgs/{Segment(owner)}/installation", $"users/{Segment(owner)}/installation" })
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null, null, asApp: true, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                return await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.GitHubInstallation, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>Every installation of the App, for scopes whose owner part is a wildcard.</summary>
    public async Task<List<GitHubInstallation>> ListInstallationsAsync(CancellationToken cancellationToken)
    {
        var installations = new List<GitHubInstallation>();
        string? next = $"app/installations?per_page={PageSize}";
        while (next is not null)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, next, null, null, asApp: true, cancellationToken).ConfigureAwait(false);
            installations.AddRange(await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.ListGitHubInstallation, cancellationToken).ConfigureAwait(false));
            next = NextPage(response);
        }

        return installations;
    }

    /// <summary>Mints an installation token, valid for an hour.</summary>
    public async Task<GitHubInstallationToken> CreateInstallationTokenAsync(long installationId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", null, null, asApp: true, cancellationToken).ConfigureAwait(false);
        return await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.GitHubInstallationToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The repos an installation can reach, signed with that owner's installation token.</summary>
    public async IAsyncEnumerable<GitHubRepository> ListInstallationReposAsync(string owner, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = $"installation/repositories?per_page={PageSize}";
        while (next is not null)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, next, null, owner, asApp: false, cancellationToken).ConfigureAwait(false);
            GitHubInstallationRepositories page = await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.GitHubInstallationRepositories, cancellationToken).ConfigureAwait(false);
            foreach (GitHubRepository repo in page.Repositories)
            {
                yield return repo;
            }

            next = NextPage(response);
        }
    }

    // The owner a repo, organization or user call is about, so App auth signs it with that owner's installation.
    // Next-page links are absolute, so the owner is found by its place after repos/, orgs/ or users/ in the path.
    private static string? OwnerOf(string uri)
    {
        string path = Uri.TryCreate(uri, UriKind.Absolute, out Uri? absolute) ? absolute.AbsolutePath : uri.Split('?')[0];
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.FindIndex(parts, p => p is "repos" or "orgs" or "users");
        return at >= 0 && at + 1 < parts.Length ? Uri.UnescapeDataString(parts[at + 1]) : null;
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    private static string RepoPath(string owner, string name) => $"repos/{Segment(owner)}/{Segment(name)}";

    private static string TeamRepoPath(string org, string slug, string owner, string name) =>
        $"orgs/{Segment(org)}/teams/{Segment(slug)}/{RepoPath(owner, name)}";

    private async Task<List<GitHubTeam>?> TryPagesAsync(string first, CancellationToken cancellationToken)
    {
        var teams = new List<GitHubTeam>();
        string? next = first;
        while (next is not null)
        {
            using HttpResponseMessage response = await SendAsync(next, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                return null;
            }

            teams.AddRange(await HttpJson.ReadAsync(response, GitHubApiJsonContext.Default.ListGitHubTeam, cancellationToken).ConfigureAwait(false));
            next = NextPage(response);
        }

        return teams;
    }

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private Task<HttpResponseMessage> SendAsync(string uri, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, uri, null, cancellationToken);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, JsonObject? body, CancellationToken cancellationToken) =>
        SendAsync(method, uri, body, OwnerOf(uri), asApp: false, cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, JsonObject? body, string? owner, bool asApp, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (owner is not null)
        {
            request.Options.Set(GitHubCredentials.Owner, owner);
        }

        if (asApp)
        {
            request.Options.Set(GitHubCredentials.AsApp, true);
        }

        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        if (body is not null)
        {
            request.Content = HttpJson.Content(body, GitHubApiJsonContext.Default.JsonObject);
        }

        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // Follows the Link header's rel="next" until there are no more pages.
    private async IAsyncEnumerable<T> PagesAsync<T>(string first, JsonTypeInfo<List<T>> typeInfo, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = first;
        while (next is not null)
        {
            using HttpResponseMessage response = await SendAsync(next, cancellationToken).ConfigureAwait(false);
            List<T> page = await HttpJson.ReadAsync(response, typeInfo, cancellationToken).ConfigureAwait(false);
            foreach (T item in page)
            {
                yield return item;
            }

            next = NextPage(response);
        }
    }

    internal static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? links))
        {
            return null;
        }

        foreach (string part in links.SelectMany(l => l.Split(',')))
        {
            string[] pieces = part.Split(';', StringSplitOptions.TrimEntries);
            if (pieces.Length >= 2 && pieces.Skip(1).Any(p => p.Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
            {
                return pieces[0].Trim('<', '>');
            }
        }

        return null;
    }

    private static async Task<bool> IsPlanLimitAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? message = JsonNode.Parse(body)?["message"]?.GetValue<string>();
            return message is not null
                && (message.Contains("Upgrade to GitHub Pro", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("make this repository public", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
