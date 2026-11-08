using System.Net.Http.Headers;

namespace Setpoint.Adapters.AzureDevOps.Auth;

/// <summary>Signs every request with a bearer token: the pipeline's job token, or an Entra ID access token.</summary>
internal sealed class BearerAuthHandler(Func<CancellationToken, ValueTask<string>> token) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(cancellationToken).ConfigureAwait(false));
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
