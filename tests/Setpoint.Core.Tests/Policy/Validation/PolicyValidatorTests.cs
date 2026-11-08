using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Validation;
using Setpoint.Core.Tests.TestData;
using Setpoint.Core.Tests.TestSupport;

namespace Setpoint.Core.Tests.Policy.Validation;

/// <summary>
/// Tests for <see cref="PolicyValidator"/>, the offline checks of <c>validate</c>: locking, selectors and targets,
/// rule values, auth methods, and group conflicts on the repos the policy names explicitly.
/// </summary>
public sealed class PolicyValidatorTests
{
    private readonly PolicyRepoBuilder _builder = new();
    private readonly PolicyValidator _sut = new([FakeProviderBlockDefinition.Instance]);

    /// <summary>
    /// Given the default policy repo,
    /// when it is validated,
    /// then nothing is reported.
    /// </summary>
    [Fact(DisplayName = "👍 A valid policy has no diagnostics")]
    public void Validate_ValidPolicy_ReportsNothing()
    {
        // Arrange
        // The builder's default repo is valid.

        // Act
        IReadOnlyList<Diagnostic> diagnostics = Validate();

        // Assert
        diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a locked baseline rule that a group declares,
    /// when it is validated,
    /// then SP0107 points at the group's declaration.
    /// </summary>
    [Fact(DisplayName = "👎 Overriding a locked rule is an error")]
    public void Validate_GroupOverridesLockedRule_ReportsLocked()
    {
        // Arrange
        _builder
            .WithBaseline("version: 1\nlayer: baseline\nlocked: [minimumReviewers]\nrules:\n  minimumReviewers: { count: 2 }")
            .WithGroup("web", Group("web", "rules:\n  minimumReviewers: { count: 1 }"));

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.LockedRuleOverridden);
        diagnostic.Location!.File.ShouldBe("groups/web.yml");
        diagnostic.Message.ShouldContain("'minimumReviewers' is locked by baseline.yml");
    }

    /// <summary>
    /// Given a locked list naming something that isn't a neutral rule,
    /// when it is validated,
    /// then SP0110 is reported.
    /// </summary>
    [Fact(DisplayName = "👎 locked: must name real rules")]
    public void Validate_UnknownLockedRule_ReportsUnknownLockedRule()
    {
        // Arrange
        _builder.WithBaseline("version: 1\nlayer: baseline\nlocked: [minReviewers]");

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.UnknownLockedRule);
    }

    /// <summary>
    /// Given malformed selectors,
    /// when they are validated,
    /// then each is reported as SP0112.
    /// </summary>
    [Theory(DisplayName = "👎 Syntax-checks selector globs offline")]
    [InlineData("acme/*")]
    [InlineData("*/acme/*")]
    [InlineData("TARGET/acme/**")]
    public void Validate_MalformedSelector_ReportsInvalidGlob(string selector)
    {
        // Arrange
        string resolved = selector.Replace("TARGET", _builder.TargetName, StringComparison.Ordinal);
        _builder.WithGroup("web", $"version: 1\nlayer: group\nname: web\nselectors: ['{resolved}']");

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.InvalidGlob);
    }

    /// <summary>
    /// Given a selector and a repo override for a target that targets.yml doesn't define,
    /// when they are validated,
    /// then each is reported as SP0113.
    /// </summary>
    [Fact(DisplayName = "👎 Selectors and overrides must name a known target")]
    public void Validate_UnknownTarget_ReportsUnknownTarget()
    {
        // Arrange
        _builder
            .WithGroup("web", "version: 1\nlayer: group\nname: web\nselectors: [nowhere/acme/*]")
            .WithRepoOverride("nowhere/acme/api", "version: 1\nlayer: repo\nrepo: nowhere/acme/api");

        // Act
        IReadOnlyList<Diagnostic> diagnostics = Validate();

        // Assert
        diagnostics.Select(d => d.Code).ShouldBe([DiagnosticCodes.UnknownTarget, DiagnosticCodes.UnknownTarget]);
    }

    /// <summary>
    /// Given an empty allowedMergeTypes,
    /// when it is validated,
    /// then SP0114 is reported.
    /// </summary>
    [Fact(DisplayName = "👎 An empty allowedMergeTypes is an error")]
    public void Validate_EmptyMergeTypes_ReportsNoMergeTypes()
    {
        // Arrange
        _builder.WithBaseline("version: 1\nlayer: baseline\nrules:\n  allowedMergeTypes: []");

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.NoMergeTypes);
        diagnostic.Location!.Line.ShouldBe(4);
    }

    /// <summary>
    /// Given minimumReviewers enabled without a count, or with zero,
    /// when it is validated,
    /// then SP0115 is reported.
    /// </summary>
    [Theory(DisplayName = "👎 minimumReviewers needs a count of at least 1")]
    [InlineData("{ resetOnPush: true }")]
    [InlineData("{ count: 0 }")]
    public void Validate_BadReviewerCount_ReportsInvalidReviewerCount(string rule)
    {
        // Arrange
        _builder.WithBaseline($"version: 1\nlayer: baseline\nrules:\n  minimumReviewers: {rule}");

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.InvalidReviewerCount);
    }

    /// <summary>
    /// Given minimumReviewers disabled without a count,
    /// when it is validated,
    /// then nothing is reported, because a removed setting needs no count.
    /// </summary>
    [Fact(DisplayName = "🖐️ A disabled minimumReviewers needs no count")]
    public void Validate_DisabledReviewersWithoutCount_IsValid()
    {
        // Arrange
        _builder.WithBaseline("version: 1\nlayer: baseline\nrules:\n  minimumReviewers: { enabled: false }");

        // Act
        IReadOnlyList<Diagnostic> diagnostics = Validate();

        // Assert
        diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// Given more than 10 reviewers in a layer that reaches a GitHub target, and the same in a group that only reaches Azure DevOps,
    /// when they are validated,
    /// then only the GitHub-reaching layer reports SP0116.
    /// </summary>
    [Fact(DisplayName = "👎 More than 10 reviewers is an error only where GitHub is reached")]
    public void Validate_ElevenReviewers_ReportsOnlyForGitHub()
    {
        // Arrange
        _builder
            .WithTargets($"version: 1\ntargets:\n  {_builder.TargetName}:\n    platform: github\n    scope: [{_builder.Owner}/*]\n  ado:\n    platform: azuredevops\n    url: https://dev.azure.com/acme")
            .WithBaseline("version: 1\nlayer: baseline\nrules:\n  minimumReviewers: { count: 11 }")
            .WithGroup("big", "version: 1\nlayer: group\nname: big\nselectors: [ado/Core/*]\nrules:\n  minimumReviewers: { count: 12 }");

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.TooManyReviewersForGitHub);
        diagnostic.Location!.File.ShouldBe("baseline.yml");
    }

    /// <summary>
    /// Given auth methods a platform doesn't support,
    /// when targets are validated,
    /// then each is reported as SP0117.
    /// </summary>
    [Theory(DisplayName = "👎 Rejects auth methods the platform doesn't support")]
    [InlineData("azuredevops", "https://ado.contoso.local/DefaultCollection", "entra")]
    [InlineData("azuredevops", "https://dev.azure.com/acme", "app\n    appId: 1")]
    [InlineData("github", null, "integrated")]
    [InlineData("github", null, "accessToken")]
    public void Validate_UnsupportedAuth_ReportsUnsupportedAuth(string platform, string? url, string auth)
    {
        // Arrange
        string urlLine = url is null ? "\n    scope: [acme/*]" : $"\n    url: {url}";
        _builder.WithTargets($"version: 1\ntargets:\n  t:\n    platform: {platform}{urlLine}\n    auth: {auth}");

        // Act
        IReadOnlyList<Diagnostic> diagnostics = Validate();

        // Assert
        diagnostics.ShouldContain(d => d.Code == DiagnosticCodes.UnsupportedAuth);
    }

    /// <summary>
    /// Given entra on an Azure DevOps Services URL,
    /// when targets are validated,
    /// then it is accepted.
    /// </summary>
    [Theory(DisplayName = "🖐️ Accepts entra on Azure DevOps Services")]
    [InlineData("https://dev.azure.com/acme")]
    [InlineData("https://acme.visualstudio.com")]
    public void Validate_EntraOnServices_IsValid(string url)
    {
        // Arrange
        _builder.WithTargets($"version: 1\ntargets:\n  t:\n    platform: azuredevops\n    url: {url}\n    auth: entra");

        // Act
        IReadOnlyList<Diagnostic> diagnostics = Validate();

        // Assert
        diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// Given targets missing what their platform needs,
    /// when they are validated,
    /// then each is reported as SP0118.
    /// </summary>
    [Theory(DisplayName = "👎 Reports targets missing scope, url or appId")]
    [InlineData("platform: github")]
    [InlineData("platform: azuredevops")]
    [InlineData("platform: github\n    scope: [acme/*]\n    auth: app")]
    [InlineData("platform: github\n    scope: [acme/*]\n    appId: 12")]
    public void Validate_IncompleteTarget_ReportsIncompleteTarget(string target)
    {
        // Arrange
        _builder.WithTargets($"version: 1\ntargets:\n  t:\n    {target}");

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.IncompleteTarget);
    }

    /// <summary>
    /// Given a boolean rule written as { enabled: false },
    /// when it is validated,
    /// then SP0119 is a warning, not an error.
    /// </summary>
    [Fact(DisplayName = "🖐️ Warns about { enabled } on a boolean rule")]
    public void Validate_EnabledFormOnBooleanRule_Warns()
    {
        // Arrange
        _builder.WithBaseline("version: 1\nlayer: baseline\nrules:\n  requireCommentResolution: { enabled: false }");

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.RedundantEnabledForm);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    /// <summary>
    /// Given a repo override for a repo outside its target's scope,
    /// when it is validated,
    /// then SP0120 warns that it never applies.
    /// </summary>
    [Fact(DisplayName = "🖐️ Warns about an override outside the target's scope")]
    public void Validate_OverrideOutsideScope_Warns()
    {
        // Arrange
        string repo = $"{_builder.TargetName}/someone-else/api";
        _builder.WithRepoOverride(repo, $"version: 1\nlayer: repo\nrepo: {repo}");

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.OutsideScope);
    }

    /// <summary>
    /// Given two groups that disagree on a rule and both match a repo that has an override,
    /// when it is validated,
    /// then the conflict is found offline as SP0111.
    /// </summary>
    [Fact(DisplayName = "👎 Finds group conflicts on repos the policy names")]
    public void Validate_GroupsConflictOnOverriddenRepo_ReportsConflict()
    {
        // Arrange
        string repo = $"{_builder.TargetName}/{_builder.Owner}/api";
        _builder
            .WithGroup("alpha", Group("alpha", "rules:\n  allowedMergeTypes: [squash]"))
            .WithGroup("beta", Group("beta", "rules:\n  allowedMergeTypes: [rebase]"))
            .WithRepoOverride(repo, $"version: 1\nlayer: repo\nrepo: {repo}");

        // Act
        Diagnostic diagnostic = Validate().ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe(DiagnosticCodes.GroupConflict);
    }

    private IReadOnlyList<Diagnostic> Validate() => _sut.Validate(_builder.Load());

    private string Group(string name, string body) =>
        $"version: 1\nlayer: group\nname: {name}\nselectors: [{_builder.TargetName}/{_builder.Owner}/*]\n{body}";
}
