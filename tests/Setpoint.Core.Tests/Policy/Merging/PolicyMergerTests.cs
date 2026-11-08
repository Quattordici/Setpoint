using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Merging;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Tests.TestData;
using Setpoint.Core.Tests.TestSupport;

namespace Setpoint.Core.Tests.Policy.Merging;

/// <summary>
/// Tests for <see cref="PolicyMerger"/>: wholesale replacement, unmanaged and removal, group conflicts,
/// locking, provider entries and the layer each effective value comes from.
/// </summary>
public sealed class PolicyMergerTests
{
    private readonly PolicyRepoBuilder _builder = new();
    private readonly List<Diagnostic> _diagnostics = [];

    private RepoRef Repo => new(_builder.TargetName, _builder.Owner, "api");

    /// <summary>
    /// Given a baseline object rule and a repo override of the same rule that leaves out one field,
    /// when they are merged,
    /// then the override's value replaces the baseline's wholesale, so the left-out field takes its default.
    /// </summary>
    [Fact(DisplayName = "👍 A lower layer replaces an object rule wholesale")]
    public void Merge_ObjectRuleOverridden_ReplacesWholesale()
    {
        // Arrange
        _builder
            .WithBaseline(Baseline("minimumReviewers: { count: 2, resetOnPush: true }"))
            .WithRepoOverride(Repo.ToString(), Override("minimumReviewers: { count: 1 }"));

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        effective.Get<MinimumReviewersRule>("minimumReviewers").ShouldBe(new MinimumReviewersRule { Count = 1, ResetOnPush = false });
        effective.Find("minimumReviewers")!.Source.Kind.ShouldBe(LayerKind.Repo);
    }

    /// <summary>
    /// Given a baseline list rule and a group that sets the same list,
    /// when they are merged,
    /// then the group's list replaces the baseline's instead of being appended to it.
    /// </summary>
    [Fact(DisplayName = "👍 A lower layer replaces a list, never appends")]
    public void Merge_ListRuleOverridden_ReplacesList()
    {
        // Arrange
        _builder
            .WithBaseline(Baseline("allowedMergeTypes: [squash, rebase]"))
            .WithGroup("web", Group("web", "allowedMergeTypes: [merge]"));

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        effective.Get<IReadOnlyList<MergeType>>("allowedMergeTypes").ShouldBe([MergeType.Merge]);
        effective.Find("allowedMergeTypes")!.Source.Label.ShouldBe("group web");
    }

    /// <summary>
    /// Given a baseline rule that a group sets to ~,
    /// when they are merged,
    /// then the rule is unmanaged for that repo.
    /// </summary>
    [Fact(DisplayName = "🖐️ ~ makes a rule unmanaged from that layer down")]
    public void Merge_GroupSetsTilde_RuleIsUnmanaged()
    {
        // Arrange
        _builder
            .WithBaseline(Baseline("requiredChecks: [{ name: ci }]"))
            .WithGroup("web", Group("web", "requiredChecks: ~"));

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        EffectiveRule rule = effective.Find("requiredChecks")!;
        rule.IsUnmanaged.ShouldBeTrue();
        rule.Source.Label.ShouldBe("group web");
    }

    /// <summary>
    /// Given a group that sets a rule to ~ and a repo override that sets it again,
    /// when they are merged,
    /// then the repo override's value applies.
    /// </summary>
    [Fact(DisplayName = "🖐️ A lower layer can manage a rule again after ~")]
    public void Merge_OverrideAfterTilde_ValueApplies()
    {
        // Arrange
        _builder
            .WithGroup("web", Group("web", "requiredChecks: ~"))
            .WithRepoOverride(Repo.ToString(), Override("requiredChecks: [{ name: build }]"));

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        effective.Get<IReadOnlyList<RequiredCheck>>("requiredChecks").ShouldHaveSingleItem().Name.ShouldBe("build");
    }

    /// <summary>
    /// Given rules set to false and to enabled: false,
    /// when they are merged,
    /// then each effective rule says the setting must be absent.
    /// </summary>
    [Fact(DisplayName = "🖐️ false and enabled: false mean the setting must be absent")]
    public void Merge_DisabledRules_MustBeAbsent()
    {
        // Arrange
        _builder.WithBaseline(Baseline("requireCommentResolution: false\n  minimumReviewers: { enabled: false }"));

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        effective.Find("requireCommentResolution")!.MustBeAbsent.ShouldBeTrue();
        effective.Find("minimumReviewers")!.MustBeAbsent.ShouldBeTrue();
    }

    /// <summary>
    /// Given two groups that both match the repo and set the same rule to different values,
    /// when they are merged,
    /// then SP0111 names both groups.
    /// </summary>
    [Fact(DisplayName = "👎 Two groups setting the same rule differently is an error")]
    public void Merge_GroupsDisagree_ReportsConflict()
    {
        // Arrange
        _builder
            .WithGroup("alpha", Group("alpha", "allowedMergeTypes: [squash]"))
            .WithGroup("beta", Group("beta", "allowedMergeTypes: [merge]"));

        // Act
        Merge();

        // Assert
        Diagnostic diagnostic = _diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.GroupConflict);
        diagnostic.Message.ShouldContain("'alpha'");
        diagnostic.Message.ShouldContain("'beta'");
    }

    /// <summary>
    /// Given two matching groups that set the same rule to the same value,
    /// when they are merged,
    /// then there is no conflict and the value applies.
    /// </summary>
    [Fact(DisplayName = "🖐️ Two groups agreeing on a value is not a conflict")]
    public void Merge_GroupsAgree_NoConflict()
    {
        // Arrange
        _builder
            .WithGroup("alpha", Group("alpha", "allowedMergeTypes: [squash]"))
            .WithGroup("beta", Group("beta", "allowedMergeTypes: [squash]"));

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        _diagnostics.ShouldBeEmpty();
        effective.Get<IReadOnlyList<MergeType>>("allowedMergeTypes").ShouldBe([MergeType.Squash]);
        effective.Groups.ShouldBe(["alpha", "beta"]);
    }

    /// <summary>
    /// Given a group whose selectors don't match the repo,
    /// when the repo is merged,
    /// then the group's rules don't apply.
    /// </summary>
    [Fact(DisplayName = "🖐️ A group applies only to repos its selectors match")]
    public void Merge_GroupDoesNotMatch_IsIgnored()
    {
        // Arrange
        _builder.WithGroup("other", $"""
            version: 1
            layer: group
            name: other
            selectors: [{_builder.TargetName}/someone-else/*]
            rules:
              allowedMergeTypes: [merge]
            """);

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        effective.Find("allowedMergeTypes").ShouldBeNull();
        effective.Groups.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a locked baseline rule that a repo override declares anyway,
    /// when they are merged,
    /// then the baseline's value stands and the rule is marked locked.
    /// </summary>
    [Fact(DisplayName = "👎 A locked rule keeps the baseline value")]
    public void Merge_LockedRuleOverridden_KeepsBaseline()
    {
        // Arrange
        _builder
            .WithBaseline("""
                version: 1
                layer: baseline
                locked: [protectFromForcePushAndDelete]
                rules:
                  protectFromForcePushAndDelete: true
                """)
            .WithRepoOverride(Repo.ToString(), Override("protectFromForcePushAndDelete: false"));

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        EffectiveRule rule = effective.Find("protectFromForcePushAndDelete")!;
        rule.Value.ShouldBe(new BooleanRule(true));
        rule.Locked.ShouldBeTrue();
        rule.Source.Kind.ShouldBe(LayerKind.Baseline);
    }

    /// <summary>
    /// Given a provider-block entry in the baseline and a different value in a group,
    /// when they are merged,
    /// then the entry is keyed under the provider and replaced by the group.
    /// </summary>
    [Fact(DisplayName = "👍 Provider-block entries merge by key like neutral rules")]
    public void Merge_ProviderEntry_MergesByKey()
    {
        // Arrange
        _builder
            .WithBaseline("version: 1\nlayer: baseline\nfake:\n  minimumReviewers: { flag: false }")
            .WithGroup("web", $"version: 1\nlayer: group\nname: web\nselectors: [{_builder.TargetName}/{_builder.Owner}/*]\nfake:\n  minimumReviewers: {{ flag: true }}");

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        effective.Get<FakeOptions>("fake.minimumReviewers").ShouldBe(new FakeOptions { Flag = true });
    }

    /// <summary>
    /// Given a baseline rule,
    /// when it is merged,
    /// then the effective rule records the baseline file and line it was declared on.
    /// </summary>
    [Fact(DisplayName = "🖐️ Each effective value records its layer and line")]
    public void Merge_BaselineRule_RecordsSource()
    {
        // Arrange
        // The default baseline declares requireCommentResolution on line 4.

        // Act
        EffectivePolicy effective = Merge();

        // Assert
        EffectiveRule rule = effective.Find("requireCommentResolution")!;
        rule.Source.ShouldBe(new LayerRef(LayerKind.Baseline, null, new SourceLocation("baseline.yml", 4, 3)));
    }

    private EffectivePolicy Merge() =>
        new PolicyMerger(_builder.Load(), [FakeProviderBlockDefinition.Instance]).Merge(Repo, _diagnostics);

    private static string Baseline(string rules) => $"version: 1\nlayer: baseline\nrules:\n  {rules}";

    private string Group(string name, string rules) =>
        $"version: 1\nlayer: group\nname: {name}\nselectors: [{_builder.TargetName}/{_builder.Owner}/*]\nrules:\n  {rules}";

    private string Override(string rules) => $"version: 1\nlayer: repo\nrepo: {Repo}\nrules:\n  {rules}";
}
