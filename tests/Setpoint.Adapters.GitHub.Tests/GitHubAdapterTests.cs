using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Rulesets;
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
/// Tests for the read side of <see cref="GitHubAdapter"/> against a stubbed GitHub API: the preflight, listing repos
/// within scope with the skip rules, private repos on GitHub Free, reading the setpoint ruleset and the unmanaged
/// settings around it, and how the result diffs against a policy.
/// </summary>
public sealed class GitHubAdapterTests : IAsyncLifetime
{
    private const string Org = "acme";
    private const string TargetName = "ghe";

    private readonly string _token = "ghp_" + new Bogus.Randomizer().AlphaNumeric(36);
    private readonly Dictionary<string, string> _environment = [];
    private WireMockServer _server = null!;
    private GitHubApiStub _api = null!;
    private GitHubAdapter _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("github-api", TestContext.Current.CancellationToken);
        _api = new GitHubApiStub(_server, _token);
        _environment["SETPOINT_GHE_TOKEN"] = _token;
        _sut = new GitHubAdapter(new GitHubAdapterOptions
        {
            GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
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
    /// Given a GitHub Enterprise Server target and its token in SETPOINT_GHE_TOKEN,
    /// when it is described,
    /// then the preflight names the server version, the pinned API version and the signed-in account.
    /// </summary>
    [Fact(DisplayName = "👍 Describes the target with its version and the signed-in account")]
    public async Task Describe_TokenSet_ReturnsIdentity()
    {
        // Arrange
        _api.User("setpoint-bot", enterpriseVersion: "3.17.2");

        // Act
        TargetInfo info = await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);

        // Assert
        info.ShouldBe(new TargetInfo("GitHub Enterprise Server 3.17.2", "2022-11-28", "setpoint-bot"));
    }

    /// <summary>
    /// Given a target whose tokenEnv names another variable,
    /// when it is described,
    /// then the token comes from that variable.
    /// </summary>
    [Fact(DisplayName = "👍 Reads the token from tokenEnv when the target names one")]
    public async Task Describe_TokenEnv_UsesThatVariable()
    {
        // Arrange
        _environment.Clear();
        _environment["GH_GOVERNANCE_PAT"] = _token;
        _api.User("setpoint-bot");

        // Act
        TargetInfo info = await _sut.DescribeAsync(Target(d => d with { TokenEnv = "GH_GOVERNANCE_PAT" }), TestContext.Current.CancellationToken);

        // Assert
        info.Identity.ShouldBe("setpoint-bot");
    }

    /// <summary>
    /// Given no token in the environment,
    /// when the target is described,
    /// then it fails naming the variable to set, before any request is sent.
    /// </summary>
    [Fact(DisplayName = "👎 Fails the preflight when the token variable isn't set")]
    public async Task Describe_NoToken_FailsNamingTheVariable()
    {
        // Arrange
        _environment.Clear();

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("SETPOINT_GHE_TOKEN");
        _server.LogEntries.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a GitHub target with an auth method only Azure DevOps has,
    /// when it is described,
    /// then it fails naming the methods GitHub supports.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses auth methods GitHub doesn't have")]
    public async Task Describe_AccessTokenAuth_IsNotSupported()
    {
        // Act
        var exception = await Should.ThrowAsync<NotSupportedException>(() =>
            _sut.DescribeAsync(Target(d => d with { Auth = AuthMethod.AccessToken }), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("use pat or app");
    }

    /// <summary>
    /// Given an organization with repos in and out of the target's scope and exclude,
    /// when repos are listed,
    /// then only repos in scope and not excluded are returned.
    /// </summary>
    [Fact(DisplayName = "👍 Lists only repos in scope and outside exclude")]
    public async Task ListRepos_ScopeAndExclude_KeepsOnlyThoseInScope()
    {
        // Arrange
        _api.User("setpoint-bot").Organization(
            Org,
            new GitHubRepoBuilder().WithOwner(Org).WithName("payments-api").Build(),
            new GitHubRepoBuilder().WithOwner(Org).WithName("payments-sandbox").Build(),
            new GitHubRepoBuilder().WithOwner(Org).WithName("website").Build());
        Target target = Target(d => d with { Scope = ["acme/payments-*"], Exclude = ["acme/*-sandbox"] });
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        List<ListedRepo> listed = await ListAsync(target);

        // Assert
        listed.Select(r => r.Repo.ToString()).ShouldBe(["ghe/acme/payments-api"]);
        listed[0].Skipped.ShouldBeNull();
    }

    /// <summary>
    /// Given archived, forked and empty repos in scope, and a tiny repo that reports size 0 but has a branch,
    /// when repos are listed,
    /// then each skipped one says why with its code, and the tiny one is managed.
    /// </summary>
    [Fact(DisplayName = "👎 Skips archived, forked and empty repos with their codes")]
    public async Task ListRepos_SkippableRepos_AreSkippedWithCodes()
    {
        // Arrange
        _api.User("setpoint-bot").Organization(
            Org,
            new GitHubRepoBuilder().WithOwner(Org).WithName("old").Archived().Build(),
            new GitHubRepoBuilder().WithOwner(Org).WithName("forked").Fork().Build(),
            new GitHubRepoBuilder().WithOwner(Org).WithName("empty").Empty().Build(),
            new GitHubRepoBuilder().WithOwner(Org).WithName("tiny").Empty().Build());
        _api.Branches(Org, "empty").Branches(Org, "tiny", "main");
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        Dictionary<string, Diagnostic?> skipped = (await ListAsync(target)).ToDictionary(r => r.Repo.Name, r => r.Skipped);

        // Assert
        skipped["old"].ShouldNotBeNull().Code.ShouldBe(DiagnosticCodes.ArchivedRepoSkipped);
        skipped["old"]!.Severity.ShouldBe(DiagnosticSeverity.Info);
        skipped["forked"].ShouldNotBeNull().Code.ShouldBe(DiagnosticCodes.ForkSkipped);
        skipped["empty"].ShouldNotBeNull().Code.ShouldBe(DiagnosticCodes.EmptyRepoSkipped);
        skipped["empty"]!.Severity.ShouldBe(DiagnosticSeverity.Warning);
        skipped["tiny"].ShouldBeNull();
    }

    /// <summary>
    /// Given a fork in scope and a target with includeForks: true,
    /// when repos are listed,
    /// then the fork is managed.
    /// </summary>
    [Fact(DisplayName = "🖐️ Manages forks when the target sets includeForks")]
    public async Task ListRepos_IncludeForks_ManagesForks()
    {
        // Arrange
        _api.User("setpoint-bot").Organization(Org, new GitHubRepoBuilder().WithOwner(Org).WithName("forked").Fork().Build());
        Target target = Target(d => d with { IncludeForks = true });
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        List<ListedRepo> listed = await ListAsync(target);

        // Assert
        listed.ShouldHaveSingleItem().Skipped.ShouldBeNull();
    }

    /// <summary>
    /// Given an organization whose repos span two pages,
    /// when repos are listed,
    /// then the second page is followed through the Link header.
    /// </summary>
    [Fact(DisplayName = "👍 Follows the Link header across pages")]
    public async Task ListRepos_TwoPages_ReturnsBoth()
    {
        // Arrange
        _api.User("setpoint-bot").OrganizationInPages(
            Org,
            [new GitHubRepoBuilder().WithOwner(Org).WithName("one").Build()],
            [new GitHubRepoBuilder().WithOwner(Org).WithName("two").Build()]);
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        List<ListedRepo> listed = await ListAsync(target);

        // Assert
        listed.Select(r => r.Repo.Name).ShouldBe(["one", "two"]);
    }

    /// <summary>
    /// Given a scope owner that is a user, not an organization,
    /// when repos are listed,
    /// then the user's repos are listed instead.
    /// </summary>
    [Fact(DisplayName = "🖐️ Lists a user's repos when the owner isn't an organization")]
    public async Task ListRepos_UserOwner_ListsUserRepos()
    {
        // Arrange
        _api.User("setpoint-bot").UserAccount("mattias", new GitHubRepoBuilder().WithOwner("mattias").WithName("dotfiles").Build());
        Target target = Target(d => d with { Scope = ["mattias/*"] });
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);

        // Act
        List<ListedRepo> listed = await ListAsync(target);

        // Assert
        listed.ShouldHaveSingleItem().Repo.ShouldBe(new RepoRef(TargetName, "mattias", "dotfiles"));
    }

    /// <summary>
    /// Given a private repo whose rulesets GitHub refuses with its upgrade message,
    /// when its capabilities are checked,
    /// then it is skipped with SP3001.
    /// </summary>
    [Fact(DisplayName = "👎 Skips a private repo on GitHub Free with SP3001")]
    public async Task GetCapabilities_PrivateOnFree_SkipsWithSp3001()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org).Private();
        _api.User("setpoint-bot").Organization(Org, repo.Build()).RulesetsNeedUpgrade(Org, repo.Name);
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        await ListAsync(target);

        // Act
        RepoCapabilities capabilities = await _sut.GetCapabilitiesAsync(new RepoRef(TargetName, Org, repo.Name), TestContext.Current.CancellationToken);

        // Assert
        capabilities.Skipped.ShouldNotBeNull().Code.ShouldBe(DiagnosticCodes.PrivateRepoOnGitHubFree);
    }

    /// <summary>
    /// Given a private repo on a plan with rulesets,
    /// when its capabilities are checked,
    /// then it can be managed.
    /// </summary>
    [Fact(DisplayName = "👍 Manages a private repo on a plan with rulesets")]
    public async Task GetCapabilities_PrivateOnPaidPlan_IsFull()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org).Private();
        _api.User("setpoint-bot").Organization(Org, repo.Build()).Rulesets(Org, repo.Name);
        Target target = Target();
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
        await ListAsync(target);

        // Act
        RepoCapabilities capabilities = await _sut.GetCapabilitiesAsync(new RepoRef(TargetName, Org, repo.Name), TestContext.Current.CancellationToken);

        // Assert
        capabilities.Skipped.ShouldBeNull();
    }

    /// <summary>
    /// Given a repo with the setpoint ruleset, an organization ruleset on the default branch and restrictive merge flags,
    /// when it is read,
    /// then the setpoint ruleset is the one live object, and the other two are unmanaged settings with their sources.
    /// </summary>
    [Fact(DisplayName = "👍 Reads the setpoint ruleset and the unmanaged settings around it")]
    public async Task Read_RulesetsAndMergeFlags_SplitsManagedFromUnmanaged()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org).WithMergeFlags(merge: false, squash: true, rebase: false);
        RulesetBuilder setpoint = new RulesetBuilder().WithPullRequest(count: 2).WithForcePushAndDeleteProtection();
        RulesetBuilder org = new RulesetBuilder().WithName("org-baseline");
        await DescribeWithRepoAsync(repo);
        _api.Rulesets(Org, repo.Name, setpoint.Summary(), org.Summary("Organization", Org))
            .Ruleset(Org, repo.Name, setpoint.Build())
            .BranchRules(Org, repo.Name, "main",
                BranchRule("pull_request", setpoint.Id, """{"required_approving_review_count":2}"""),
                BranchRule("required_status_checks", org.Id, """{"required_status_checks":[{"context":"sec-scan"}]}""", "Organization", Org))
            .Teams(Org, repo.Name);

        // Act
        LiveSnapshot live = await _sut.ReadAsync(new RepoRef(TargetName, Org, repo.Name), TestContext.Current.CancellationToken);

        // Assert
        LiveObject ruleset = live.Objects.ShouldHaveSingleItem();
        ruleset.Key.ShouldBe($"gh:ruleset/{setpoint.Id}");
        ruleset.MatchKey.ShouldBe(GitHubAdapter.MatchKey);
        ruleset.Native.AsObject().ContainsKey("_links").ShouldBeFalse();
        ruleset.Rules[RulesetMapper.MinimumReviewers]!.ToJsonString().ShouldBe("""{"count":2,"resetOnPush":true}""");
        live.Unmanaged.Select(u => $"{u.Rule} | {u.Source} | {u.Value?.ToJsonString()}").ShouldBe(
        [
            """requiredChecks | ruleset 'org-baseline' (organization acme) | [{"name":"sec-scan"}]""",
            """allowedMergeTypes | repo merge settings | ["squash"]""",
        ]);
        live.UnknownRules.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a read-only token, for which GitHub leaves bypass actors and merge flags out,
    /// when the repo is read,
    /// then bypass actors and team access are reported as unknown and no merge restriction is made up.
    /// </summary>
    [Fact(DisplayName = "👎 Reports bypass actors as unknown for a read-only token")]
    public async Task Read_ReadOnlyToken_ReportsBypassActorsUnknown()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org).WithoutMergeFlags();
        RulesetBuilder setpoint = new RulesetBuilder().WithPullRequest().WithoutBypassActorsVisible();
        await DescribeWithRepoAsync(repo);
        _api.Rulesets(Org, repo.Name, setpoint.Summary()).Ruleset(Org, repo.Name, setpoint.Build()).BranchRules(Org, repo.Name, "main").TeamsForbidden(Org, repo.Name);

        // Act
        LiveSnapshot live = await _sut.ReadAsync(new RepoRef(TargetName, Org, repo.Name), TestContext.Current.CancellationToken);

        // Assert
        live.UnknownRules.ShouldBe([RulesetMapper.BypassActors, "access", "github.access"]);
        live.Unmanaged.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a repo with no setpoint ruleset and a policy that sets rules,
    /// when it is diffed,
    /// then the plan creates the ruleset, with the full payload apply would send.
    /// </summary>
    [Fact(DisplayName = "👍 Plans a create when the repo has no setpoint ruleset")]
    public async Task Diff_NoRuleset_PlansCreate()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org);
        await DescribeWithRepoAsync(repo);
        _api.Rulesets(Org, repo.Name).BranchRules(Org, repo.Name, "main");
        DesiredState desired = Desired(repo, (RulesetMapper.MinimumReviewers, """{"count":1,"resetOnPush":false}"""), (RulesetMapper.ProtectFromForcePushAndDelete, "true"));

        // Act
        RepoDiff diff = await DiffAsync(repo, desired);

        // Assert
        PlanObject created = diff.Objects.ShouldHaveSingleItem();
        created.Operation.ShouldBe(ObjectOperation.Create);
        created.Key.ShouldBe(GitHubAdapter.CreateKey);
        created.Fingerprint.ShouldBe(Fingerprint.NothingMatches(GitHubAdapter.MatchKey));
        created.Payload!["rules"]!.AsArray().Select(r => r!["type"]!.GetValue<string>()).ShouldBe(["pull_request", "non_fast_forward", "deletion"]);
        diff.Changes.Select(c => c.Rule).ShouldBe([RulesetMapper.MinimumReviewers, RulesetMapper.ProtectFromForcePushAndDelete]);
    }

    /// <summary>
    /// Given a live setpoint ruleset that already matches the policy,
    /// when it is diffed,
    /// then nothing changes, and rules the policy leaves unmanaged are only listed.
    /// </summary>
    [Fact(DisplayName = "👍 Plans nothing when the setpoint ruleset matches the policy")]
    public async Task Diff_RulesetMatches_IsInSync()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org);
        RulesetBuilder setpoint = new RulesetBuilder().WithPullRequest(count: 2, dismissStale: true).WithStatusChecks(false, "build");
        await DescribeWithRepoAsync(repo);
        _api.Rulesets(Org, repo.Name, setpoint.Summary()).Ruleset(Org, repo.Name, setpoint.Build()).BranchRules(Org, repo.Name, "main");
        DesiredState desired = Desired(
            repo,
            (RulesetMapper.MinimumReviewers, """{"count":2,"resetOnPush":true}"""),
            (RulesetMapper.RequiredChecks, """[{"name":"build"}]"""),
            (RulesetMapper.RequireCommentResolution, null),
            (RulesetMapper.BypassActors, "[]"));

        // Act
        RepoDiff diff = await DiffAsync(repo, desired);

        // Assert
        diff.Objects.ShouldBeEmpty();
        diff.Diagnostics.ShouldBeEmpty();
        diff.Unmanaged.Select(u => u.Rule).ShouldContain(RulesetMapper.AllowedMergeTypes);
    }

    /// <summary>
    /// Given a live setpoint ruleset and a policy that declares only some of its rules,
    /// when the desired objects are mapped,
    /// then the payload keeps the live values of the rules the policy doesn't declare.
    /// </summary>
    [Fact(DisplayName = "🖐️ Keeps undeclared rules' live values in the payload")]
    public async Task Diff_PartialPolicy_KeepsUndeclaredLiveRules()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org);
        RulesetBuilder setpoint = new RulesetBuilder().WithStatusChecks(true, "build");
        await DescribeWithRepoAsync(repo);
        _api.Rulesets(Org, repo.Name, setpoint.Summary()).Ruleset(Org, repo.Name, setpoint.Build()).BranchRules(Org, repo.Name, "main");
        DesiredState desired = Desired(repo, (RulesetMapper.ProtectFromForcePushAndDelete, "true"));

        // Act
        RepoDiff diff = await DiffAsync(repo, desired);

        // Assert
        PlanObject updated = diff.Objects.ShouldHaveSingleItem();
        updated.Operation.ShouldBe(ObjectOperation.Update);
        updated.Fingerprint.ShouldStartWith("sha256:");
        updated.Payload!["rules"]!.AsArray().Select(r => r!["type"]!.GetValue<string>()).ShouldBe(["required_status_checks", "non_fast_forward", "deletion"]);
    }

    /// <summary>
    /// Given an organization ruleset that requires more approvals than the policy,
    /// when the repo is diffed,
    /// then SP2101 warns that the stricter unmanaged setting is what takes effect.
    /// </summary>
    [Fact(DisplayName = "👎 Warns with SP2101 when another ruleset is stricter")]
    public async Task Diff_StricterOtherRuleset_WarnsSp2101()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org);
        RulesetBuilder org = new RulesetBuilder().WithName("org-protect");
        await DescribeWithRepoAsync(repo);
        _api.Rulesets(Org, repo.Name, org.Summary("Organization", Org))
            .BranchRules(Org, repo.Name, "main", BranchRule("pull_request", org.Id, """{"required_approving_review_count":4}""", "Organization", Org));
        DesiredState desired = Desired(repo, (RulesetMapper.MinimumReviewers, """{"count":2,"resetOnPush":false}"""));

        // Act
        RepoDiff diff = await DiffAsync(repo, desired);

        // Assert
        Diagnostic warning = diff.Diagnostics.ShouldHaveSingleItem();
        warning.Code.ShouldBe(DiagnosticCodes.UnmanagedStricter);
        warning.Message.ShouldContain("ruleset 'org-protect'");
    }

    /// <summary>
    /// Given repo merge settings that allow only rebase and a policy that allows only squash,
    /// when the repo is diffed,
    /// then SP2102 is an error, because no pull request could complete.
    /// </summary>
    [Fact(DisplayName = "👎 Fails with SP2102 when merge settings leave no merge method")]
    public async Task Diff_MergeFlagsLeaveNoMethod_FailsSp2102()
    {
        // Arrange
        GitHubRepoBuilder repo = new GitHubRepoBuilder().WithOwner(Org).WithMergeFlags(merge: false, squash: false, rebase: true);
        await DescribeWithRepoAsync(repo);
        _api.Rulesets(Org, repo.Name).BranchRules(Org, repo.Name, "main");
        DesiredState desired = Desired(repo, (RulesetMapper.AllowedMergeTypes, """["squash"]"""));

        // Act
        RepoDiff diff = await DiffAsync(repo, desired);

        // Assert
        Diagnostic error = diff.Diagnostics.Single(d => d.Code == DiagnosticCodes.NoMergeMethodLeft);
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.Message.ShouldContain("repo merge settings allows rebase");
        diff.Diagnostics.ShouldContain(d => d.Code == DiagnosticCodes.UnmanagedStricter);
    }

    private Target Target(Func<TargetDefinition, TargetDefinition>? change = null)
    {
        var definition = new TargetDefinition { Platform = Platform.GitHub, Url = _api.Url, Scope = ["acme/*"] };
        return new Target(TargetName, change?.Invoke(definition) ?? definition);
    }

    private async Task<List<ListedRepo>> ListAsync(Target target)
    {
        var listed = new List<ListedRepo>();
        await foreach (ListedRepo repo in _sut.ListReposAsync(target, TestContext.Current.CancellationToken))
        {
            listed.Add(repo);
        }

        return listed;
    }

    private async Task DescribeWithRepoAsync(GitHubRepoBuilder repo)
    {
        _api.User("setpoint-bot").Repo(repo.Build());
        await _sut.DescribeAsync(Target(), TestContext.Current.CancellationToken);
    }

    private async Task<RepoDiff> DiffAsync(GitHubRepoBuilder repo, DesiredState desired)
    {
        LiveSnapshot live = await _sut.ReadAsync(desired.Repo, TestContext.Current.CancellationToken);
        return RepoDiffer.Diff(desired, live, _sut.MapObjects(desired, live));
    }

    private static DesiredState Desired(GitHubRepoBuilder repo, params (string Rule, string? Json)[] rules)
    {
        var layer = new LayerRef(LayerKind.Baseline, null, SourceLocation.StartOf("baseline.yml"));
        return new DesiredState(
            new RepoRef(TargetName, repo.Owner, repo.Name),
            rules.ToDictionary(r => r.Rule, r => new DesiredRule(r.Rule, r.Json is null ? null : JsonNode.Parse(r.Json), layer, Locked: false)));
    }

    private static JsonObject BranchRule(string type, long rulesetId, string? parameters = null, string sourceType = "Repository", string source = "acme/payments")
    {
        var rule = new JsonObject
        {
            ["type"] = type,
            ["ruleset_source_type"] = sourceType,
            ["ruleset_source"] = source,
            ["ruleset_id"] = rulesetId,
        };
        if (parameters is not null)
        {
            rule["parameters"] = JsonNode.Parse(parameters);
        }

        return rule;
    }
}
