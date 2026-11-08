using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Rulesets;
using Setpoint.Adapters.GitHub.Tests.TestSupport;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;
using Setpoint.Http;
using Setpoint.Http.Tests.TestSupport;
using WireMock.Server;

namespace Setpoint.Adapters.GitHub.Tests;

/// <summary>
/// Tests for <see cref="GitHubAdapter.ApplyAsync"/>, replaying the recorded fixtures in <c>Fixtures/apply</c>:
/// create, update and delete of the setpoint ruleset, the payload sent as planned, a 403 naming the permission,
/// and that nothing but the setpoint ruleset is ever written.
/// </summary>
public sealed class GitHubApplyTests : IAsyncLifetime
{
    private const string TargetName = "ghe";

    private static readonly string s_fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "apply");

    private readonly string _token = "ghp_" + new Bogus.Randomizer().AlphaNumeric(36);
    private WireMockServer _server = null!;
    private GitHubAdapter _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await WireMockServerFactory.CreateAsync("github-apply", TestContext.Current.CancellationToken);
        foreach (string mapping in Directory.GetFiles(s_fixtures, "*.json"))
        {
            _server.ReadStaticMappingAndAddOrUpdate(mapping);
        }

        var api = new GitHubApiStub(_server, _token);
        api.User("setpoint-bot");
        _sut = new GitHubAdapter(new GitHubAdapterOptions
        {
            GetEnvironmentVariable = name => name == "SETPOINT_GHE_TOKEN" ? _token : null,
            Http = new HttpCoreOptions { MaxRetries = 1, BaseDelay = TimeSpan.FromMilliseconds(1) },
        });
        var target = new Target(TargetName, new TargetDefinition { Platform = Platform.GitHub, Url = api.Url, Scope = ["acme/*"] });
        await _sut.DescribeAsync(target, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        WireMockServerFactory.Dispose(_server);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Given a planned create of the setpoint ruleset,
    /// when it is applied,
    /// then the planned payload is POSTed exactly as it is in the plan, and the write succeeds.
    /// </summary>
    [Fact(DisplayName = "👍 Creates the ruleset by sending the planned payload as-is")]
    public async Task Apply_Create_PostsPlannedPayload()
    {
        // Arrange
        JsonObject payload = ProtectionPayload();
        var planned = new PlanObject(GitHubAdapter.CreateKey, ObjectOperation.Create, Fingerprint.NothingMatches(GitHubAdapter.MatchKey), GitHubAdapter.MatchKey, payload);

        // Act
        ApplyResult result = await ApplyAsync("payments", planned);

        // Assert
        result.Succeeded.ShouldBeTrue();
        var write = Requests().Single(r => r.Method == "POST");
        write.Path.ShouldBe("/api/v3/repos/acme/payments/rulesets");
        JsonNode.DeepEquals(JsonNode.Parse(write.Body!), payload).ShouldBeTrue();
    }

    /// <summary>
    /// Given a planned update of the live setpoint ruleset 4242,
    /// when it is applied,
    /// then the payload is PUT to that ruleset, targeting the default branch and active, with no bypass actors.
    /// </summary>
    [Fact(DisplayName = "👍 Updates the live ruleset by its id")]
    public async Task Apply_Update_PutsToRulesetId()
    {
        // Arrange
        var planned = new PlanObject("gh:ruleset/4242", ObjectOperation.Update, "sha256:abc", GitHubAdapter.MatchKey, ProtectionPayload(withBypassActors: true));

        // Act
        ApplyResult result = await ApplyAsync("payments", planned);

        // Assert
        result.Succeeded.ShouldBeTrue();
        JsonNode sent = JsonNode.Parse(Requests().Single(r => r.Method == "PUT").Body!)!;
        sent["enforcement"]!.GetValue<string>().ShouldBe("active");
        sent["conditions"]!["ref_name"]!["include"]!.ToJsonString().ShouldBe("""["~DEFAULT_BRANCH"]""");
        sent["bypass_actors"]!.AsArray().ShouldBeEmpty();
    }

    /// <summary>
    /// Given a planned delete of the setpoint ruleset 4243,
    /// when it is applied,
    /// then that ruleset is deleted.
    /// </summary>
    [Fact(DisplayName = "👍 Deletes the ruleset when the policy leaves it nothing to do")]
    public async Task Apply_Delete_DeletesRuleset()
    {
        // Arrange
        var planned = new PlanObject("gh:ruleset/4243", ObjectOperation.Delete, "sha256:def", GitHubAdapter.MatchKey);

        // Act
        ApplyResult result = await ApplyAsync("payments", planned);

        // Assert
        result.Succeeded.ShouldBeTrue();
        Requests().ShouldContain(r => r.Method == "DELETE" && r.Path == "/api/v3/repos/acme/payments/rulesets/4243");
    }

    /// <summary>
    /// Given a token without Administration: write, which GitHub answers with 403,
    /// when a planned update is applied,
    /// then the repo fails with a message naming the missing permission.
    /// </summary>
    [Fact(DisplayName = "👎 A 403 fails the repo and names the missing permission")]
    public async Task Apply_Forbidden_NamesAdministrationWrite()
    {
        // Arrange
        var planned = new PlanObject("gh:ruleset/77", ObjectOperation.Update, "sha256:abc", GitHubAdapter.MatchKey, ProtectionPayload());

        // Act
        ApplyResult result = await ApplyAsync("readonly", planned);

        // Assert
        result.Succeeded.ShouldBeFalse();
        string error = result.Objects.ShouldHaveSingleItem().Error.ShouldNotBeNull();
        error.ShouldContain("403: Resource not accessible by personal access token");
        error.ShouldContain("Administration: write");
    }

    /// <summary>
    /// Given a write that fails, followed by another planned object,
    /// when they are applied,
    /// then the second is not attempted, because a failed write stops the repo.
    /// </summary>
    [Fact(DisplayName = "👎 A failed write stops the rest of the repo")]
    public async Task Apply_FailureThenMore_StopsTheRepo()
    {
        // Arrange
        var failing = new PlanObject("gh:ruleset/77", ObjectOperation.Update, "sha256:abc", GitHubAdapter.MatchKey, ProtectionPayload());
        var next = new PlanObject("gh:ruleset/78", ObjectOperation.Delete, "sha256:def", GitHubAdapter.MatchKey);

        // Act
        ApplyResult result = await ApplyAsync("readonly", failing, next);

        // Assert
        result.Objects[1].Succeeded.ShouldBeFalse();
        result.Objects[1].Error.ShouldNotBeNull().ShouldContain("Not attempted");
        Requests().ShouldNotContain(r => r.Method == "DELETE");
    }

    /// <summary>
    /// Given a planned object that isn't the setpoint ruleset,
    /// when it is applied,
    /// then it is refused without any request, so Setpoint never writes another ruleset or a repo setting.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses to write anything but the setpoint ruleset")]
    public async Task Apply_OtherObject_IsRefusedWithoutWriting()
    {
        // Arrange
        var planned = new PlanObject("gh:ruleset/99", ObjectOperation.Update, "sha256:abc", "gh:ruleset/org-protect", ProtectionPayload());

        // Act
        ApplyResult result = await ApplyAsync("payments", planned);

        // Assert
        result.Succeeded.ShouldBeFalse();
        Requests().ShouldNotContain(r => r.Method != "GET");
    }

    private List<WireMock.IRequestMessage> Requests() => [.. _server.LogEntries.Select(e => e.RequestMessage).OfType<WireMock.IRequestMessage>()];

    private Task<ApplyResult> ApplyAsync(string repo, params PlanObject[] objects) =>
        _sut.ApplyAsync(new RepoRef(TargetName, "acme", repo), new RepoChangeSet(objects), TestContext.Current.CancellationToken);

    private static JsonObject ProtectionPayload(bool withBypassActors = false)
    {
        var values = new Dictionary<string, JsonNode?> { [RulesetMapper.ProtectFromForcePushAndDelete] = true };
        if (withBypassActors)
        {
            values[RulesetMapper.BypassActors] = new JsonArray();
        }

        return RulesetMapper.Build(values, live: null)!;
    }
}
