using Setpoint.Adapters.GitHub.Policy;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Loading;

namespace Setpoint.Adapters.GitHub.Tests.Policy;

/// <summary>
/// Tests for <see cref="GitHubProviderBlock"/> as the loader reads it: every option, defaults, and strictness.
/// </summary>
public sealed class GitHubProviderBlockTests
{
    private const string Targets = """
        version: 1
        targets:
          github:
            platform: github
            scope: [acme/*]
        """;

    private readonly PolicyRepoLoader _loader = new([GitHubProviderBlockDefinition.Instance]);

    /// <summary>
    /// Given a github block that sets every option,
    /// when the baseline is loaded,
    /// then each option reads as written.
    /// </summary>
    [Fact(DisplayName = "👍 Reads every github option")]
    public void Load_FullBlock_ReadsEveryOption()
    {
        // Arrange
        long appId = new Bogus.Randomizer().Long(1, 9_999_999);
        string baseline = $"""
            version: 1
            layer: baseline
            github:
              pullRequest:
                requireCodeOwnerReview: true
                requireLastPushApproval: true
              requiredChecks:
                strict: true
                integrationIds:
                  ci: {appId}
              bypassActors:
                - actorType: team
                  actorId: 42
                  bypassMode: pullRequest
                - actorType: organizationAdmin
            """;

        // Act
        GitHubProviderBlock block = LoadBlock(baseline);

        // Assert
        block.PullRequest.Value.ShouldBe(new GitHubPullRequestOptions { RequireCodeOwnerReview = true, RequireLastPushApproval = true });
        block.RequiredChecks.Value.Strict.ShouldBeTrue();
        block.RequiredChecks.Value.IntegrationIds!["ci"].ShouldBe(appId);
        block.BypassActors.Value.Count.ShouldBe(2);
        block.BypassActors.Value[0].ShouldBe(new GitHubBypassActor { ActorType = GitHubBypassActorType.Team, ActorId = 42, BypassMode = GitHubBypassMode.PullRequest });
    }

    /// <summary>
    /// Given a bypass actor without a bypass mode,
    /// when it is loaded,
    /// then the mode defaults to always.
    /// </summary>
    [Fact(DisplayName = "🖐️ Bypass mode defaults to always")]
    public void Load_BypassActorWithoutMode_DefaultsToAlways()
    {
        // Arrange
        string baseline = """
            version: 1
            layer: baseline
            github:
              bypassActors:
                - actorType: organizationAdmin
            """;

        // Act
        GitHubProviderBlock block = LoadBlock(baseline);

        // Assert
        block.BypassActors.Value.ShouldHaveSingleItem().BypassMode.ShouldBe(GitHubBypassMode.Always);
    }

    /// <summary>
    /// Given a github block with a key from another platform,
    /// when it is loaded,
    /// then it fails with an unknown key error.
    /// </summary>
    [Fact(DisplayName = "👎 Rejects an Azure DevOps option in the github block")]
    public void Load_ForeignKey_ReportsUnknownKey()
    {
        // Arrange
        string baseline = """
            version: 1
            layer: baseline
            github:
              minimumReviewers:
                creatorVoteCounts: true
            """;

        // Act
        PolicyLoadResult result = Load(baseline);

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.UnknownKey);
    }

    private PolicyLoadResult Load(string baseline) =>
        _loader.Load([new PolicySourceFile("targets.yml", Targets), new PolicySourceFile("baseline.yml", baseline)]);

    private GitHubProviderBlock LoadBlock(string baseline)
    {
        PolicyLoadResult result = Load(baseline);
        result.Diagnostics.ShouldBeEmpty();
        return result.Repo!.Baseline.Content.Providers["github"].ShouldBeOfType<GitHubProviderBlock>();
    }
}
