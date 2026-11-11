using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Providers;
using Setpoint.Core.Tests.TestData;
using Setpoint.Core.Tests.TestSupport;
using Setpoint.Testing;

namespace Setpoint.Core.Tests.Planning;

/// <summary>
/// Tests for <see cref="Planner"/> with a fake adapter and no HTTP: the diff on managed rules only, creates,
/// updates and removals keyed on native objects, fingerprints, the neutral changes view, unmanaged settings,
/// skipped and failed repos, unreachable targets, live selector checks and <c>--strict</c>.
/// </summary>
public sealed class PlannerTests
{
    private readonly PolicyRepoBuilder _builder = new();
    private readonly FakeProviderAdapter _adapter = new(providerBlock: FakeProviderBlockDefinition.Instance);
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    private RepoRef Repo => new(_builder.TargetName, _builder.Owner, "api");

    /// <summary>
    /// Given a baseline rule and a repo whose live object already has that value,
    /// when the repo is planned,
    /// then it is in sync, with no objects and no changes.
    /// </summary>
    [Fact(DisplayName = "👍 A repo that matches the policy is in sync")]
    public async Task Plan_LiveMatches_InSync()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: true"));
        _adapter.AddRepo(Repo).WithLive("requireCommentResolution", "true");

        // Act
        PlanRepo repo = Single(await PlanAsync());

        // Assert
        repo.Status.ShouldBe(PlanRepoStatus.InSync);
        repo.Objects.ShouldBeEmpty();
        repo.Changes.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a locked minimumReviewers of 2 in the baseline and a live policy requiring 1,
    /// when the repo is planned,
    /// then the live object is updated, fingerprinted over its live state, and the change names the field, layer and lock.
    /// </summary>
    [Fact(DisplayName = "👍 A changed field becomes an update of the live object")]
    public async Task Plan_FieldDiffers_UpdatesLiveObject()
    {
        // Arrange
        _builder.WithBaseline(Baseline("minimumReviewers: { count: 2, resetOnPush: true }", locked: "[minimumReviewers]"));
        _adapter.AddRepo(Repo).WithLive("minimumReviewers", """{ "count": 1, "resetOnPush": true }""");

        // Act
        PlanRepo repo = Single(await PlanAsync());

        // Assert
        repo.Status.ShouldBe(PlanRepoStatus.Changes);
        PlanObject planned = repo.Objects.ShouldHaveSingleItem();
        planned.Key.ShouldBe("fake:policy/1");
        planned.Operation.ShouldBe(ObjectOperation.Update);
        planned.Fingerprint.ShouldStartWith(Fingerprint.Prefix);
        planned.Fingerprint.ShouldNotBe(Fingerprint.NothingMatches("fake:minimumReviewers"));
        planned.Payload!["value"]!["count"]!.GetValue<int>().ShouldBe(2);
        repo.Changes.ShouldBe([new PlanChange("minimumReviewers.count", ChangeAction.Update, "1", "2", "baseline", true, "fake:policy/1")]);
    }

    /// <summary>
    /// Given a rule with no live object,
    /// when the repo is planned,
    /// then an object is created under its create key, fingerprinted as "nothing matches".
    /// </summary>
    [Fact(DisplayName = "👍 A missing setting becomes a create that fingerprints nothing matching")]
    public async Task Plan_NoLiveObject_Creates()
    {
        // Arrange
        _builder.WithBaseline(Baseline("allowedMergeTypes: [squash, rebase]"));
        _adapter.AddRepo(Repo);

        // Act
        PlanRepo repo = Single(await PlanAsync());

        // Assert
        PlanObject planned = repo.Objects.ShouldHaveSingleItem();
        planned.Key.ShouldBe("fake:new/allowedMergeTypes");
        planned.Operation.ShouldBe(ObjectOperation.Create);
        planned.Fingerprint.ShouldBe(Fingerprint.NothingMatches("fake:allowedMergeTypes"));
        PlanChange change = repo.Changes.ShouldHaveSingleItem();
        change.Action.ShouldBe(ChangeAction.Create);
        change.From.ShouldBe(ValueText.None);
        change.To.ShouldBe("rebase, squash");
    }

    /// <summary>
    /// Given a rule set to false and a live object carrying it,
    /// when the repo is planned,
    /// then the object is deleted, with no payload, and the change is a removal.
    /// </summary>
    [Fact(DisplayName = "👍 A rule set to false deletes the live setting")]
    public async Task Plan_RuleFalseWithLive_Deletes()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: false"));
        _adapter.AddRepo(Repo).WithLive("requireCommentResolution", "true");

        // Act
        PlanRepo repo = Single(await PlanAsync());

        // Assert
        PlanObject planned = repo.Objects.ShouldHaveSingleItem();
        planned.Operation.ShouldBe(ObjectOperation.Delete);
        planned.Payload.ShouldBeNull();
        repo.Changes.ShouldHaveSingleItem().Action.ShouldBe(ChangeAction.Remove);
    }

    /// <summary>
    /// Given a rule set to false and no live setting,
    /// when the repo is planned,
    /// then nothing needs doing.
    /// </summary>
    [Fact(DisplayName = "🖐️ A rule set to false with nothing live is in sync")]
    public async Task Plan_RuleFalseNothingLive_InSync()
    {
        // Arrange
        _builder.WithBaseline(Baseline("minimumReviewers: { enabled: false }"));
        _adapter.AddRepo(Repo);

        // Act
        PlanRepo repo = Single(await PlanAsync());

        // Assert
        repo.Status.ShouldBe(PlanRepoStatus.InSync);
    }

    /// <summary>
    /// Given a rule left unmanaged with ~ and a live object carrying it,
    /// when the repo is planned,
    /// then it isn't diffed and is counted as unmanaged instead.
    /// </summary>
    [Fact(DisplayName = "🖐️ An unmanaged rule is counted, not diffed")]
    public async Task Plan_UnmanagedRule_CountedNotDiffed()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: ~"));
        _adapter.AddRepo(Repo).WithLive("requireCommentResolution", "true");

        // Act
        PlanRepo repo = Single(await PlanAsync());

        // Assert
        repo.Status.ShouldBe(PlanRepoStatus.InSync);
        repo.UnmanagedCount.ShouldBe(1);
        repo.Unmanaged.ShouldHaveSingleItem().Source.ShouldBe("fake:policy/1");
    }

    /// <summary>
    /// Given merge types listed in a different order from the live ones,
    /// when the repo is planned,
    /// then it is in sync, because lists are sets.
    /// </summary>
    [Fact(DisplayName = "🖐️ List order doesn't count as a change")]
    public async Task Plan_ListInOtherOrder_InSync()
    {
        // Arrange
        _builder.WithBaseline(Baseline("allowedMergeTypes: [squash, rebase]"));
        _adapter.AddRepo(Repo).WithLive("allowedMergeTypes", """["rebase", "squash"]""");

        // Act
        PlanRepo repo = Single(await PlanAsync());

        // Assert
        repo.Status.ShouldBe(PlanRepoStatus.InSync);
    }

    /// <summary>
    /// Given a provider-block rule for this adapter,
    /// when the repo is planned with nothing live,
    /// then the provider rule is planned like a neutral one.
    /// </summary>
    [Fact(DisplayName = "👍 The adapter's own provider rules are diffed")]
    public async Task Plan_ProviderRule_Diffed()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: true", extra: "fake:\n  minimumReviewers: { flag: true }"));
        _adapter.AddRepo(Repo).WithLive("requireCommentResolution", "true");

        // Act
        PlanRepo repo = Single(await PlanAsync());

        // Assert
        repo.Changes.ShouldHaveSingleItem().Rule.ShouldBe("fake.minimumReviewers");
    }

    /// <summary>
    /// Given unmanaged settings that allow only rebase, and then only merge, and a policy that allows squash and rebase,
    /// when the repo is planned,
    /// then SP2102 is an error naming every source, because together they leave no merge method.
    /// </summary>
    [Fact(DisplayName = "👎 Unmanaged merge settings that leave no merge method raise SP2102")]
    public async Task Plan_NoMergeMethodLeft_FailsSP2102()
    {
        // Arrange
        _builder.WithBaseline(Baseline("allowedMergeTypes: [squash, rebase]"));
        FakeProviderAdapter.FakeRepo live = _adapter.AddRepo(Repo).WithLive("allowedMergeTypes", """["rebase", "squash"]""");
        live.Unmanaged.Add(new UnmanagedSetting("allowedMergeTypes", "repo merge settings", System.Text.Json.Nodes.JsonNode.Parse("""["rebase"]""")));
        live.Unmanaged.Add(new UnmanagedSetting("allowedMergeTypes", "ruleset 'org-merge'", System.Text.Json.Nodes.JsonNode.Parse("""["merge"]""")));

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        Diagnostic error = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.NoMergeMethodLeft);
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.Message.ShouldContain("repo merge settings allows rebase; ruleset 'org-merge' allows merge");
    }

    /// <summary>
    /// Given an unmanaged merge restriction that still leaves a method the policy allows,
    /// when the repo is planned,
    /// then there is no SP2102, only the SP2101 warning that it is stricter.
    /// </summary>
    [Fact(DisplayName = "🖐️ A merge restriction that leaves a method is only a warning")]
    public async Task Plan_MergeMethodLeft_OnlyWarns()
    {
        // Arrange
        _builder.WithBaseline(Baseline("allowedMergeTypes: [squash, rebase]"));
        _adapter.AddRepo(Repo).WithLive("allowedMergeTypes", """["rebase", "squash"]""")
            .Unmanaged.Add(new UnmanagedSetting("allowedMergeTypes", "repo merge settings", System.Text.Json.Nodes.JsonNode.Parse("""["squash"]""")));

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.UnmanagedStricter);
    }

    /// <summary>
    /// Given an unmanaged ruleset requiring 3 approvals and a policy requiring 2,
    /// when the repo is planned,
    /// then SP2101 warns that the effective result is stricter than declared.
    /// </summary>
    [Fact(DisplayName = "👎 An unmanaged stricter setting raises SP2101")]
    public async Task Plan_UnmanagedStricter_WarnsSP2101()
    {
        // Arrange
        _builder.WithBaseline(Baseline("minimumReviewers: { count: 2 }"));
        _adapter.AddRepo(Repo).WithLive("minimumReviewers", """{ "count": 2 }""")
            .Unmanaged.Add(new UnmanagedSetting("minimumReviewers", "ruleset 'org-protect'", System.Text.Json.Nodes.JsonNode.Parse("""{ "count": 3 }""")));

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        Diagnostic warning = result.Diagnostics.ShouldHaveSingleItem();
        warning.Code.ShouldBe(DiagnosticCodes.UnmanagedStricter);
        warning.Message.ShouldContain("org-protect");
        Single(result).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.UnmanagedStricter);
    }

    /// <summary>
    /// Given an unmanaged grant of admin to platform, as a project-level permission, and a policy giving platform write,
    /// when the repo is planned,
    /// then SP2101 says that access still applies, because Setpoint doesn't take it away.
    /// </summary>
    [Fact(DisplayName = "👎 An unmanaged grant above the policy's role raises SP2101")]
    public async Task Plan_UnmanagedAccessGrant_WarnsSP2101()
    {
        // Arrange
        _builder.WithBaseline(Baseline("access: { platform: write }"));
        _adapter.AddRepo(Repo).WithLive("access", """{ "platform": "write" }""")
            .Unmanaged.Add(new UnmanagedSetting("access", "permissions inherited from project Payments", System.Text.Json.Nodes.JsonNode.Parse("""{ "platform": "admin" }""")));

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        Diagnostic warning = result.Diagnostics.ShouldHaveSingleItem();
        warning.Code.ShouldBe(DiagnosticCodes.UnmanagedStricter);
        warning.Message.ShouldContain("that access still applies");
    }

    /// <summary>
    /// Given an unmanaged grant of read to platform, and a policy giving platform write,
    /// when the repo is planned,
    /// then nothing is raised, because the policy's role already covers it.
    /// </summary>
    [Fact(DisplayName = "👍 An unmanaged grant below the policy's role raises nothing")]
    public async Task Plan_UnmanagedAccessGrantBelow_RaisesNothing()
    {
        // Arrange
        _builder.WithBaseline(Baseline("access: { platform: write }"));
        _adapter.AddRepo(Repo).WithLive("access", """{ "platform": "write" }""")
            .Unmanaged.Add(new UnmanagedSetting("access", "permissions inherited from project Payments", System.Text.Json.Nodes.JsonNode.Parse("""{ "platform": "read" }""")));

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a rule the repo's platform plan doesn't offer, with a live value that differs from the policy,
    /// when the repo is planned,
    /// then the rule is left alone with its SP3002 warning, and the rest of the repo is still planned.
    /// </summary>
    [Fact(DisplayName = "👎 A rule the repo can't have is skipped with its warning")]
    public async Task Plan_UnavailableRule_IsSkippedWithWarning()
    {
        // Arrange
        _builder.WithBaseline(Baseline("minimumReviewers: { count: 2 }\n  requireCommentResolution: true"));
        _adapter.AddRepo(Repo).WithLive("minimumReviewers", """{ "count": 1, "resetOnPush": false }""")
            .Unavailable.Add(new RuleWarning("requireCommentResolution", Diagnostic.Warning(DiagnosticCodes.RuleUnavailable, "requireCommentResolution: not on this plan.")));

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.RuleUnavailable);
        Single(result).Changes.Select(c => c.Rule).ShouldBe(["minimumReviewers.count"]);
    }

    /// <summary>
    /// Given two repos that share the object a rule maps to, and declare it the same way,
    /// when they are planned,
    /// then the object is written once, by the first repo by path, and the other is in sync.
    /// </summary>
    [Fact(DisplayName = "👍 A shared object the repos agree on is planned once")]
    public async Task Plan_SharedObjectAgreed_PlannedOnce()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: true"));
        _adapter.SharedRules.Add("requireCommentResolution");
        _adapter.AddRepo(new RepoRef(_builder.TargetName, _builder.Owner, "web"));
        _adapter.AddRepo(Repo);

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Document.Repos.Select(r => $"{r.Path} {r.Status} {r.Objects.Count} {r.Changes.Count}").ShouldBe(
            [$"{_builder.Owner}/web InSync 0 0", $"{_builder.Owner}/api Changes 1 1"],
            ignoreOrder: true);
    }

    /// <summary>
    /// Given two repos that share the object a rule maps to, and a repo override that declares it differently,
    /// when they are planned,
    /// then both repos fail with SP0122, each naming the other, and nothing is planned for either.
    /// </summary>
    [Fact(DisplayName = "👎 A shared object declared differently is SP0122 on each repo")]
    public async Task Plan_SharedObjectConflict_FailsBoth()
    {
        // Arrange
        string target = _builder.TargetName;
        string owner = _builder.Owner;
        _builder.WithBaseline(Baseline("requireCommentResolution: true"))
            .WithRepoOverride($"{target}/{owner}/web", $"""
                version: 1
                layer: repo
                repo: {target}/{owner}/web
                rules:
                  requireCommentResolution: false
                """);
        _adapter.SharedRules.Add("requireCommentResolution");
        _adapter.AddRepo(new RepoRef(target, owner, "web")).WithLive("requireCommentResolution", "true");
        _adapter.AddRepo(Repo);

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Document.Repos.ShouldAllBe(r => r.Status == PlanRepoStatus.Error && r.Objects.Count == 0);
        Diagnostic[] errors = [.. result.Diagnostics.Where(d => d.Code == DiagnosticCodes.SharedObjectConflict)];
        errors.Length.ShouldBe(2);
        errors.ShouldContain(d => d.Message.Contains($"and {owner}/web declare it differently", StringComparison.Ordinal));
        errors.ShouldContain(d => d.Message.Contains($"and {owner}/api declare it differently", StringComparison.Ordinal));
    }

    /// <summary>
    /// Given a rule that two live objects match,
    /// when the repo is planned,
    /// then SP2201 warns and nothing is created or changed for that rule.
    /// </summary>
    [Fact(DisplayName = "👎 An ambiguous match raises SP2201 and is left alone")]
    public async Task Plan_Ambiguous_WarnsAndLeavesAlone()
    {
        // Arrange
        _builder.WithBaseline(Baseline("minimumReviewers: { count: 2 }"));
        _adapter.AddRepo(Repo).Ambiguous.Add(new AmbiguousMatch("minimumReviewers", ["ado:policy/31", "ado:policy/32"], "type + branch main"));

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        Single(result).Objects.ShouldBeEmpty();
        Diagnostic warning = result.Diagnostics.ShouldHaveSingleItem();
        warning.Code.ShouldBe(DiagnosticCodes.AmbiguousMatch);
        warning.Message.ShouldContain("ado:policy/31");
    }

    /// <summary>
    /// Given a live warning about a rule the policy sets, and one about a rule it doesn't,
    /// when the repo is planned,
    /// then only the warning about the declared rule is reported.
    /// </summary>
    [Fact(DisplayName = "🖐️ Live warnings are reported only for rules the policy sets")]
    public async Task Plan_RuleWarnings_OnlyForDeclaredRules()
    {
        // Arrange
        _builder.WithBaseline(Baseline("protectFromForcePushAndDelete: true"));
        FakeProviderAdapter.FakeRepo repo = _adapter.AddRepo(Repo);
        repo.Warnings.Add(new RuleWarning("protectFromForcePushAndDelete", Diagnostic.Warning(DiagnosticCodes.ForcePushAllowed, "creator allowed")));
        repo.Warnings.Add(new RuleWarning("requireCommentResolution", Diagnostic.Warning(DiagnosticCodes.ForcePushAllowed, "not declared")));

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("creator allowed");
    }

    /// <summary>
    /// Given an adapter that can't map the policy onto a repo, such as a pipeline that doesn't exist,
    /// when the repo is planned,
    /// then the repo fails with the adapter's code and message, not as a failed read.
    /// </summary>
    [Fact(DisplayName = "👎 A policy the adapter can't map fails the repo with the adapter's code")]
    public async Task Plan_MappingFails_ReportsAdapterCode()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: true"));
        _adapter.AddRepo(Repo).MappingFailure = new PolicyMappingException(DiagnosticCodes.PipelineNotFound, "requiredChecks: no pipeline is named 'ci'");

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        Single(result).Status.ShouldBe(PlanRepoStatus.Error);
        Diagnostic error = result.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe(DiagnosticCodes.PipelineNotFound);
        error.Message.ShouldContain("no pipeline is named 'ci'");
    }

    /// <summary>
    /// Given a rule whose live value the token can't see,
    /// when the repo is planned,
    /// then it is reported as unknown (SP2202) rather than as a change.
    /// </summary>
    [Fact(DisplayName = "🖐️ A value the token can't see is unknown, not a change")]
    public async Task Plan_UnknownValue_NotAChange()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: true"));
        _adapter.AddRepo(Repo).Unknown.Add("requireCommentResolution");

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        Single(result).Objects.ShouldBeEmpty();
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.LiveValueUnknown);
    }

    /// <summary>
    /// Given a repo the adapter lists as skipped, and one whose capabilities rule it out,
    /// when the plan runs,
    /// then both are skipped with their diagnostics and neither is read.
    /// </summary>
    [Fact(DisplayName = "🖐️ Skipped repos are listed as skipped and not read")]
    public async Task Plan_SkippedRepos_NotRead()
    {
        // Arrange
        FakeProviderAdapter.FakeRepo archived = _adapter.AddRepo(Repo with { Name = "old" });
        archived.ListingSkip = new Diagnostic(DiagnosticCodes.ArchivedRepoSkipped, DiagnosticSeverity.Info, "archived");
        archived.ReadFailure = new InvalidOperationException("should not be read");
        FakeProviderAdapter.FakeRepo privateRepo = _adapter.AddRepo(Repo with { Name = "secret" });
        privateRepo.CapabilitySkip = Diagnostic.Warning(DiagnosticCodes.PrivateRepoOnGitHubFree, "private repo on GitHub Free");
        privateRepo.ReadFailure = new InvalidOperationException("should not be read");

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Document.Repos.Select(r => r.Status).ShouldAllBe(s => s == PlanRepoStatus.Skipped);
        result.Document.Summary.Skipped.ShouldBe(2);
        result.Diagnostics.Select(d => d.Code).ShouldBe([DiagnosticCodes.ArchivedRepoSkipped, DiagnosticCodes.PrivateRepoOnGitHubFree], ignoreOrder: true);
    }

    /// <summary>
    /// Given a repo whose read fails,
    /// when the plan runs,
    /// then that repo is an error (SP1002) and the other repo is still planned.
    /// </summary>
    [Fact(DisplayName = "👎 A failed read fails that repo only")]
    public async Task Plan_ReadFails_OtherReposContinue()
    {
        // Arrange
        _adapter.AddRepo(Repo).ReadFailure = new HttpRequestException("503 Service Unavailable");
        _adapter.AddRepo(Repo with { Name = "web" }).WithLive("requireCommentResolution", "true");

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Document.Repos.Single(r => r.Path.EndsWith("/api", StringComparison.Ordinal)).Status.ShouldBe(PlanRepoStatus.Error);
        result.Document.Repos.Single(r => r.Path.EndsWith("/web", StringComparison.Ordinal)).Status.ShouldBe(PlanRepoStatus.InSync);
        Diagnostic error = result.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe(DiagnosticCodes.RepoReadFailed);
        error.Message.ShouldStartWith(Repo.ToString());
    }

    /// <summary>
    /// Given a target whose preflight fails,
    /// when the plan runs with and without --skip-unreachable,
    /// then SP1001 is an error by default and a warning with the flag, and the target is left out either way.
    /// </summary>
    [Theory(DisplayName = "👎 An unreachable target is an error unless skipped")]
    [InlineData(false, DiagnosticSeverity.Error)]
    [InlineData(true, DiagnosticSeverity.Warning)]
    public async Task Plan_TargetUnreachable_ReportsSP1001(bool skipUnreachable, DiagnosticSeverity expected)
    {
        // Arrange
        _adapter.AddRepo(Repo);
        _adapter.DescribeFailure = new HttpRequestException("No such host is known.");

        // Act
        PlanResult result = await PlanAsync(new PlanOptions(SkipUnreachable: skipUnreachable));

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.TargetUnreachable);
        diagnostic.Severity.ShouldBe(expected);
        result.Document.Targets.ShouldBeEmpty();
        result.Document.Repos.ShouldBeEmpty();
        result.Document.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.TargetUnreachable);
    }

    /// <summary>
    /// Given a target whose platform has no adapter,
    /// when the plan runs,
    /// then the target is reported unreachable.
    /// </summary>
    [Fact(DisplayName = "👎 A platform without an adapter is unreachable")]
    public async Task Plan_NoAdapter_Unreachable()
    {
        // Arrange
        var planner = new Planner([], [FakeProviderBlockDefinition.Instance], _time);

        // Act
        PlanResult result = await planner.PlanAsync(_builder.LoadInput(), new PlanOptions(), TestContext.Current.CancellationToken);

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.TargetUnreachable);
    }

    /// <summary>
    /// Given a listed repo outside the target's scope and one in its exclude list,
    /// when the plan runs,
    /// then neither appears in the plan.
    /// </summary>
    [Fact(DisplayName = "🖐️ Repos outside scope or excluded are left out")]
    public async Task Plan_OutOfScope_LeftOut()
    {
        // Arrange
        _builder.WithTargets($"""
            version: 1
            targets:
              {_builder.TargetName}:
                platform: github
                scope: [{_builder.Owner}/*]
                exclude: [{_builder.Owner}/legacy-*]
            """);
        _adapter.AddRepo(Repo);
        _adapter.AddRepo(Repo with { Name = "legacy-ledger" });
        _adapter.AddRepo(Repo with { Owner = "someone-else" });

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Document.Repos.ShouldHaveSingleItem().Path.ShouldBe($"{_builder.Owner}/api");
        result.Document.Targets.ShouldHaveSingleItem().RepoCount.ShouldBe(1);
    }

    /// <summary>
    /// Given a group whose glob matches no live repo, an exact selector and a repo override for repos that don't exist,
    /// when the plan runs,
    /// then the glob is SP0201 and the other two are SP0202, at their places in the files.
    /// </summary>
    [Fact(DisplayName = "👎 Selectors and overrides are checked against live repos")]
    public async Task Plan_SelectorsMatchNothing_Reported()
    {
        // Arrange
        string target = _builder.TargetName;
        string owner = _builder.Owner;
        _builder
            .WithGroup("web", $"""
                version: 1
                layer: group
                name: web
                selectors: [{target}/{owner}/web-*, {target}/{owner}/missing]
                rules:
                  requireCommentResolution: true
                """)
            .WithRepoOverride($"{target}/{owner}/gone", $"""
                version: 1
                layer: repo
                repo: {target}/{owner}/gone
                rules:
                  requireCommentResolution: true
                """);
        _adapter.AddRepo(Repo).WithLive("requireCommentResolution", "true");

        // Act
        PlanResult result = await PlanAsync();

        // Assert
        result.Diagnostics.Select(d => d.Code).ShouldBe([DiagnosticCodes.SelectorMatchesNothing, DiagnosticCodes.RepoNotFound, DiagnosticCodes.RepoNotFound], ignoreOrder: true);
        result.Diagnostics.ShouldAllBe(d => d.Location != null);
        result.Diagnostics.Single(d => d.Message.Contains("missing", StringComparison.Ordinal)).Location!.Line.ShouldBe(4);
    }

    /// <summary>
    /// Given a plan that raises a warning,
    /// when it runs with --strict,
    /// then the warning is an error in the result and in the plan document.
    /// </summary>
    [Fact(DisplayName = "👎 --strict turns plan warnings into errors")]
    public async Task Plan_Strict_PromotesWarnings()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: true"));
        _adapter.AddRepo(Repo).Unknown.Add("requireCommentResolution");

        // Act
        PlanResult result = await PlanAsync(new PlanOptions(Strict: true));

        // Assert
        result.HasErrors.ShouldBeTrue();
        Single(result).Diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    /// <summary>
    /// Given a plan over several repos,
    /// when it is made,
    /// then the header records the version, time, policy hash and commit, options and targets, and the summary adds up.
    /// </summary>
    [Fact(DisplayName = "👍 The plan header and summary describe the run")]
    public async Task Plan_Header_DescribesRun()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: true\n  allowedMergeTypes: [squash]"));
        _adapter.AddRepo(Repo);
        _adapter.AddRepo(Repo with { Name = "web" }).WithLive("requireCommentResolution", "true").WithLive("allowedMergeTypes", """["squash"]""");
        PlanInput input = _builder.LoadInput(commit: "3f9c2a1");

        // Act
        PlanDocument plan = (await new Planner([_adapter], [FakeProviderBlockDefinition.Instance], _time)
            .PlanAsync(input, new PlanOptions(Strict: true), TestContext.Current.CancellationToken)).Document;

        // Assert
        plan.Schema.ShouldBe(PlanDocument.SchemaUri);
        plan.FormatVersion.ShouldBe(1);
        plan.SetpointVersion.ShouldBe(SetpointVersion.Current);
        plan.CreatedAt.ShouldBe(_time.GetUtcNow());
        plan.Policy.ShouldBe(new PlanPolicy(PolicyHash.Compute(input.Sources), input.Sources.Count, "3f9c2a1"));
        plan.Options.Strict.ShouldBeTrue();
        PlanTarget target = plan.Targets.ShouldHaveSingleItem();
        target.Detected.ShouldBe(_adapter.Info.Detected);
        target.Identity.ShouldBe(_adapter.Info.Identity);
        plan.Summary.ShouldBe(new PlanSummary(ReposChanged: 1, Create: 2, Update: 0, Remove: 0, InSync: 1, Skipped: 0, Errors: 0, Unmanaged: 0, Warnings: 0));
        plan.HasChanges.ShouldBeTrue();
    }

    private Task<PlanResult> PlanAsync(PlanOptions? options = null) =>
        new Planner([_adapter], [FakeProviderBlockDefinition.Instance], _time)
            .PlanAsync(_builder.LoadInput(), options ?? new PlanOptions(), TestContext.Current.CancellationToken);

    private static PlanRepo Single(PlanResult result) => result.Document.Repos.ShouldHaveSingleItem();

    private static string Baseline(string rules, string? locked = null, string? extra = null) => $"""
        version: 1
        layer: baseline
        {(locked is null ? string.Empty : $"locked: {locked}")}
        rules:
          {rules}
        {extra ?? string.Empty}
        """;
}
