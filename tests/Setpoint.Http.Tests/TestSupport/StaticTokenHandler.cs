using System.Net.Http.Headers;

namespace Setpoint.Http.Tests.TestSupport;

/// <summary>Stands in for an adapter's authentication handler: adds a bearer token to every request.</summary>
internal sealed class StaticTokenHandler(string token) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}
