using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
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

    /// <summary>Creates a repo ruleset from the payload, exactly as given.</summary>
    public Task<HttpResponseMessage> CreateRulesetAsync(string owner, string name, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"{RepoPath(owner, name)}/rulesets", payload, cancellationToken);

    /// <summary>Replaces a repo ruleset with the payload, exactly as given.</summary>
    public Task<HttpResponseMessage> UpdateRulesetAsync(string owner, string name, long id, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, $"{RepoPath(owner, name)}/rulesets/{id}", payload, cancellationToken);

    /// <summary>Deletes a repo ruleset.</summary>
    public Task<HttpResponseMessage> DeleteRulesetAsync(string owner, string name, long id, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, $"{RepoPath(owner, name)}/rulesets/{id}", null, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    private static string RepoPath(string owner, string name) => $"repos/{Segment(owner)}/{Segment(name)}";

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private Task<HttpResponseMessage> SendAsync(string uri, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, uri, null, cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
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
