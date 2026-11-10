using Setpoint.Core.Applying;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Providers;
using Setpoint.Core.Tests.TestData;
using Setpoint.Core.Tests.TestSupport;
using Setpoint.Testing;

namespace Setpoint.Core.Tests.Applying;

/// <summary>
/// Tests for <see cref="Applier"/> with a fake adapter and no HTTP: the checks that refuse a run before the first
/// write (Setpoint version, policy hash, live drift, <c>--max-changes</c>, <c>--strict</c>, unreachable targets),
/// and how a failed write stops its repo, or the run with <c>--fail-fast</c>, with the matching exit codes.
/// </summary>
public sealed class ApplierTests
{
    private readonly PolicyRepoBuilder _builder = new();
    private readonly FakeProviderAdapter _adapter = new(providerBlock: FakeProviderBlockDefinition.Instance);
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    public ApplierTests() => _builder.WithBaseline("""
        version: 1
        layer: baseline
        rules:
          minimumReviewers: { count: 2, resetOnPush: true }
          requireCommentResolution: true
        """);

    private RepoRef Api => new(_builder.TargetName, _builder.Owner, "api");

    private RepoRef Web => new(_builder.TargetName, _builder.Owner, "web");

    /// <summary>
    /// Given a fresh plan with an update and a create for one repo,
    /// when it is applied,
    /// then both objects are written, the repo reads as applied and the exit code is 0.
    /// </summary>
    [Fact(DisplayName = "👍 Applies a fresh plan and writes every planned object")]
    public async Task Apply_FreshPlan_WritesEveryObject()
    {
        // Arrange
        _adapter.AddRepo(Api).WithLive("minimumReviewers", """{ "count": 1, "resetOnPush": true }""");
        PlanDocument plan = await PlanAsync();

        // Act
        ApplyResultSet result = await ApplyAsync(plan);

        // Assert
        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Refusal.ShouldBeNull();
        RepoApplyResult repo = result.Repos.ShouldHaveSingleItem();
        repo.Status.ShouldBe(RepoApplyStatus.Applied);
        repo.Written.ShouldBe(2);
        _adapter.Applied.ShouldHaveSingleItem().Changes.Objects.Select(o => o.Key)
            .ShouldBe(["fake:new/requireCommentResolution", "fake:policy/1"], ignoreOrder: true);
    }

    /// <summary>
    /// Given a plan made by another Setpoint version,
    /// when it is applied,
    /// then the run is refused as stale with exit code 3 and no target is written.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a plan made by another Setpoint version")]
    public async Task Apply_OtherSetpointVersion_RefusesStale()
    {
        // Arrange
        _adapter.AddRepo(Api);
        PlanDocument plan = (await PlanAsync()) with { SetpointVersion = "0.0.1-other" };

        // Act
        ApplyResultSet result = await ApplyAsync(plan);

        // Assert
        result.ExitCode.ShouldBe(ExitCode.StalePlan);
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("0.0.1-other");
        _adapter.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a plan whose policy files changed after it was made,
    /// when it is applied,
    /// then the run is refused as stale with exit code 3 and nothing is written.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a plan when the policy files changed since")]
    public async Task Apply_PolicyChanged_RefusesStale()
    {
        // Arrange
        _adapter.AddRepo(Api);
        PlanDocument plan = await PlanAsync();
        _builder.WithBaseline("""
            version: 1
            layer: baseline
            rules:
              requireCommentResolution: true
            """);

        // Act
        ApplyResultSet result = await ApplyAsync(plan);

        // Assert
        result.ExitCode.ShouldBe(ExitCode.StalePlan);
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("policy files changed");
        _adapter.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a plan whose updated live object was edited by hand after planning,
    /// when it is applied,
    /// then the fingerprint check refuses the whole run as stale with exit code 3.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a plan when a live object changed since planning")]
    public async Task Apply_LiveObjectChanged_RefusesStale()
    {
        // Arrange
        FakeProviderAdapter.FakeRepo live = _adapter.AddRepo(Api).WithLive("minimumReviewers", """{ "count": 1, "resetOnPush": true }""");
        PlanDocument plan = await PlanAsync();
        live.Touch();

        // Act
        ApplyResultSet result = await ApplyAsync(plan);

        // Assert
        result.ExitCode.ShouldBe(ExitCode.StalePlan);
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("fake:policy/1 changed since the plan");
        _adapter.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a plan that creates a setting and a matching object that appeared live after planning,
    /// when it is applied,
    /// then the run is refused as stale, so the create never duplicates it.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a create when a matching object appeared since planning")]
    public async Task Apply_CreateTargetAppeared_RefusesStale()
    {
        // Arrange
        FakeProviderAdapter.FakeRepo live = _adapter.AddRepo(Api).WithLive("minimumReviewers", """{ "count": 2, "resetOnPush": true }""");
        PlanDocument plan = await PlanAsync();
        live.WithLive("requireCommentResolution", "false");

        // Act
        ApplyResultSet result = await ApplyAsync(plan);

        // Assert
        result.ExitCode.ShouldBe(ExitCode.StalePlan);
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("now exists where the plan creates");
        _adapter.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a plan that creates a setting and two matching objects that appeared live after planning,
    /// when it is applied,
    /// then the run is refused as stale, because an ambiguous match is never written.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a create when several matching objects appeared since planning")]
    public async Task Apply_CreateTargetAppearedAmbiguous_RefusesStale()
    {
        // Arrange
        FakeProviderAdapter.FakeRepo live = _adapter.AddRepo(Api).WithLive("minimumReviewers", """{ "count": 2, "resetOnPush": true }""");
        PlanDocument plan = await PlanAsync();
        live.Ambiguous.Add(new AmbiguousMatch("requireCommentResolution", ["fake:policy/8", "fake:policy/9"], "type + branch main", "fake:requireCommentResolution"));

        // Act
        ApplyResultSet result = await ApplyAsync(plan);

        // Assert
        result.ExitCode.ShouldBe(ExitCode.StalePlan);
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("fake:policy/8, fake:policy/9 now exist where the plan creates");
        _adapter.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a plan that updates an object which was deleted after planning,
    /// when it is applied,
    /// then the run is refused as stale.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a plan when an object it updates is gone")]
    public async Task Apply_UpdatedObjectGone_RefusesStale()
    {
        // Arrange
        FakeProviderAdapter.FakeRepo live = _adapter.AddRepo(Api).WithLive("minimumReviewers", """{ "count": 1, "resetOnPush": true }""");
        PlanDocument plan = await PlanAsync();
        live.Objects.Clear();

        // Act
        ApplyResultSet result = await ApplyAsync(plan);

        // Assert
        result.ExitCode.ShouldBe(ExitCode.StalePlan);
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("no longer exists");
    }

    /// <summary>
    /// Given a plan that changes two repos and --max-changes 1,
    /// when it is applied,
    /// then the guard refuses the run with exit code 4 before any target is contacted.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses a plan that changes more repos than --max-changes")]
    public async Task Apply_OverMaxChanges_RefusesGuard()
    {
        // Arrange
        _adapter.AddRepo(Api);
        _adapter.AddRepo(Web);
        PlanDocument plan = await PlanAsync();

        // Act
        ApplyResultSet result = await ApplyAsync(plan, new ApplyOptions(MaxChanges: 1));

        // Assert
        result.ExitCode.ShouldBe(ExitCode.GuardTripped);
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.TooManyChanges);
        _adapter.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a plan that changes two repos and --max-changes at the limit, or 0 for no limit,
    /// when it is applied,
    /// then both repos are written.
    /// </summary>
    [Theory(DisplayName = "🖐️ Allows a plan at the --max-changes limit or with no limit")]
    [InlineData(2)]
    [InlineData(0)]
    public async Task Apply_AtOrWithoutLimit_Writes(int maxChanges)
    {
        // Arrange
        _adapter.AddRepo(Api);
        _adapter.AddRepo(Web);
        PlanDocument plan = await PlanAsync();

        // Act
        ApplyResultSet result = await ApplyAsync(plan, new ApplyOptions(MaxChanges: maxChanges));

        // Assert
        result.ExitCode.ShouldBe(ExitCode.Success);
        _adapter.Applied.Count.ShouldBe(2);
    }

    /// <summary>
    /// Given two repos where the first one's write fails,
    /// when the plan is applied without --fail-fast,
    /// then the first repo is failed, the second is still applied, and the exit code is 1.
    /// </summary>
    [Fact(DisplayName = "👎 A failed write stops that repo and the others continue")]
    public async Task Apply_WriteFails_OtherReposContinue()
    {
        // Arrange
        _adapter.AddRepo(Api).ApplyFailure = "HTTP 500";
        _adapter.AddRepo(Web);
        PlanDocument plan = await PlanAsync();

        // Act
        ApplyResultSet result = await ApplyAsync(plan);

        // Assert
        result.ExitCode.ShouldBe(ExitCode.RuntimeFailure);
        result.Repos.Select(r => (r.Repo, r.Status)).ShouldBe([(Api, RepoApplyStatus.Failed), (Web, RepoApplyStatus.Applied)]);
        Diagnostic failure = result.Diagnostics.ShouldHaveSingleItem();
        failure.Code.ShouldBe(DiagnosticCodes.WriteFailed);
        failure.Message.ShouldContain("HTTP 500");
    }

    /// <summary>
    /// Given two repos where the first one's write fails,
    /// when the plan is applied with --fail-fast,
    /// then the second repo is not attempted.
    /// </summary>
    [Fact(DisplayName = "👎 --fail-fast stops the run at the first failed write")]
    public async Task Apply_FailFast_StopsRun()
    {
        // Arrange
        _adapter.AddRepo(Api).ApplyFailure = "HTTP 500";
        _adapter.AddRepo(Web);
        PlanDocument plan = await PlanAsync();

        // Act
        ApplyResultSet result = await ApplyAsync(plan, new ApplyOptions(FailFast: true));

        // Assert
        result.ExitCode.ShouldBe(ExitCode.RuntimeFailure);
        result.Repos.Select(r => r.Status).ShouldBe([RepoApplyStatus.Failed, RepoApplyStatus.NotAttempted]);
        _adapter.Applied.ShouldHaveSingleItem().Repo.ShouldBe(Api);
    }

    /// <summary>
    /// Given a target that is unreachable at apply time,
    /// when the plan is applied with or without --skip-unreachable,
    /// then without it the run is refused with exit code 1, and with it the repos are skipped with a warning.
    /// </summary>
    [Theory(DisplayName = "👎 An unreachable target refuses the run unless --skip-unreachable")]
    [InlineData(false, ExitCode.RuntimeFailure, DiagnosticSeverity.Error)]
    [InlineData(true, ExitCode.Success, DiagnosticSeverity.Warning)]
    public async Task Apply_TargetUnreachable_RefusesOrSkips(bool skipUnreachable, ExitCode expected, DiagnosticSeverity severity)
    {
        // Arrange
        _adapter.AddRepo(Api);
        PlanDocument plan = await PlanAsync();
        _adapter.DescribeFailure = new HttpRequestException("connection refused");

        // Act
        ApplyResultSet result = await ApplyAsync(plan, new ApplyOptions(SkipUnreachable: skipUnreachable));

        // Assert
        result.ExitCode.ShouldBe(expected);
        Diagnostic unreachable = result.Diagnostics.ShouldHaveSingleItem();
        unreachable.Code.ShouldBe(DiagnosticCodes.TargetUnreachable);
        unreachable.Severity.ShouldBe(severity);
        _adapter.Applied.ShouldBeEmpty();
        if (skipUnreachable)
        {
            result.Repos.ShouldHaveSingleItem().Status.ShouldBe(RepoApplyStatus.Skipped);
        }
    }

    /// <summary>
    /// Given an unreachable target with --skip-unreachable and --strict,
    /// when the plan is applied,
    /// then the skip warning becomes an error and nothing is written.
    /// </summary>
    [Fact(DisplayName = "👎 --strict turns a skipped target into an error")]
    public async Task Apply_StrictSkipUnreachable_Refuses()
    {
        // Arrange
        _adapter.AddRepo(Api);
        PlanDocument plan = await PlanAsync();
        _adapter.DescribeFailure = new HttpRequestException("connection refused");

        // Act
        ApplyResultSet result = await ApplyAsync(plan, new ApplyOptions(Strict: true, SkipUnreachable: true));

        // Assert
        result.ExitCode.ShouldBe(ExitCode.RuntimeFailure);
        result.Refusal.ShouldNotBeNull();
        result.Repos.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a plan that carries a repo warning,
    /// when it is applied with --strict,
    /// then the warning is reported as an error and nothing is written.
    /// </summary>
    [Fact(DisplayName = "👎 --strict refuses a plan that has warnings")]
    public async Task Apply_StrictWithPlanWarnings_Refuses()
    {
        // Arrange
        _adapter.AddRepo(Api).Warnings.Add(new RuleWarning("minimumReviewers", Diagnostic.Warning(DiagnosticCodes.ForcePushAllowed, "explicit allow")));
        PlanDocument plan = await PlanAsync();

        // Act
        ApplyResultSet result = await ApplyAsync(plan, new ApplyOptions(Strict: true));

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(DiagnosticSeverity.Error);
        result.Refusal.ShouldNotBeNull();
        _adapter.Applied.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a live re-read that fails during the drift check,
    /// when the plan is applied,
    /// then the run is refused before the first write.
    /// </summary>
    [Fact(DisplayName = "👎 Refuses the run when live state can't be re-read")]
    public async Task Apply_ReadFails_Refuses()
    {
        // Arrange
        FakeProviderAdapter.FakeRepo live = _adapter.AddRepo(Api);
        PlanDocument plan = await PlanAsync();
        live.ReadFailure = new HttpRequestException("timeout");

        // Act
        ApplyResultSet result = await ApplyAsync(plan);

        // Assert
        result.ExitCode.ShouldBe(ExitCode.RuntimeFailure);
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.RepoReadFailed);
        _adapter.Applied.ShouldBeEmpty();
    }

    private async Task<PlanDocument> PlanAsync()
    {
        PlanResult result = await new Planner([_adapter], [FakeProviderBlockDefinition.Instance], _time)
            .PlanAsync(_builder.LoadInput(), new PlanOptions(MaxChanges: 0), TestContext.Current.CancellationToken);
        result.HasErrors.ShouldBeFalse();
        return result.Document;
    }

    private Task<ApplyResultSet> ApplyAsync(PlanDocument plan, ApplyOptions? options = null)
    {
        PlanInput input = _builder.LoadInput();
        return new Applier([_adapter]).ApplyAsync(plan, input.Repo, input.Sources, options ?? new ApplyOptions(), TestContext.Current.CancellationToken);
    }
}
