using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Tests.TestData;
using Setpoint.Adapters.GitHub.Tests.TestSupport;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Merging;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;
using Setpoint.Http.Tests.TestSupport;
using WireMock.Server;

namespace Setpoint.Adapters.GitHub.Tests;

/// <summary>
/// Tests for the <c>access</c> rule and <c>github.access</c> on <see cref="GitHubAdapter"/> against a stubbed GitHub
/// API: reading a repo's teams, diffing them against the policy with unlisted teams left alone, the SP0207 errors,
/// and writing a team's permission.
/// </summary>
public sealed class GitHubTeamAccessTests : IAsyncLifetime
{
    private const string Org = "acme";
    private const string TargetName = "ghe";

    private readonly string _token = "ghp_" + new Bogus.Randomizer().AlphaNumeric(36);
    private WireMockServer _server = null!;
    private GitHubApiStub _api = null!;
    private GitHubAdapter _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("github-teams", TestContext.Current.CancellationToken);
        _api = new GitHubApiStub(_server, _token);
        _sut = new GitHubAdapter(new GitHubAdapterOptions
        {
            GetEnvironmentVariable = name => name == "SETPOINT_GHE_TOKEN" ? _token : null,
            Http = new HttpCoreOptions { MaxRetries = 1, BaseDelay = TimeSpan.FromMilliseconds(1) },
        });
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        WireMockServerFactory.Dispose(_server);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given a repo where platform has push and legacy has admin, and a policy giving platform admin and payments write,
    /// when it is diffed,
    /// then platform is updated to admin, payments is created with push, and legacy is left alone as unmanaged.
    /// </summary>
    [Fact(DisplayName = "👍 Plans the named teams and leaves the others alone")]
    public async Task Diff_NamedTeams_UpdatesCreatesAndLeavesOthers()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoWithTeamsAsync(("platform", "push"), ("legacy", "admin"));
        DesiredState desired = Desired(repo, ("access", """{"platform":"admin","payments":"write"}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        diff.Objects.Select(o => $"{o.Key} {o.Operation} {o.Payload?.ToJsonString()}").ShouldBe(
        [
            """gh:team/platform Update {"permission":"admin"}""",
            """gh:new/team/payments Create {"permission":"push"}""",
        ]);
        diff.Changes.Select(c => $"{c.Rule}: {c.From} -> {c.To}").ShouldBe(["access.platform: write -> admin", "access: (none) -> payments write"]);
        diff.Unmanaged.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            u => u.Source.ShouldBe("gh:team/legacy"),
            u => u.Value.ShouldBe("legacy admin"));
    }

    /// <summary>
    /// Given a repo where platform has push and a policy saying platform: none,
    /// when it is diffed,
    /// then the repo is removed from platform.
    /// </summary>
    [Fact(DisplayName = "👍 none removes a team's access")]
    public async Task Diff_None_DeletesTeamAccess()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoWithTeamsAsync(("platform", "push"));
        DesiredState desired = Desired(repo, ("access", """{"platform":"none"}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        PlanObject removed = diff.Objects.ShouldHaveSingleItem();
        removed.Key.ShouldBe("gh:team/platform");
        removed.Operation.ShouldBe(ObjectOperation.Delete);
    }

    /// <summary>
    /// Given a team with maintain and a policy giving it write, and another with push and github.access giving it triage,
    /// when it is diffed,
    /// then both change: maintain isn't write, and the GitHub-only role is written as is.
    /// </summary>
    [Fact(DisplayName = "👍 Tells GitHub-only roles from neutral ones")]
    public async Task Diff_GitHubOnlyRoles_AreDistinct()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoWithTeamsAsync(("platform", "maintain"), ("support", "push"));
        DesiredState desired = Desired(repo, ("access", """{"platform":"write"}"""), ("github.access", """{"support":"triage"}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        diff.Objects.Select(o => $"{o.Key} {o.Payload?.ToJsonString()}").ShouldBe(
        [
            """gh:team/platform {"permission":"push"}""",
            """gh:team/support {"permission":"triage"}""",
        ]);
        diff.Changes.Select(c => $"{c.Rule}: {c.From} -> {c.To}").ShouldBe(["access.platform: maintain -> write", "github.access: (none) -> support triage"]);
    }

    /// <summary>
    /// Given teams that already have the roles the policy gives them,
    /// when it is diffed,
    /// then nothing changes.
    /// </summary>
    [Fact(DisplayName = "👍 Teams with their roles are in sync")]
    public async Task Diff_InSync_HasNoObjects()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoWithTeamsAsync(("platform", "admin"), ("support", "maintain"));
        DesiredState desired = Desired(repo, ("access", """{"Platform":"admin"}"""), ("github.access", """{"support":"maintain"}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        diff.Objects.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a policy naming a team the organization doesn't have,
    /// when the desired objects are mapped,
    /// then SP0207 names the missing team.
    /// </summary>
    [Fact(DisplayName = "👎 SP0207 for a team the organization doesn't have")]
    public async Task Map_UnknownTeam_FailsSp0207()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoWithTeamsAsync(("platform", "push"));
        DesiredState desired = Desired(repo, ("access", """{"plattform":"write"}"""));

        // Act
        PolicyMappingException failure = await Should.ThrowAsync<PolicyMappingException>(() => DiffAsync(desired));

        // Assert
        failure.Code.ShouldBe(DiagnosticCodes.PrincipalNotFound);
        failure.Message.ShouldContain("no team 'plattform' in acme");
    }

    /// <summary>
    /// Given a policy giving one team a role in access and in github.access,
    /// when the desired objects are mapped,
    /// then SP0207 says it can have only one.
    /// </summary>
    [Fact(DisplayName = "👎 SP0207 for a team in both access and github.access")]
    public async Task Map_TeamInBothMaps_FailsSp0207()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoWithTeamsAsync(("platform", "push"));
        DesiredState desired = Desired(repo, ("access", """{"platform":"write"}"""), ("github.access", """{"platform":"maintain"}"""));

        // Act
        PolicyMappingException failure = await Should.ThrowAsync<PolicyMappingException>(() => DiffAsync(desired));

        // Assert
        failure.Message.ShouldContain("in both access and github.access");
    }

    /// <summary>
    /// Given a repo owned by a user and a policy naming a team,
    /// when the desired objects are mapped,
    /// then SP0207 says users have no teams, without listing any.
    /// </summary>
    [Fact(DisplayName = "👎 SP0207 for teams on a repo owned by a user")]
    public async Task Map_UserOwnedRepo_FailsSp0207()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().OwnedByUser("mattias");
        _api.User("setpoint-bot").Repo(repo.Build()).Rulesets(repo.Owner, repo.Name).BranchRules(repo.Owner, repo.Name, "main");
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
        DesiredState desired = Desired(repo, ("access", """{"platform":"write"}"""));

        // Act
        PolicyMappingException failure = await Should.ThrowAsync<PolicyMappingException>(() => DiffAsync(desired));

        // Assert
        failure.Message.ShouldContain("mattias is a user account");
        _server.LogEntries.ShouldNotContain(e => e.RequestMessage!.Path.EndsWith("/teams", StringComparison.Ordinal));
    }

    /// <summary>
    /// Given a planned create of payments' access,
    /// when it is applied,
    /// then the planned payload is PUT to the organization's team repo path.
    /// </summary>
    [Fact(DisplayName = "👍 Writes a team's permission with the planned payload")]
    public async Task Apply_Create_PutsTeamRepo()
    {
        // Arrange
        _api.User("setpoint-bot").TeamRepoWrites(Org);
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
        var planned = new PlanObject("gh:new/team/payments", ObjectOperation.Create, Fingerprint.NothingMatches("gh:team/payments"), "gh:team/payments", new JsonObject { ["permission"] = "push" });

        // Act
        ApplyResult result = await _sut.ApplyAsync(new RepoRef(TargetName, Org, "payments-api"), new RepoChangeSet([planned]), TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.ShouldBeTrue();
        var write = _server.LogEntries.Select(e => e.RequestMessage!).Single(r => r.Method == "PUT");
        write.Path.ShouldBe($"{GitHubApiStub.Root}/orgs/acme/teams/payments/repos/acme/payments-api");
        write.Body.ShouldBe("""{"permission":"push"}""");
    }

    /// <summary>
    /// Given a planned delete of legacy's access,
    /// when it is applied,
    /// then the repo is removed from the team.
    /// </summary>
    [Fact(DisplayName = "👍 Removes a team's access")]
    public async Task Apply_Delete_RemovesTeamRepo()
    {
        // Arrange
        _api.User("setpoint-bot").TeamRepoWrites(Org);
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
        var planned = new PlanObject("gh:team/legacy", ObjectOperation.Delete, "sha256:abc", "gh:team/legacy");

        // Act
        ApplyResult result = await _sut.ApplyAsync(new RepoRef(TargetName, Org, "payments-api"), new RepoChangeSet([planned]), TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.ShouldBeTrue();
        _server.LogEntries.ShouldContain(e => e.RequestMessage!.Method == "DELETE" && e.RequestMessage.Path == $"{GitHubApiStub.Root}/orgs/acme/teams/legacy/repos/acme/payments-api");
    }

    /// <summary>
    /// Given a token that may not change team access, which GitHub answers with 403,
    /// when a team's access is applied,
    /// then the error names the permissions and the role it needs.
    /// </summary>
    [Fact(DisplayName = "👎 A 403 on team access names the permissions")]
    public async Task Apply_Forbidden_NamesPermissions()
    {
        // Arrange
        _api.User("setpoint-bot").TeamRepoWrites(Org, 403, """{"message":"Must have admin rights to Repository."}""");
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
        var planned = new PlanObject("gh:team/platform", ObjectOperation.Update, "sha256:abc", "gh:team/platform", new JsonObject { ["permission"] = "admin" });

        // Act
        ApplyResult result = await _sut.ApplyAsync(new RepoRef(TargetName, Org, "payments-api"), new RepoChangeSet([planned]), TestContext.Current.CancellationToken);

        // Assert
        string error = result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull();
        error.ShouldContain("403: Must have admin rights");
        error.ShouldContain("Members: read");
    }

    private async Task<GitHubRepoBuilder> RepoWithTeamsAsync(params (string Slug, string Permission)[] teams)
    {
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org);
        _api.User("setpoint-bot")
            .Repo(repo.Build())
            .Rulesets(Org, repo.Name)
            .BranchRules(Org, repo.Name, "main")
            .Teams(Org, repo.Name, teams)
            .OrgTeams(Org, "platform", "payments", "legacy", "support");
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
        return repo;
    }

    private async Task<RepoDiff> DiffAsync(DesiredState desired)
    {
        LiveSnapshot live = await _sut.ReadAsync(desired.Repo, TestContext.Current.CancellationToken);
        return RepoDiffer.Diff(desired, live, _sut.MapObjects(desired, live));
    }

    private Target Target() => new(TargetName, new TargetDefinition { Platform = Platform.GitHub, Url = _api.Url, Scope = ["*/*"] });

    private static DesiredState Desired(GitHubRepoBuilder repo, params (string Rule, string Json)[] rules)
    {
        var layer = new LayerRef(LayerKind.Baseline, null, SourceLocation.StartOf("baseline.yml"));
        return new DesiredState(
            new RepoRef(TargetName, repo.Owner, repo.Name),
            rules.ToDictionary(r => r.Rule, r => new DesiredRule(r.Rule, JsonNode.Parse(r.Json), layer, Locked: false)));
    }
}
