using Setpoint.Adapters.AzureDevOps.Policy;
using Setpoint.Adapters.GitHub.Policy;
using Setpoint.Cli.Composition;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Cli.Tests.Policy;

/// <summary>
/// Loads the policy schema prototype (docs/prototypes/PROTOTYPE-policy-schema on the planning branch,
/// copied verbatim into TestData) with every compiled-in adapter, as the build ticket requires.
/// </summary>
public sealed class PrototypePolicyRepoTests
{
    private static readonly string s_prototypeDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "prototype-policy-repo");

    private readonly PolicyRepoLoader _sut = new(ProviderBlocks.All);

    /// <summary>
    /// Given the prototype policy repo,
    /// when it is loaded with the real adapters' provider blocks,
    /// then it loads with no diagnostics at all.
    /// </summary>
    [Fact(DisplayName = "👍 The prototype policy repo loads cleanly")]
    public void LoadDirectory_Prototype_HasNoDiagnostics()
    {
        // Arrange
        // The prototype is copied to the test output directory.

        // Act
        PolicyLoadResult result = _sut.LoadDirectory(s_prototypeDirectory);

        // Assert
        result.Diagnostics.ShouldBeEmpty();
        result.Repo.ShouldNotBeNull();
    }

    /// <summary>
    /// Given the prototype policy repo,
    /// when it is loaded,
    /// then its targets, locked rules, neutral rules and both provider blocks read as written.
    /// </summary>
    [Fact(DisplayName = "👍 The prototype's targets and baseline read as written")]
    public void LoadDirectory_Prototype_ReadsTargetsAndBaseline()
    {
        // Arrange
        // The prototype is copied to the test output directory.

        // Act
        PolicyRepo repo = _sut.LoadDirectory(s_prototypeDirectory).Repo!;

        // Assert
        repo.Targets.Content.Targets.Keys.ShouldBe(["ado-onprem", "ado-cloud", "github"], ignoreOrder: true);
        repo.Targets.Content.Targets["ado-onprem"].Auth.ShouldBe(AuthMethod.Integrated);

        PolicyLayer baseline = repo.Baseline.Content;
        baseline.Locked.ShouldBe(["minimumReviewers", "protectFromForcePushAndDelete"]);
        baseline.Rules.MinimumReviewers.Value.ShouldBe(new MinimumReviewersRule { Count = 2, ResetOnPush = true });
        baseline.Rules.AllowedMergeTypes.Value.ShouldBe([MergeType.Squash, MergeType.Rebase]);

        var ado = baseline.Providers["azuredevops"].ShouldBeOfType<AzureDevOpsProviderBlock>();
        ado.MinimumReviewers.Value.ShouldBe(new AzureDevOpsMinimumReviewersOptions { CreatorVoteCounts = false, AllowDownvotes = false });
        var github = baseline.Providers["github"].ShouldBeOfType<GitHubProviderBlock>();
        github.PullRequest.Value.RequireCodeOwnerReview.ShouldBeFalse();
    }

    /// <summary>
    /// Given the prototype policy repo,
    /// when it is loaded,
    /// then the payments group and the legacy-ledger override carry their selectors, unmanaged rule and removal.
    /// </summary>
    [Fact(DisplayName = "👍 The prototype's group and repo override read as written")]
    public void LoadDirectory_Prototype_ReadsGroupAndOverride()
    {
        // Arrange
        // The prototype is copied to the test output directory.

        // Act
        PolicyRepo repo = _sut.LoadDirectory(s_prototypeDirectory).Repo!;

        // Assert
        PolicyLayer payments = repo.Groups.ShouldHaveSingleItem().Content;
        payments.Selectors.ShouldBe(["ado-onprem/Payments/*", "github/quattordici/payments-*"]);
        payments.Providers["azuredevops"].ShouldBeOfType<AzureDevOpsProviderBlock>().RequiredChecks.Value.ValidDuration.ShouldBe(720);

        PolicyFile<PolicyLayer> legacy = repo.RepoOverrides.ShouldHaveSingleItem();
        legacy.Path.ShouldBe("repos/ado-onprem/Payments/legacy-ledger.yml");
        legacy.Content.Rules.RequiredChecks.State.ShouldBe(DeclaredState.Unmanaged);
        legacy.Content.Rules.RequireCommentResolution.Value.Enabled.ShouldBeFalse();
    }
}
