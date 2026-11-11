using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Policies;
using Setpoint.Adapters.AzureDevOps.Tests.TestData;
using Setpoint.Adapters.AzureDevOps.Tests.TestSupport;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Merging;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;
using Setpoint.Http.Tests.TestSupport;
using WireMock.Server;

namespace Setpoint.Adapters.AzureDevOps.Tests;

/// <summary>
/// Tests for the <c>access</c> rule on <see cref="AzureDevOpsAdapter"/> against a stubbed Azure DevOps Server
/// collection: the repo token's entries read as roles, names resolved to identities, the role's allow bits written
/// with every other bit kept, permissions inherited from the project summarised, and entries on any other token
/// refused at apply.
/// </summary>
public sealed class AzureDevOpsAccessTests : IAsyncLifetime
{
    private const string ValidUsers = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-3";
    private const string Contributors = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-1";
    private const string Readers = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-2";

    private static readonly LayerRef s_baseline = new(LayerKind.Baseline, null, SourceLocation.StartOf("baseline.yml"));

    private readonly string _token = new Bogus.Randomizer().AlphaNumeric(52);
    private readonly string _projectId = Guid.NewGuid().ToString();
    private WireMockServer _server = null!;
    private AzureDevOpsApiStub _api = null!;
    private AzureDevOpsAdapter _sut = null!;
    private RepoRef _repo = null!;
    private string _repoId = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("ado-access", TestContext.Current.CancellationToken);
        _api = new AzureDevOpsApiStub(_server, _token);
        _api.ConnectionData("Setpoint Service", @"CORP\svc-setpoint").PolicyArea("7.0").Projects((_projectId, "Payments")).PolicyTypes(_projectId);
        _sut = new AzureDevOpsAdapter(new AzureDevOpsAdapterOptions
        {
            GetEnvironmentVariable = name => name == "SETPOINT_ADO_ONPREM_TOKEN" ? _token : null,
            Http = new HttpCoreOptions { MaxRetries = 1, BaseDelay = TimeSpan.FromMilliseconds(1) },
        });

        JsonObject listedRepo = new AdoRepoBuilder().InProject("Payments").WithName("payments-api").Build();
        listedRepo["project"]!["id"] = _projectId;
        _repoId = listedRepo["id"]!.GetValue<string>();
        _api.Repositories(listedRepo)
            .PolicyConfigurations(_projectId, [], [])
            .BuildDefinitions(_projectId)
            .ValidUsers("Payments", ValidUsers)
            .Acl(ForcePushAcl.BranchToken(_projectId, _repoId, "main"))
            .Identities((Contributors, @"[Payments]\Contributors"), (Readers, @"[Payments]\Readers"));

        var target = new Target("ado-onprem", new TargetDefinition { Platform = Platform.AzureDevOps, Url = _api.CollectionUrl });
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        await foreach (ListedRepo listed in _sut.ListReposAsync(target, TestContext.Current.CancellationToken))
        {
            _repo = listed.Repo;
        }
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        WireMockServerFactory.Dispose(_server);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given Contributors with the write bits plus Force push on the repo token, and Readers with only a deny,
    /// when the repo is read,
    /// then Contributors is one live object whose role is write, and Readers, which has no role bit, isn't read.
    /// </summary>
    [Fact(DisplayName = "👍 Reads the repo token's entries as roles")]
    public async Task Read_RepoTokenEntries_ReadAsRoles()
    {
        // Arrange
        _api.Acl(RepoToken, (Contributors, RepoAccessAcl.WriteBits | 8, 0), (Readers, 0, 4));

        // Act
        LiveSnapshot live = await _sut.ReadAsync(_repo, TestContext.Current.CancellationToken);

        // Assert
        LiveObject entry = live.Objects.ShouldHaveSingleItem();
        entry.MatchKey.ShouldBe(RepoAccessAcl.MatchKey(Contributors));
        entry.Rules["access"]!.ToJsonString().ShouldBe("""{"[Payments]\\Contributors":"write"}""");
    }

    /// <summary>
    /// Given Contributors with write, Force push (8) and a Bypass policies deny (128) on the repo token,
    /// when the policy gives Contributors admin,
    /// then the planned entry adds the admin bits and keeps Force push and the deny, on the repo token, and replaces only that entry.
    /// </summary>
    [Fact(DisplayName = "👍 Raises a group to admin and keeps the bits Setpoint doesn't own")]
    public async Task Map_Admin_SetsRoleBitsAndKeepsTheRest()
    {
        // Arrange
        _api.Acl(RepoToken, (Contributors, RepoAccessAcl.WriteBits | 8, 128))
            .Acl(RepoAccessAcl.ProjectToken(_projectId))
            .IdentitySearch(@"[Payments]\Contributors", (Contributors, @"[Payments]\Contributors"));

        // Act
        DesiredObject desired = (await MapAsync("""{"Contributors":"admin"}""")).ShouldHaveSingleItem();

        // Assert
        desired.MatchKey.ShouldBe(RepoAccessAcl.MatchKey(Contributors));
        JsonObject payload = desired.Payload.ShouldBeOfType<JsonObject>();
        payload["token"]!.GetValue<string>().ShouldBe(RepoToken);
        payload["merge"]!.GetValue<bool>().ShouldBeFalse();
        AccessControlEntry entry = ForcePushAcl.FromPayload(payload).ShouldNotBeNull();
        entry.Allow.ShouldBe(RepoAccessAcl.AdminBits | 8);
        entry.Deny.ShouldBe(128);
        desired.Rules["access"]!.ToJsonString().ShouldBe("""{"[Payments]\\Contributors":"admin"}""");
    }

    /// <summary>
    /// Given Contributors with write plus Force push on the repo token,
    /// when the policy gives Contributors none,
    /// then the entry is updated with every role bit cleared and Force push kept, rather than deleted.
    /// </summary>
    [Fact(DisplayName = "👍 none clears the role bits and keeps the rest of the entry")]
    public async Task Map_None_ClearsRoleBits()
    {
        // Arrange
        _api.Acl(RepoToken, (Contributors, RepoAccessAcl.WriteBits | 8, 0))
            .Acl(RepoAccessAcl.ProjectToken(_projectId))
            .IdentitySearch(@"[Payments]\Contributors", (Contributors, @"[Payments]\Contributors"));

        // Act
        DesiredObject desired = (await MapAsync("""{"Contributors":"none"}""")).ShouldHaveSingleItem();

        // Assert
        ForcePushAcl.FromPayload(desired.Payload.ShouldBeOfType<JsonObject>()).ShouldNotBeNull().Allow.ShouldBe(8);
        desired.Rules["access"].ShouldBeNull();
    }

    /// <summary>
    /// Given a name that no identity has,
    /// when the repo is prepared,
    /// then SP0207 names the project to look in.
    /// </summary>
    [Fact(DisplayName = "👎 A name no identity has is SP0207")]
    public async Task Prepare_UnknownName_ThrowsPrincipalNotFound()
    {
        // Arrange
        _api.Acl(RepoToken).IdentitySearch(@"[Payments]\Platfrom");

        // Act
        PolicyMappingException ex = await Should.ThrowAsync<PolicyMappingException>(() => MapAsync("""{"Platfrom":"write"}"""));

        // Assert
        ex.Code.ShouldBe(DiagnosticCodes.PrincipalNotFound);
        ex.Message.ShouldContain(@"no group or user is named '[Payments]\Platfrom'");
    }

    /// <summary>
    /// Given a name two identities match, neither exactly,
    /// when the repo is prepared,
    /// then SP0207 lists both and asks for the scoped name.
    /// </summary>
    [Fact(DisplayName = "👎 A name several identities match is SP0207")]
    public async Task Prepare_AmbiguousName_ThrowsPrincipalNotFound()
    {
        // Arrange
        _api.Acl(RepoToken).IdentitySearch(@"[Payments]\Release", (Contributors, @"[Payments]\Release Managers"), (Readers, @"[Payments]\Release Readers"));

        // Act
        PolicyMappingException ex = await Should.ThrowAsync<PolicyMappingException>(() => MapAsync("""{"Release":"write"}"""));

        // Assert
        ex.Code.ShouldBe(DiagnosticCodes.PrincipalNotFound);
        ex.Message.ShouldContain("2 identities match");
    }

    /// <summary>
    /// Given Contributors with admin on the project token,
    /// when the policy gives Contributors write on the repo,
    /// then the project's grant is an unmanaged setting, which the plan reports as granting more than the policy.
    /// </summary>
    [Fact(DisplayName = "👍 A role inherited from the project is summarised as unmanaged")]
    public async Task Prepare_ProjectGrant_IsUnmanaged()
    {
        // Arrange
        _api.Acl(RepoToken)
            .Acl(RepoAccessAcl.ProjectToken(_projectId), (Contributors, RepoAccessAcl.AdminBits, 0))
            .IdentitySearch(@"[Payments]\Contributors", (Contributors, @"[Payments]\Contributors"));
        LiveSnapshot read = await _sut.ReadAsync(_repo, TestContext.Current.CancellationToken);

        // Act
        LiveSnapshot live = await _sut.PrepareAsync(Desired("""{"Contributors":"write"}"""), read, TestContext.Current.CancellationToken);

        // Assert
        UnmanagedSetting inherited = live.Unmanaged.ShouldHaveSingleItem();
        inherited.Rule.ShouldBe("access");
        inherited.Source.ShouldBe("permissions inherited from project Payments");
        inherited.Value!.ToJsonString().ShouldBe("""{"Contributors":"admin"}""");
    }

    /// <summary>
    /// Given a planned access entry on the repo token,
    /// when it is applied,
    /// then it is set in the Git Repositories namespace exactly as planned.
    /// </summary>
    [Fact(DisplayName = "👍 Applies an access entry on the repo token")]
    public async Task Apply_AccessEntry_SetsIt()
    {
        // Arrange
        _api.AclWrites();
        JsonObject payload = ForcePushAcl.Payload(RepoToken, new AccessControlEntry(Contributors, RepoAccessAcl.AdminBits, 0));
        var planned = new PlanObject($"ado:acl/{RepoToken}/{Contributors}", ObjectOperation.Update, "sha256:abc", RepoAccessAcl.MatchKey(Contributors), payload);

        // Act
        ApplyResult result = await _sut.ApplyAsync(_repo, new RepoChangeSet([planned]), TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.ShouldBeTrue();
        WireMock.IRequestMessage write = Writes().ShouldHaveSingleItem();
        write.Path.ShouldBe($"{AzureDevOpsApiStub.Collection}/_apis/accesscontrolentries/{ForcePushAcl.NamespaceId}");
        JsonNode.DeepEquals(JsonNode.Parse(write.Body!), payload).ShouldBeTrue();
    }

    /// <summary>
    /// Given planned access entries on the project token, and for another identity than the match key names,
    /// when they are applied,
    /// then both are refused without any request.
    /// </summary>
    [Theory(DisplayName = "👎 Refuses an access entry on another token or for another identity")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Apply_AccessEntryElsewhere_IsRefusedWithoutWriting(bool projectToken)
    {
        // Arrange
        _api.AclWrites();
        string token = projectToken ? RepoAccessAcl.ProjectToken(_projectId) : RepoToken;
        string descriptor = projectToken ? Contributors : Readers;
        JsonObject payload = ForcePushAcl.Payload(token, new AccessControlEntry(descriptor, RepoAccessAcl.AdminBits, 0));
        var planned = new PlanObject($"ado:acl/{RepoToken}/{Contributors}", ObjectOperation.Update, "sha256:abc", RepoAccessAcl.MatchKey(Contributors), payload);

        // Act
        ApplyResult result = await _sut.ApplyAsync(_repo, new RepoChangeSet([planned]), TestContext.Current.CancellationToken);

        // Assert
        result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull().ShouldContain("isn't a permission entry on payments-api's repo token");
        Writes().ShouldBeEmpty();
    }

    private string RepoToken => RepoAccessAcl.RepoToken(_projectId, _repoId);

    private DesiredState Desired(string access) =>
        new(_repo, new Dictionary<string, DesiredRule>(StringComparer.Ordinal)
        {
            ["access"] = new("access", CanonicalJson.ForRule(JsonNode.Parse(access)), s_baseline, false),
        });

    private async Task<IReadOnlyList<DesiredObject>> MapAsync(string access)
    {
        DesiredState desired = Desired(access);
        LiveSnapshot read = await _sut.ReadAsync(_repo, TestContext.Current.CancellationToken);
        LiveSnapshot live = await _sut.PrepareAsync(desired, read, TestContext.Current.CancellationToken);
        return [.. _sut.MapObjects(desired, live).Where(o => RepoAccessAcl.IsMatchKey(o.MatchKey))];
    }

    private List<WireMock.IRequestMessage> Writes() =>
        [.. _server.LogEntries.Select(e => e.RequestMessage).OfType<WireMock.IRequestMessage>().Where(r => r.Method is not ("GET" or "OPTIONS"))];
}
