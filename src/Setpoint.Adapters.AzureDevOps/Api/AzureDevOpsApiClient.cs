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

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    /// <summary>A project or repo name as one path segment.</summary>
    internal static string Segment(string value) => Uri.EscapeDataString(value);

    private string Versioned(string path) => $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}api-version={ApiVersion}";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.ParseAdd("application/json");
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
