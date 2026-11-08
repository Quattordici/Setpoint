using System.Net.Http.Headers;
using Setpoint.Adapters.GitHub.Api;

namespace Setpoint.Adapters.GitHub.Auth;

/// <summary>How requests to one target are signed.</summary>
internal abstract class GitHubCredentials
{
    /// <summary>The request option naming the repo owner a request is for, so App auth can pick that owner's installation.</summary>
    public static readonly HttpRequestOptionsKey<string> Owner = new("Setpoint.GitHub.Owner");

    /// <summary>The request option marking a call the App makes as itself, signed with its JWT.</summary>
    public static readonly HttpRequestOptionsKey<bool> AsApp = new("Setpoint.GitHub.AsApp");

    /// <summary>The bearer token for the request.</summary>
    public abstract ValueTask<string> TokenForAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

/// <summary>A personal access token, used for every request.</summary>
internal sealed class PatCredentials(string token) : GitHubCredentials
{
    /// <inheritdoc />
    public override ValueTask<string> TokenForAsync(HttpRequestMessage request, CancellationToken cancellationToken) => ValueTask.FromResult(token);
}

/// <summary>Signs every request with the token its credentials give for it.</summary>
internal sealed class GitHubAuthHandler(GitHubCredentials credentials) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string token = await credentials.TokenForAsync(request, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A GitHub App: it signs its own calls with a JWT, and repo calls with an installation token for the repo's owner,
/// which it mints on first use and mints again before the hour GitHub gives it runs out.
/// </summary>
internal sealed class AppCredentials(long appId, System.Security.Cryptography.RSA key, TimeProvider time, Action<string> registerSecret) : GitHubCredentials, IDisposable
{
    private readonly TimeProvider _time = time;
    private readonly Action<string> _registerSecret = registerSecret;

    /// <summary>How long before expiry an installation token is replaced.</summary>
    public static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, Installation> _installations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _mint = new(1, 1);
    private GitHubApiClient? _api;

    /// <summary>The installations found, by owner.</summary>
    public IReadOnlyCollection<string> Owners => _installations.Keys;

    /// <summary>Gives the credentials the client to mint tokens with. The client signs those calls with the JWT.</summary>
    public void Attach(GitHubApiClient api) => _api = api;

    /// <summary>Records the installation that serves an owner.</summary>
    public void AddInstallation(string owner, long installationId) => _installations[owner] = new Installation(installationId);

    /// <inheritdoc />
    public override async ValueTask<string> TokenForAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(AsApp, out bool asApp) && asApp)
        {
            string jwt = GitHubAppJwt.Create(appId, key, _time.GetUtcNow());
            _registerSecret(jwt);
            return jwt;
        }

        Installation installation = request.Options.TryGetValue(Owner, out string? owner) && _installations.TryGetValue(owner, out Installation? found)
            ? found
            : _installations.Count == 1
                ? _installations.Values.First()
                : throw new InvalidOperationException(
                    owner is null
                        ? "a GitHub App request didn't say which installation it is for"
                        : $"the GitHub App isn't installed for '{owner}'");
        return await installation.TokenAsync(this, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        key.Dispose();
        _mint.Dispose();
    }

    private sealed class Installation(long id)
    {
        private string? _token;
        private DateTimeOffset _expires;

        public async ValueTask<string> TokenAsync(AppCredentials owner, CancellationToken cancellationToken)
        {
            if (_token is { } current && owner._time.GetUtcNow() < _expires - RefreshBefore)
            {
                return current;
            }

            await owner._mint.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_token is null || owner._time.GetUtcNow() >= _expires - RefreshBefore)
                {
                    GitHubApiClient api = owner._api ?? throw new InvalidOperationException("the GitHub App credentials have no client to mint tokens with");
                    GitHubInstallationToken minted = await api.CreateInstallationTokenAsync(id, cancellationToken).ConfigureAwait(false);
                    owner._registerSecret(minted.Token);
                    _token = minted.Token;
                    _expires = minted.ExpiresAt;
                }

                return _token;
            }
            finally
            {
                owner._mint.Release();
            }
        }
    }
}
