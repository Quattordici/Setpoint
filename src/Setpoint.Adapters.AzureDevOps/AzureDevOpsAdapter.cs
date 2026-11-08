using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Api;
using Setpoint.Adapters.AzureDevOps.Auth;
using Setpoint.Adapters.AzureDevOps.Policy;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;

namespace Setpoint.Adapters.AzureDevOps;

/// <summary>
/// The Azure DevOps adapter, for Services and for Server 2022 and later. Setpoint finds out which one a target is,
/// and which release of Server, from the target itself; nothing about it is declared in targets.yml.
/// </summary>
/// <remarks>
/// This build has the preflight and the repo listing. Reading branch policies and writing them come next, so for
/// now every listed repo fails its read and nothing is ever written.
/// </remarks>
public sealed class AzureDevOpsAdapter : IProviderAdapter, IDisposable
{
    private readonly AzureDevOpsAdapterOptions _options;
    private readonly SecretRedactor _redactor = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RepoRef, AdoRepository> _listed = new();

    /// <summary>Creates the adapter.</summary>
    public AzureDevOpsAdapter(AzureDevOpsAdapterOptions? options = null) => _options = options ?? new AzureDevOpsAdapterOptions();

    /// <inheritdoc />
    public Platform Platform => Platform.AzureDevOps;

    /// <inheritdoc />
    public IProviderBlockDefinition? ProviderBlock => AzureDevOpsProviderBlockDefinition.Instance;

    /// <summary>
    /// The preflight: signs in and resolves the identity, tells Services from Server, refuses Server releases older
    /// than 2022 unless they are allowed, and checks that the server has every policy type Setpoint manages.
    /// </summary>
    public async Task<TargetInfo> DescribeAsync(Target target, CancellationToken cancellationToken)
    {
        Uri url = target.Definition.Url ?? throw new InvalidOperationException("an Azure DevOps target needs its organization or collection url");
        AuthMethod auth = target.Definition.Auth ?? AuthMethod.Pat;
        DelegatingHandler signer = auth switch
        {
            AuthMethod.Pat => new PatAuthHandler(Secret(TargetCredentials.TokenVariable(target), "token")),
            _ => throw new NotSupportedException($"auth: {Name(auth)} isn't available on Azure DevOps in this build yet; use pat"),
        };

        HttpClient http = TargetHttpClientFactory.Create(
            target.Name,
            AzureDevOpsEndpoint.ApiRoot(url),
            _options.Http,
            _options.Logger,
            _redactor,
            [signer],
            timeProvider: _options.Time);
        var api = new AzureDevOpsApiClient(http);
        try
        {
            JsonObject connection = await api.GetConnectionDataAsync(cancellationToken).ConfigureAwait(false);
            string identity = AzureDevOpsEndpoint.Identity(connection)
                ?? throw new InvalidOperationException("Azure DevOps signed Setpoint in anonymously, so it didn't accept the token");

            string detected;
            if (AzureDevOpsEndpoint.IsServices(connection, url))
            {
                detected = "Azure DevOps Services";
            }
            else
            {
                Version released = AzureDevOpsEndpoint.ReleasedVersion(await api.GetAreaLocationsAsync("policy", cancellationToken).ConfigureAwait(false))
                    ?? throw new InvalidOperationException("the server didn't say which REST versions its policy API has, so its release is unknown");
                detected = AzureDevOpsEndpoint.ServerName(released);
                if (released < AzureDevOpsEndpoint.Minimum)
                {
                    if (!_options.AllowUnsupportedServer)
                    {
                        throw new NotSupportedException(
                            $"it is {detected}, and Setpoint needs Azure DevOps Server 2022 or later; --allow-unsupported-server runs against it anyway");
                    }

                    api.ApiVersion = AzureDevOpsEndpoint.Format(released);
                    detected += " (unsupported)";
                }
            }

            await CheckPolicyTypesAsync(api, cancellationToken).ConfigureAwait(false);

            if (_sessions.TryRemove(target.Name, out Session? previous))
            {
                previous.Dispose();
            }

            _sessions[target.Name] = new Session(api);
            return new TargetInfo(detected, api.ApiVersion, identity);
        }
        catch
        {
            api.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lists every repo in the collection or organization that is in the target's scope. Disabled repos, repos in
    /// maintenance, empty repos and forks (unless <c>includeForks</c>) are listed as skipped.
    /// </summary>
    public async IAsyncEnumerable<ListedRepo> ListReposAsync(Target target, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Session session = SessionFor(target.Name);
        List<AdoRepository> repos = await session.Api.ListRepositoriesAsync(cancellationToken).ConfigureAwait(false);
        foreach (AdoRepository repo in repos.OrderBy(r => r.Project.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var reference = new RepoRef(target.Name, repo.Project.Name, repo.Name);
            if (!target.InScope(reference))
            {
                continue;
            }

            _listed[reference] = repo;
            yield return new ListedRepo(reference, SkipReason(target, repo));
        }
    }

    /// <inheritdoc />
    public Task<RepoCapabilities> GetCapabilitiesAsync(RepoRef repo, CancellationToken cancellationToken) => Task.FromResult(RepoCapabilities.Full);

    /// <inheritdoc />
    public Task<LiveSnapshot> ReadAsync(RepoRef repo, CancellationToken cancellationToken) =>
        throw new NotSupportedException("reading Azure DevOps branch policies isn't in this build of Setpoint yet");

    /// <inheritdoc />
    public IReadOnlyList<DesiredObject> MapObjects(DesiredState desired, LiveSnapshot live) => [];

    /// <summary>Writes nothing: this build can't write Azure DevOps branch policies yet, so every object is refused.</summary>
    public Task<ApplyResult> ApplyAsync(RepoRef repo, RepoChangeSet changes, CancellationToken cancellationToken) =>
        Task.FromResult(new ApplyResult([.. changes.Objects.Select(o =>
            new ObjectApplyOutcome(o.Key, false, "Writing Azure DevOps branch policies isn't in this build of Setpoint yet; nothing was written."))]));

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (Session session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>The listed repo, with the ids later calls need, or null when it wasn't listed in this run.</summary>
    internal AdoRepository? Listed(RepoRef repo) => _listed.GetValueOrDefault(repo);

    // The type ids are fixed, but the Status one comes from Microsoft's Terraform provider rather than the REST
    // reference, so the preflight asks the server. Types are listed per project; a collection with no projects
    // has nothing to manage, so there is nothing to check.
    private static async Task CheckPolicyTypesAsync(AzureDevOpsApiClient api, CancellationToken cancellationToken)
    {
        AdoProject? project = await api.GetFirstProjectAsync(cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return;
        }

        var known = (await api.GetPolicyTypesAsync(project.Id, cancellationToken).ConfigureAwait(false))
            .Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missing = [.. AzureDevOpsPolicyTypes.Required.Where(t => !known.Contains(t.Id)).Select(t => $"{t.Name} ({t.Id})")];
        if (missing.Length > 0)
        {
            throw new NotSupportedException($"the server lacks the branch policy types Setpoint manages: {string.Join(", ", missing)}");
        }
    }

    private static Diagnostic? SkipReason(Target target, AdoRepository repo)
    {
        if (repo.IsDisabled || repo.IsInMaintenance)
        {
            string state = repo.IsDisabled ? "disabled" : "in maintenance";
            return Diagnostic.Info(DiagnosticCodes.ArchivedRepoSkipped, $"The repo is {state}, so it is skipped.");
        }

        if (repo.IsFork && !target.Definition.IncludeForks)
        {
            return Diagnostic.Info(DiagnosticCodes.ForkSkipped, "The repo is a fork, so it is skipped. Set includeForks: true on the target to manage forks.");
        }

        return repo.DefaultBranch is null
            ? Diagnostic.Warning(DiagnosticCodes.EmptyRepoSkipped, "The repo is empty, so it has no default branch to protect and is skipped.")
            : null;
    }

    private static string Name(AuthMethod auth) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(auth.ToString());

    private Session SessionFor(string target) =>
        _sessions.TryGetValue(target, out Session? session)
            ? session
            : throw new InvalidOperationException($"Target '{target}' hasn't been described yet.");

    private string Secret(string variable, string what)
    {
        string value = _options.GetEnvironmentVariable(variable) is { Length: > 0 } set
            ? set
            : throw new InvalidOperationException($"the environment variable {variable} holding its {what} is not set");
        _redactor.Add(value);
        return value;
    }

    private sealed record Session(AzureDevOpsApiClient Api) : IDisposable
    {
        public void Dispose() => Api.Dispose();
    }
}
