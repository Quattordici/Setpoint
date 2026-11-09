using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Api;
using Setpoint.Adapters.AzureDevOps.Auth;
using Setpoint.Adapters.AzureDevOps.Policies;
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
/// This build reads and plans. Writing branch policies comes next, so apply writes nothing yet.
/// </remarks>
public sealed class AzureDevOpsAdapter : IProviderAdapter, IDisposable
{
    private readonly AzureDevOpsAdapterOptions _options;
    private readonly SecretRedactor _redactor = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RepoRef, AdoRepository> _listed = new();
    private readonly ConcurrentDictionary<RepoRef, RepoContext> _contexts = new();
    private readonly ConcurrentDictionary<(string Target, string Project), Lazy<Task<ProjectState>>> _projects = new();

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
        SignIn signIn = SignInFor(target, url);
        HttpClient Client(Uri root) => TargetHttpClientFactory.Create(
            target.Name,
            root,
            _options.Http,
            _options.Logger,
            _redactor,
            signIn.Handlers(),
            signIn.PrimaryHandler(),
            _options.Time);

        var api = new AzureDevOpsApiClient(Client(AzureDevOpsEndpoint.ApiRoot(url)));
        AzureDevOpsApiClient? identities = null;
        try
        {
            JsonObject connection = await api.GetConnectionDataAsync(cancellationToken).ConfigureAwait(false);
            string identity = AzureDevOpsEndpoint.Identity(connection)
                ?? throw new InvalidOperationException("Azure DevOps signed Setpoint in anonymously, so it didn't accept the token");

            string detected;
            bool services = AzureDevOpsEndpoint.IsServices(connection, url);
            if (services)
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

            Uri identityRoot = AzureDevOpsEndpoint.IdentityRoot(url, services);
            identities = identityRoot == AzureDevOpsEndpoint.ApiRoot(url)
                ? null
                : new AzureDevOpsApiClient(Client(identityRoot))
                {
                    ApiVersion = api.ApiVersion,
                };

            if (_sessions.TryRemove(target.Name, out Session? previous))
            {
                previous.Dispose();
            }

            _sessions[target.Name] = new Session(api, identities, signIn.Owned);
            return new TargetInfo(detected, api.ApiVersion, identity);
        }
        catch
        {
            api.Dispose();
            identities?.Dispose();
            signIn.Owned?.Dispose();
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

    /// <summary>Every rule can be managed on a listed repo; repos that can't be were skipped while listing.</summary>
    public Task<RepoCapabilities> GetCapabilitiesAsync(RepoRef repo, CancellationToken cancellationToken) => Task.FromResult(RepoCapabilities.Full);

    /// <summary>
    /// Reads the policy configurations that apply to the repo's default branch, and the Force push entries on it.
    /// A project's policies, pipelines and Project Valid Users group are read once per run and shared by its repos.
    /// </summary>
    public async Task<LiveSnapshot> ReadAsync(RepoRef repo, CancellationToken cancellationToken)
    {
        Session session = SessionFor(repo.Target);
        AdoRepository listed = Listed(repo) ?? throw new InvalidOperationException($"The repo '{repo}' wasn't listed in this run.");
        ProjectState project = await _projects
            .GetOrAdd((repo.Target, listed.Project.Id), key => new Lazy<Task<ProjectState>>(() => ReadProjectAsync(session, listed.Project, cancellationToken)))
            .Value.ConfigureAwait(false);

        var context = new RepoContext(listed, project.Pipelines, project.ValidUsersDescriptor);
        JsonObject? acl = await session.Api.GetAccessControlListAsync(ForcePushAcl.NamespaceId, context.BranchToken, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> names = await IdentityNamesAsync(session, acl, cancellationToken).ConfigureAwait(false);

        _contexts[repo] = context;
        return LiveReader.Read(context, project.Configurations, acl, d => names.GetValueOrDefault(d) ?? d);
    }

    /// <summary>Maps the desired state to policy configurations and the Force push entry. It makes no calls.</summary>
    /// <exception cref="PolicyMappingException">A required check names no pipeline in the project, or more than one (SP0206).</exception>
    public IReadOnlyList<DesiredObject> MapObjects(DesiredState desired, LiveSnapshot live) =>
        ObjectMapper.Map(desired, live, _contexts.TryGetValue(desired.Repo, out RepoContext? context)
            ? context
            : throw new InvalidOperationException($"The repo '{desired.Repo}' hasn't been read yet."));

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

    private static async Task<ProjectState> ReadProjectAsync(Session session, AdoProject project, CancellationToken cancellationToken)
    {
        List<JsonObject> configurations = await session.Api.ListPolicyConfigurationsAsync(project.Id, cancellationToken).ConfigureAwait(false);
        List<AdoBuildDefinition> pipelines = await session.Api.ListBuildDefinitionsAsync(project.Id, cancellationToken).ConfigureAwait(false);
        List<AdoIdentity>? validUsers = await session.IdentityApi.SearchIdentitiesAsync($"[{project.Name}]\\Project Valid Users", cancellationToken).ConfigureAwait(false);
        return new ProjectState(configurations, pipelines, validUsers?.FirstOrDefault()?.Descriptor);
    }

    private static async Task<Dictionary<string, string>> IdentityNamesAsync(Session session, JsonObject? acl, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] descriptors = acl is null ? [] : [.. ForcePushAcl.Entries(acl).Select(e => e.Descriptor)];
        foreach (AdoIdentity identity in await session.IdentityApi.GetIdentitiesAsync(descriptors, cancellationToken).ConfigureAwait(false) ?? [])
        {
            if (identity.Descriptor is { } descriptor && (identity.CustomDisplayName ?? identity.ProviderDisplayName) is { } name)
            {
                names[descriptor] = name;
            }
        }

        return names;
    }

    // How requests to the target are signed. Each client gets its own handlers; a token source they share is
    // owned by the session. Services or Server is decided from the URL here, as validate decides it, because the
    // sign-in has to exist before the connection data that says so for certain.
    private SignIn SignInFor(Target target, Uri url)
    {
        AuthMethod auth = target.Definition.Auth ?? AuthMethod.Pat;
        bool services = AzureDevOpsEndpoint.IsServices([], url);
        switch (auth)
        {
            case AuthMethod.Pat:
                string pat = Secret(TargetCredentials.TokenVariable(target), "token");
                return new SignIn(() => [new PatAuthHandler(pat)]);

            case AuthMethod.AccessToken:
                string jobToken = Secret(
                    TargetCredentials.AccessTokenVariable,
                    "pipeline job token (map it in the pipeline step with env: SYSTEM_ACCESSTOKEN: $(System.AccessToken))");
                return new SignIn(() => [new BearerAuthHandler(_ => ValueTask.FromResult(jobToken))]);

            case AuthMethod.Integrated when services:
                throw new NotSupportedException("auth: integrated (Windows auth) works on Azure DevOps Server only; use pat, accessToken or entra");

            case AuthMethod.Integrated:
                return new SignIn(() => [], _options.CreateIntegratedHandler ?? WindowsAuthHandler);

            case AuthMethod.Entra when !services:
                throw new NotSupportedException("auth: entra works on Azure DevOps Services only; use pat, accessToken or integrated");

            case AuthMethod.Entra:
                var source = new EntraTokenSource(
                    _options.CreateEntraCredential?.Invoke() ?? EntraTokenSource.Default(_options.GetEnvironmentVariable),
                    _options.Time,
                    _redactor.Add);
                return new SignIn(() => [new BearerAuthHandler(source.GetAsync)], Owned: source);

            default:
                throw new NotSupportedException($"auth: {Name(auth)} isn't available on Azure DevOps; use pat, accessToken, integrated or entra");
        }
    }

    // Windows auth: the default handler, answering the server's Negotiate or NTLM challenge as the account Setpoint
    // runs as. No credential is read from anywhere, and none is logged.
    private static HttpMessageHandler WindowsAuthHandler() => new SocketsHttpHandler
    {
        Credentials = System.Net.CredentialCache.DefaultNetworkCredentials,
        PreAuthenticate = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    };

    /// <summary>How one target's requests are signed.</summary>
    /// <param name="Handlers">Creates the signing handlers for one client.</param>
    /// <param name="Primary">Creates the innermost handler, or null for the platform default.</param>
    /// <param name="Owned">What the session disposes, such as a token source its clients share.</param>
    private sealed record SignIn(Func<DelegatingHandler[]> Handlers, Func<HttpMessageHandler>? Primary = null, IDisposable? Owned = null)
    {
        public HttpMessageHandler? PrimaryHandler() => Primary?.Invoke();
    }

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

    // Identities live on another host on Services; on Server the collection serves them.
    private sealed record Session(AzureDevOpsApiClient Api, AzureDevOpsApiClient? Identities, IDisposable? Credentials) : IDisposable
    {
        public AzureDevOpsApiClient IdentityApi => Identities ?? Api;

        public void Dispose()
        {
            Api.Dispose();
            Identities?.Dispose();
            Credentials?.Dispose();
        }
    }

    private sealed record ProjectState(IReadOnlyList<JsonObject> Configurations, IReadOnlyList<AdoBuildDefinition> Pipelines, string? ValidUsersDescriptor);
}
