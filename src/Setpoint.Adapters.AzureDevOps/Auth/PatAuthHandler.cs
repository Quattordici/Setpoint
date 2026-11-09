using System.Net.Http.Headers;
using System.Text;

namespace Setpoint.Adapters.AzureDevOps.Auth;

/// <summary>Signs every request with a personal access token, as Basic auth with an empty user name.</summary>
internal sealed class PatAuthHandler(string token) : DelegatingHandler
{
    private readonly AuthenticationHeaderValue _header = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = _header;
        return base.SendAsync(request, cancellationToken);
    }
}
