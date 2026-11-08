using System.Net.Http.Headers;

namespace Setpoint.Adapters.GitHub;

/// <summary>Signs every request with the target's token.</summary>
internal sealed class BearerTokenHandler(string token) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}
