using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Setpoint.Http;

namespace Setpoint.Adapters.AzureDevOps.Api;

/// <summary>
/// The thin, typed wrapper over one Azure DevOps organization's or collection's REST API. It only knows endpoints;
/// what the answers mean for Setpoint is the adapter's business. Every REST call carries <c>api-version</c>.
/// </summary>
/// <remarks>
/// The two discovery calls are the exception and carry no version, because they run before Setpoint knows which
/// versions the server has: <c>_apis/connectionData</c> tells Services from Server, and <c>OPTIONS _apis/{area}</c>
/// is how Microsoft's own clients learn a server's versions. Pinning 7.0 on them would make a Server 2020 fail
/// with "version out of range" instead of being named and refused.
/// </remarks>
internal sealed class AzureDevOpsApiClient(HttpClient http) : IDisposable
{
    /// <summary>The REST API version Setpoint pins: Azure DevOps Server 2022's, which every newer server and Services accept.</summary>
    public const string PinnedApiVersion = "7.0";

    /// <summary>The API version calls carry. It is <see cref="PinnedApiVersion"/> unless an older server was allowed.</summary>
    public string ApiVersion { get; set; } = PinnedApiVersion;

    /// <summary>Who the token signs in as and whether the target is Services or Server, from <c>_apis/connectionData</c>.</summary>
    public async Task<JsonObject> GetConnectionDataAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "_apis/connectionData", cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, AzureDevOpsApiJsonContext.Default.JsonObject, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The resource locations of one API area, each with the versions the server has for it.</summary>
    public async Task<List<AdoResourceLocation>> GetAreaLocationsAsync(string area, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Options, $"_apis/{Uri.EscapeDataString(area)}", cancellationToken).ConfigureAwait(false);
        return (await ReadAsync(response, AzureDevOpsApiJsonContext.Default.AdoListAdoResourceLocation, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>The first team project in the collection, or null when it has none.</summary>
    public async Task<AdoProject?> GetFirstProjectAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, Versioned("_apis/projects?$top=1"), cancellationToken).ConfigureAwait(false);
        return (await ReadAsync(response, AzureDevOpsApiJsonContext.Default.AdoListAdoProject, cancellationToken).ConfigureAwait(false)).Value.FirstOrDefault();
    }

    /// <summary>The branch policy types the server has, seen from one project.</summary>
    public async Task<List<AdoPolicyType>> GetPolicyTypesAsync(string project, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, Versioned($"{Segment(project)}/_apis/policy/types"), cancellationToken).ConfigureAwait(false);
        return (await ReadAsync(response, AzureDevOpsApiJsonContext.Default.AdoListAdoPolicyType, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Every Git repo in the collection or organization, across all its projects.</summary>
    public async Task<List<AdoRepository>> ListRepositoriesAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, Versioned("_apis/git/repositories"), cancellationToken).ConfigureAwait(false);
        return (await ReadAsync(response, AzureDevOpsApiJsonContext.Default.AdoListAdoRepository, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Every branch policy configuration in a project, as Azure DevOps returns them. The project-wide listing is used
    /// rather than the per-repo Git one because it is a released resource at 7.0; the adapter works out which ones
    /// apply to a repo's default branch from their scopes, and reads each project once per run.
    /// </summary>
    public Task<List<JsonObject>> ListPolicyConfigurationsAsync(string projectId, CancellationToken cancellationToken) =>
        PagesAsync($"{Segment(projectId)}/_apis/policy/configurations", AzureDevOpsApiJsonContext.Default.AdoListJsonObject, cancellationToken);

    /// <summary>Every pipeline in a project, for resolving required check names to build definition ids.</summary>
    public Task<List<AdoBuildDefinition>> ListBuildDefinitionsAsync(string projectId, CancellationToken cancellationToken) =>
        PagesAsync($"{Segment(projectId)}/_apis/build/definitions", AzureDevOpsApiJsonContext.Default.AdoListAdoBuildDefinition, cancellationToken);

    /// <summary>
    /// The access control list on one security token, or null when the token may not read security, which needs
    /// more than a read-only PAT. An empty object means no list is set on the token.
    /// </summary>
    public async Task<JsonObject?> GetAccessControlListAsync(string namespaceId, string token, CancellationToken cancellationToken)
    {
        string path = Versioned($"_apis/accesscontrollists/{Segment(namespaceId)}?token={Uri.EscapeDataString(token)}&includeExtendedInfo=false&recurse=false");
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        List<JsonObject> lists = (await ReadAsync(response, AzureDevOpsApiJsonContext.Default.AdoListJsonObject, cancellationToken).ConfigureAwait(false)).Value;
        return lists.FirstOrDefault(l => string.Equals(l["token"]?.GetValue<string>(), token, StringComparison.OrdinalIgnoreCase)) ?? [];
    }

    /// <summary>The identities with these descriptors, or null when the token may not read identities.</summary>
    public async Task<List<AdoIdentity>?> GetIdentitiesAsync(IReadOnlyCollection<string> descriptors, CancellationToken cancellationToken)
    {
        if (descriptors.Count == 0)
        {
            return [];
        }

        string path = Versioned($"_apis/identities?descriptors={Uri.EscapeDataString(string.Join(',', descriptors))}&queryMembership=None");
        return await IdentitiesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The identities a general search finds, such as <c>[Payments]\Project Valid Users</c>, or null when the token may not read identities.</summary>
    public Task<List<AdoIdentity>?> SearchIdentitiesAsync(string filterValue, CancellationToken cancellationToken) =>
        IdentitiesAsync(Versioned($"_apis/identities?searchFilter=General&filterValue={Uri.EscapeDataString(filterValue)}&queryMembership=None"), cancellationToken);

    /// <summary>Creates a policy configuration in a project from the payload, exactly as given.</summary>
    public Task<HttpResponseMessage> CreatePolicyConfigurationAsync(string projectId, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, Versioned($"{Segment(projectId)}/_apis/policy/configurations"), payload, cancellationToken);

    /// <summary>Replaces a policy configuration with the payload, exactly as given.</summary>
    public Task<HttpResponseMessage> UpdatePolicyConfigurationAsync(string projectId, int id, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, Versioned($"{Segment(projectId)}/_apis/policy/configurations/{id}"), payload, cancellationToken);

    /// <summary>Deletes a policy configuration.</summary>
    public Task<HttpResponseMessage> DeletePolicyConfigurationAsync(string projectId, int id, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, Versioned($"{Segment(projectId)}/_apis/policy/configurations/{id}"), null, cancellationToken);

    /// <summary>
    /// Sets access control entries on one token: the body of Access Control Entries - Set, exactly as given. With
    /// <c>merge: false</c> each listed identity's entry is replaced and every other entry is left alone.
    /// </summary>
    public Task<HttpResponseMessage> SetAccessControlEntriesAsync(string namespaceId, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, Versioned($"_apis/accesscontrolentries/{Segment(namespaceId)}"), payload, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    /// <summary>The header Azure DevOps sends when a listing has another page.</summary>
    internal const string ContinuationHeader = "x-ms-continuationtoken";

    private async Task<List<AdoIdentity>?> IdentitiesAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        return (await ReadAsync(response, AzureDevOpsApiJsonContext.Default.AdoListAdoIdentity, cancellationToken).ConfigureAwait(false)).Value;
    }

    // Follows x-ms-continuationtoken until the listing has no more pages.
    private async Task<List<T>> PagesAsync<T>(string path, JsonTypeInfo<AdoList<T>> typeInfo, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? continuation = null;
        do
        {
            string page = continuation is null ? path : $"{path}?continuationToken={Uri.EscapeDataString(continuation)}";
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, Versioned(page), cancellationToken).ConfigureAwait(false);
            items.AddRange((await ReadAsync(response, typeInfo, cancellationToken).ConfigureAwait(false)).Value);
            continuation = response.Headers.TryGetValues(ContinuationHeader, out IEnumerable<string>? values)
                ? values.FirstOrDefault(v => v.Length > 0)
                : null;
        }
        while (continuation is not null);

        return items;
    }

    /// <summary>A project or repo name as one path segment.</summary>
    internal static string Segment(string value) => Uri.EscapeDataString(value);

    private string Versioned(string path) => $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}api-version={ApiVersion}";

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken) =>
        SendAsync(method, path, null, cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = HttpJson.Content(body, AzureDevOpsApiJsonContext.Default.JsonObject);
        }

        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // Azure DevOps answers a token it doesn't accept with 203 and its HTML sign-in page instead of 401, so a
    // non-JSON success is a failed sign-in, not a parse error.
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        bool isJson = response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.OrdinalIgnoreCase) ?? false;
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation || (response.IsSuccessStatusCode && !isJson))
        {
            throw new HttpApiException(
                response.RequestMessage?.Method,
                response.RequestMessage?.RequestUri,
                (int)response.StatusCode,
                "Azure DevOps answered with its sign-in page, so it didn't accept the token. Check that it is valid and hasn't expired.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HttpApiException(
                response.RequestMessage?.Method,
                response.RequestMessage?.RequestUri,
                (int)response.StatusCode,
                "Azure DevOps refused the token (401). Check that it is valid, hasn't expired and belongs to this organization or collection.");
        }

        return await HttpJson.ReadAsync(response, typeInfo, cancellationToken).ConfigureAwait(false);
    }
}
