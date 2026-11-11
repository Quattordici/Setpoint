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
/// Tests for the <c>environments</c> rule and <c>github.environments</c> on <see cref="GitHubAdapter"/> against a
/// stubbed GitHub API: reading environments, creating a missing one, replacing only the declared fields, clearing
/// protection with <c>enabled: false</c>, leaving unnamed environments alone, the SP0207 and SP0121 errors, private
/// repos, and writing an environment.
/// </summary>
public sealed class GitHubEnvironmentTests : IAsyncLifetime
{
    private const string Org = "acme";
    private const string TargetName = "ghe";

    private readonly string _token = "ghp_" + new Bogus.Randomizer().AlphaNumeric(36);
    private WireMockServer _server = null!;
    private GitHubApiStub _api = null!;
    private GitHubAdapter _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("github-environments", TestContext.Current.CancellationToken);
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
    /// Given Production with team platform and user mona as reviewers, a 30-minute wait and protected branches only,
    /// when the repo is read,
    /// then it is one live object whose neutral and GitHub rules carry those values.
    /// </summary>
    [Fact(DisplayName = "👍 Reads an environment's reviewers, wait and branches")]
    public async Task Read_Environment_CarriesItsProtection()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoAsync(Environment("Production", wait: 30, protectedBranches: true, ("Team", "platform"), ("User", "Mona")));

        // Act
        LiveSnapshot live = await _sut.ReadAsync(Ref(repo), TestContext.Current.CancellationToken);

        // Assert
        LiveObject environment = live.Objects.Where(o => o.MatchKey == "gh:environment/production").ShouldHaveSingleItem();
        environment.Key.ShouldBe("gh:environment/Production");
        environment.Rules["environments"]!.ToJsonString().ShouldBe("""{"production":{"branches":"default","reviewers":["platform"],"waitMinutes":30}}""");
        environment.Rules["github.environments"]!.ToJsonString().ShouldBe("""{"production":{"preventSelfReview":false,"users":["mona"]}}""");
    }

    /// <summary>
    /// Given a repo without a production environment,
    /// when the policy wants platform to approve deployments to it after 15 minutes, from protected branches,
    /// then the environment is created with the team's id, the wait and the branch policy.
    /// </summary>
    [Fact(DisplayName = "👍 Creates a missing environment")]
    public async Task Diff_MissingEnvironment_IsCreated()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoAsync();
        DesiredState desired = Desired(repo, ("environments", """{"production":{"enabled":true,"reviewers":["platform"],"waitMinutes":15,"branches":"default"}}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        PlanObject created = diff.Objects.ShouldHaveSingleItem();
        created.Key.ShouldBe("gh:new/environment/production");
        created.Operation.ShouldBe(ObjectOperation.Create);
        created.Payload!.ToJsonString().ShouldBe(
            $$$$"""{"wait_timer":15,"prevent_self_review":false,"reviewers":[{"type":"Team","id":{{{{GitHubApiStub.TeamId("platform")}}}}}],"deployment_branch_policy":{"protected_branches":true,"custom_branch_policies":false}}""");
    }

    /// <summary>
    /// Given Production with team legacy and user mona as reviewers and custom branch policies,
    /// when the policy sets only its team reviewers to platform,
    /// then the teams are replaced and mona, the wait and the custom branch policies are kept.
    /// </summary>
    [Fact(DisplayName = "👍 Replaces only the declared fields")]
    public async Task Diff_DeclaredFields_ReplaceOnlyThose()
    {
        // Arrange
        JsonObject production = Environment("Production", wait: 5, protectedBranches: false, ("Team", "legacy"), ("User", "mona"));
        production["deployment_branch_policy"] = new JsonObject { ["protected_branches"] = false, ["custom_branch_policies"] = true };
        GitHubRepoBuilder repo = await RepoAsync(production);
        DesiredState desired = Desired(repo, ("environments", """{"Production":{"enabled":true,"reviewers":["platform"]}}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        PlanObject updated = diff.Objects.ShouldHaveSingleItem();
        updated.Key.ShouldBe("gh:environment/Production");
        updated.Operation.ShouldBe(ObjectOperation.Update);
        updated.Payload!.ToJsonString().ShouldBe(
            $$$$"""{"wait_timer":5,"prevent_self_review":false,"reviewers":[{"type":"Team","id":{{{{GitHubApiStub.TeamId("platform")}}}}},{"type":"User","id":7}],"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}""");
        diff.Changes.Select(c => c.Rule).ShouldBe(["environments.production"]);
    }

    /// <summary>
    /// Given Production with exactly the reviewers, wait and branches the policy declares,
    /// when it is diffed,
    /// then nothing changes.
    /// </summary>
    [Fact(DisplayName = "👍 An environment with its protection is in sync")]
    public async Task Diff_InSync_HasNoObjects()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoAsync(Environment("Production", wait: 10, protectedBranches: true, ("Team", "platform"), ("User", "mona")));
        _api.UserByLogin("mona", 7);
        DesiredState desired = Desired(
            repo,
            ("environments", """{"production":{"enabled":true,"reviewers":["Platform"],"waitMinutes":10,"branches":"default"}}"""),
            ("github.environments", """{"production":{"users":["Mona"]}}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        diff.Objects.ShouldBeEmpty();
    }

    /// <summary>
    /// Given Production with reviewers, a wait and a branch policy, and no staging environment,
    /// when the policy disables both,
    /// then Production's protection is cleared and the environment kept, and staging isn't created.
    /// </summary>
    [Fact(DisplayName = "👍 enabled: false clears the protection and creates nothing")]
    public async Task Diff_Disabled_ClearsProtectionAndCreatesNothing()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoAsync(Environment("Production", wait: 10, protectedBranches: true, ("Team", "platform")));
        DesiredState desired = Desired(repo, ("environments", """{"production":{"enabled":false},"staging":{"enabled":false}}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        PlanObject cleared = diff.Objects.ShouldHaveSingleItem();
        cleared.Operation.ShouldBe(ObjectOperation.Update);
        cleared.Payload!.ToJsonString().ShouldBe("""{"wait_timer":0,"prevent_self_review":false,"reviewers":[],"deployment_branch_policy":null}""");
    }

    /// <summary>
    /// Given Production, which the policy names, and preview, which it doesn't,
    /// when it is diffed,
    /// then preview is left alone and listed as unmanaged.
    /// </summary>
    [Fact(DisplayName = "👍 Environments the policy doesn't name are unmanaged")]
    public async Task Diff_UnnamedEnvironment_IsUnmanaged()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoAsync(
            Environment("Production", wait: 0, protectedBranches: false, ("Team", "platform")),
            Environment("preview", wait: 0, protectedBranches: false));
        DesiredState desired = Desired(repo, ("environments", """{"production":{"enabled":true,"reviewers":["platform"]}}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        diff.Objects.ShouldBeEmpty();
        diff.Unmanaged.ShouldHaveSingleItem().Source.ShouldBe("gh:environment/preview");
    }

    /// <summary>
    /// Given a policy naming a team that isn't in the organization, or a user GitHub doesn't have,
    /// when the repo is mapped,
    /// then the plan fails with SP0207.
    /// </summary>
    [Theory(DisplayName = "👎 An unknown team or user is SP0207")]
    [InlineData("environments", """{"production":{"enabled":true,"reviewers":["nobody"]}}""", "there is no such team")]
    [InlineData("github.environments", """{"production":{"users":["ghost-user"]}}""", "GitHub has no such user")]
    public async Task Map_UnknownReviewer_ThrowsPrincipalNotFound(string rule, string json, string message)
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoAsync();
        DesiredState desired = Desired(repo, (rule, json));

        // Act
        PolicyMappingException ex = await Should.ThrowAsync<PolicyMappingException>(() => DiffAsync(desired));

        // Assert
        ex.Code.ShouldBe(DiagnosticCodes.PrincipalNotFound);
        ex.Message.ShouldContain(message);
    }

    /// <summary>
    /// Given a policy with four teams and three users for one environment,
    /// when the repo is mapped,
    /// then the plan fails with SP0121, because GitHub allows six reviewers.
    /// </summary>
    [Fact(DisplayName = "👎 More than six reviewers is SP0121")]
    public async Task Map_TooManyReviewers_ThrowsInvalidEnvironment()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoAsync();
        _api.UserByLogin("ann", 1).UserByLogin("bob", 2).UserByLogin("cid", 3);
        DesiredState desired = Desired(
            repo,
            ("environments", """{"production":{"enabled":true,"reviewers":["platform","payments","legacy","support"]}}"""),
            ("github.environments", """{"production":{"users":["ann","bob","cid"]}}"""));

        // Act
        PolicyMappingException ex = await Should.ThrowAsync<PolicyMappingException>(() => DiffAsync(desired));

        // Assert
        ex.Code.ShouldBe(DiagnosticCodes.InvalidEnvironment);
        ex.Message.ShouldContain("7 reviewers");
    }

    /// <summary>
    /// Given a private repo,
    /// when a policy with environments is diffed,
    /// then environments are skipped with SP3002, nothing is planned for them, and they aren't even listed.
    /// </summary>
    [Fact(DisplayName = "👎 Private repos skip environments with SP3002")]
    public async Task Diff_PrivateRepo_SkipsWithWarning()
    {
        // Arrange
        GitHubRepoBuilder repo = await RepoAsync(r => r.Private());
        DesiredState desired = Desired(repo, ("environments", """{"production":{"enabled":true,"reviewers":["platform"]}}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        diff.Objects.ShouldBeEmpty();
        diff.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.RuleUnavailable);
        _server.LogEntries.ShouldNotContain(e => e.RequestMessage!.Path.EndsWith("/environments", StringComparison.Ordinal));
    }

    /// <summary>
    /// Given a token that may not list environments,
    /// when a policy with environments is diffed,
    /// then they are reported as unknown (SP2202) and nothing is planned.
    /// </summary>
    [Fact(DisplayName = "👎 Environments the token can't list are unknown")]
    public async Task Diff_EnvironmentsForbidden_AreUnknown()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org);
        _api.User("setpoint-bot").Repo(repo.Build()).Rulesets(Org, repo.Name).BranchRules(Org, repo.Name, "main").Teams(Org, repo.Name)
            .OrgTeams(Org, "platform").EnvironmentsForbidden(Org, repo.Name);
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
        DesiredState desired = Desired(repo, ("environments", """{"production":{"enabled":true,"reviewers":["platform"]}}"""));

        // Act
        RepoDiff diff = await DiffAsync(desired);

        // Assert
        diff.Objects.ShouldBeEmpty();
        diff.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.LiveValueUnknown);
    }

    /// <summary>
    /// Given a planned create of the production environment,
    /// when it is applied,
    /// then the payload is PUT to the environment by name, exactly as planned.
    /// </summary>
    [Fact(DisplayName = "👍 Applies an environment with PUT")]
    public async Task Apply_Environment_PutsPayload()
    {
        // Arrange
        _api.User("setpoint-bot").EnvironmentWrites(Org, "payments-api");
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
        var payload = new JsonObject { ["wait_timer"] = 15, ["prevent_self_review"] = false, ["reviewers"] = new JsonArray(), ["deployment_branch_policy"] = null };
        var planned = new PlanObject("gh:new/environment/production", ObjectOperation.Create, "sha256:abc", "gh:environment/production", payload);

        // Act
        ApplyResult result = await _sut.ApplyAsync(new RepoRef(TargetName, Org, "payments-api"), new RepoChangeSet([planned]), TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.ShouldBeTrue();
        WireMock.IRequestMessage put = _server.LogEntries.Select(e => e.RequestMessage!).Where(r => r.Method == "PUT").ShouldHaveSingleItem();
        put.Path.ShouldBe($"{GitHubApiStub.Root}/repos/acme/payments-api/environments/production");
        JsonNode.DeepEquals(JsonNode.Parse(put.Body!), payload).ShouldBeTrue();
    }

    /// <summary>
    /// Given a planned delete of an environment, as a hand-edited plan file could hold,
    /// when it is applied,
    /// then it is refused without a request, because environments are never deleted.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses to delete an environment")]
    public async Task Apply_Delete_IsRefused()
    {
        // Arrange
        _api.User("setpoint-bot").EnvironmentWrites(Org, "payments-api");
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
        var planned = new PlanObject("gh:environment/production", ObjectOperation.Delete, "sha256:abc", "gh:environment/production");

        // Act
        ApplyResult result = await _sut.ApplyAsync(new RepoRef(TargetName, Org, "payments-api"), new RepoChangeSet([planned]), TestContext.Current.CancellationToken);

        // Assert
        result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull().ShouldContain("never deleted");
        _server.LogEntries.ShouldNotContain(e => e.RequestMessage!.Method == "PUT" || e.RequestMessage.Method == "DELETE");
    }

    private Task<GitHubRepoBuilder> RepoAsync(params JsonObject[] environments) => RepoAsync(r => r, environments);

    private async Task<GitHubRepoBuilder> RepoAsync(Func<GitHubRepoBuilder, GitHubRepoBuilder> shape, params JsonObject[] environments)
    {
        GitHubRepoBuilder repo = shape(new GitHubRepoBuilder().WithOwner(Org));
        _api.User("setpoint-bot")
            .Repo(repo.Build())
            .Rulesets(Org, repo.Name)
            .BranchRules(Org, repo.Name, "main")
            .Teams(Org, repo.Name)
            .OrgTeams(Org, "platform", "payments", "legacy", "support")
            .Environments(Org, repo.Name, environments);
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
        return repo;
    }

    private async Task<RepoDiff> DiffAsync(DesiredState desired)
    {
        LiveSnapshot read = await _sut.ReadAsync(desired.Repo, TestContext.Current.CancellationToken);
        LiveSnapshot live = await _sut.PrepareAsync(desired, read, TestContext.Current.CancellationToken);
        return RepoDiffer.Diff(desired, live, _sut.MapObjects(desired, live));
    }

    private static JsonObject Environment(string name, int wait, bool protectedBranches, params (string Type, string Name)[] reviewers)
    {
        var rules = new JsonArray();
        if (wait > 0)
        {
            rules.Add(new JsonObject { ["id"] = 3736, ["node_id"] = "GA_kwDO", ["type"] = "wait_timer", ["wait_timer"] = wait });
        }

        if (reviewers.Length > 0)
        {
            rules.Add(new JsonObject
            {
                ["id"] = 3755,
                ["node_id"] = "GA_kwDP",
                ["type"] = "required_reviewers",
                ["prevent_self_review"] = false,
                ["reviewers"] = new JsonArray([.. reviewers.Select(r => (JsonNode)new JsonObject
                {
                    ["type"] = r.Type,
                    ["reviewer"] = r.Type == "Team"
                        ? new JsonObject { ["id"] = GitHubApiStub.TeamId(r.Name), ["slug"] = r.Name }
                        : new JsonObject { ["id"] = 7, ["login"] = r.Name },
                })]),
            });
        }

        return new JsonObject
        {
            ["id"] = 161088068,
            ["name"] = name,
            ["created_at"] = "2026-01-02T03:04:05Z",
            ["updated_at"] = "2026-11-10T03:04:05Z",
            ["protection_rules"] = rules,
            ["deployment_branch_policy"] = protectedBranches ? new JsonObject { ["protected_branches"] = true, ["custom_branch_policies"] = false } : null,
        };
    }

    private static RepoRef Ref(GitHubRepoBuilder repo) => new(TargetName, repo.Owner, repo.Name);

    private Target Target() => new(TargetName, new TargetDefinition { Platform = Platform.GitHub, Url = _api.Url, Scope = ["*/*"] });

    private static DesiredState Desired(GitHubRepoBuilder repo, params (string Rule, string Json)[] rules)
    {
        var layer = new LayerRef(LayerKind.Baseline, null, SourceLocation.StartOf("baseline.yml"));
        return new DesiredState(
            Ref(repo),
            rules.ToDictionary(r => r.Rule, r => new DesiredRule(r.Rule, JsonNode.Parse(r.Json), layer, Locked: false)));
    }
}
