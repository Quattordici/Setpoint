using Setpoint.Adapters.AzureDevOps.Policy;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Adapters.AzureDevOps.Tests.Policy;

/// <summary>
/// Tests for <see cref="AzureDevOpsProviderBlock"/> as the loader reads it: every option, ~ and strictness.
/// </summary>
public sealed class AzureDevOpsProviderBlockTests
{
    private const string Targets = """
        version: 1
        targets:
          ado:
            platform: azuredevops
            url: https://dev.azure.com/acme
        """;

    private readonly PolicyRepoLoader _loader = new([AzureDevOpsProviderBlockDefinition.Instance]);

    /// <summary>
    /// Given an azuredevops block that sets every option,
    /// when the baseline is loaded,
    /// then each option reads as written.
    /// </summary>
    [Fact(DisplayName = "👍 Reads every azuredevops option")]
    public void Load_FullBlock_ReadsEveryOption()
    {
        // Arrange
        int validDuration = new Bogus.Randomizer().Int(1, 1440);
        string baseline = $"""
            version: 1
            layer: baseline
            azuredevops:
              minimumReviewers:
                creatorVoteCounts: true
                allowDownvotes: true
                blockLastPusherVote: true
              requiredChecks:
                validDuration: {validDuration}
                pathFilters: [/src/*, "!/docs/*"]
                statusChecks:
                  - name: sonar
                    genre: quality
              allowedMergeTypes:
                allowRebaseMerge: true
            """;

        // Act
        AzureDevOpsProviderBlock block = LoadBlock(baseline);

        // Assert
        block.MinimumReviewers.Value.ShouldBe(new AzureDevOpsMinimumReviewersOptions { CreatorVoteCounts = true, AllowDownvotes = true, BlockLastPusherVote = true });
        block.RequiredChecks.Value.ValidDuration.ShouldBe(validDuration);
        block.RequiredChecks.Value.PathFilters.ShouldBe(["/src/*", "!/docs/*"]);
        block.RequiredChecks.Value.StatusChecks.ShouldHaveSingleItem().ShouldBe(new AzureDevOpsStatusCheck { Name = "sonar", Genre = "quality" });
        block.AllowedMergeTypes.Value.AllowRebaseMerge.ShouldBeTrue();
    }

    /// <summary>
    /// Given an azuredevops entry set to ~,
    /// when it is loaded,
    /// then that entry is unmanaged.
    /// </summary>
    [Fact(DisplayName = "🖐️ An entry set to ~ is unmanaged")]
    public void Load_TildeEntry_IsUnmanaged()
    {
        // Arrange
        string baseline = """
            version: 1
            layer: baseline
            azuredevops:
              minimumReviewers: ~
            """;

        // Act
        AzureDevOpsProviderBlock block = LoadBlock(baseline);

        // Assert
        block.MinimumReviewers.State.ShouldBe(DeclaredState.Unmanaged);
    }

    /// <summary>
    /// Given a status check without a name,
    /// when it is loaded,
    /// then it fails with SP0106 naming the missing key.
    /// </summary>
    [Fact(DisplayName = "👎 Requires a name on each status check")]
    public void Load_StatusCheckWithoutName_ReportsInvalidValue()
    {
        // Arrange
        string baseline = """
            version: 1
            layer: baseline
            azuredevops:
              requiredChecks:
                statusChecks:
                  - genre: quality
            """;

        // Act
        PolicyLoadResult result = Load(baseline);

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.InvalidValue);
        diagnostic.Message.ShouldContain("name");
    }

    private PolicyLoadResult Load(string baseline) =>
        _loader.Load([new PolicySourceFile("targets.yml", Targets), new PolicySourceFile("baseline.yml", baseline)]);

    private AzureDevOpsProviderBlock LoadBlock(string baseline)
    {
        PolicyLoadResult result = Load(baseline);
        result.Diagnostics.ShouldBeEmpty();
        return result.Repo!.Baseline.Content.Providers["azuredevops"].ShouldBeOfType<AzureDevOpsProviderBlock>();
    }
}
