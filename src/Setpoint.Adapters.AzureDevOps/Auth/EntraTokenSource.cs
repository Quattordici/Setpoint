using Azure.Core;

namespace Setpoint.Adapters.AzureDevOps.Auth;

/// <summary>
/// Access tokens for Azure DevOps from Microsoft Entra ID. One token serves every request to the target until five
/// minutes before it expires, and each one is registered with the redactor before it is used.
/// </summary>
internal sealed class EntraTokenSource(TokenCredential credential, TimeProvider time, Action<string> registerSecret) : IDisposable
{
    /// <summary>The Azure DevOps resource, as a scope.</summary>
    public const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    /// <summary>How long before expiry a token is replaced.</summary>
    public static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken? _current;

    /// <summary>
    /// The credential Setpoint signs in with, tried in order: workload identity federation (a pipeline's service
    /// connection), managed identity, then the Azure CLI's login. Nothing interactive.
    /// </summary>
    public static TokenCredential Default(Func<string, string?> environment)
    {
        Azure.Identity.ManagedIdentityId identity = environment("AZURE_CLIENT_ID") is { Length: > 0 } clientId
            ? Azure.Identity.ManagedIdentityId.FromUserAssignedClientId(clientId)
            : Azure.Identity.ManagedIdentityId.SystemAssigned;
        return new Azure.Identity.ChainedTokenCredential(
            new Azure.Identity.WorkloadIdentityCredential(),
            new Azure.Identity.ManagedIdentityCredential(identity),
            new Azure.Identity.AzureCliCredential());
    }

    /// <summary>A token that is valid for at least five more minutes.</summary>
    public async ValueTask<string> GetAsync(CancellationToken cancellationToken)
    {
        if (Fresh() is { } token)
        {
            return token;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh() is { } again)
            {
                return again;
            }

            AccessToken minted = await credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken).ConfigureAwait(false);
            registerSecret(minted.Token);
            _current = minted;
            return minted.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private string? Fresh() =>
        _current is { } token && token.ExpiresOn - RefreshBefore > time.GetUtcNow() ? token.Token : null;
}
