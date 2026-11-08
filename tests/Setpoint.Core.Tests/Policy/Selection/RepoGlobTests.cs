using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Selection;

namespace Setpoint.Core.Tests.Policy.Selection;

/// <summary>Tests for <see cref="RepoGlob"/>: syntax checks, wildcards and case-insensitive matching.</summary>
public sealed class RepoGlobTests
{
    /// <summary>
    /// Given selectors with wildcards in different parts,
    /// when matched against a repo,
    /// then * and ? match within a part, ignoring case.
    /// </summary>
    [Theory(DisplayName = "👍 Matches * and ? within a part, ignoring case")]
    [InlineData("ado/Payments/*", true)]
    [InlineData("ado/payments/LEDGER", true)]
    [InlineData("ado/*/led?er", true)]
    [InlineData("ado/Pay*/l*", true)]
    [InlineData("ado/Payments/api-*", false)]
    [InlineData("github/Payments/*", false)]
    public void Matches_Selector_MatchesRepo(string selector, bool expected)
    {
        // Arrange
        RepoGlob glob = RepoGlob.TryParse(selector, 3, out _)!;

        // Act
        bool matches = glob.Matches(new RepoRef("ado", "Payments", "ledger"));

        // Assert
        matches.ShouldBe(expected);
    }

    /// <summary>
    /// Given globs with the wrong number of parts, an empty part or unsupported syntax,
    /// when parsed,
    /// then each is rejected with a reason.
    /// </summary>
    [Theory(DisplayName = "👎 Rejects malformed globs")]
    [InlineData("ado/Payments")]
    [InlineData("ado/Payments/api/extra")]
    [InlineData("ado//api")]
    [InlineData("ado/**/api")]
    [InlineData("ado/Payments/[ab]*")]
    [InlineData("ado/Payments/{a,b}")]
    public void TryParse_Malformed_ReturnsError(string selector)
    {
        // Arrange
        // The selector comes from the theory data.

        // Act
        RepoGlob? glob = RepoGlob.TryParse(selector, 3, out string? error);

        // Assert
        glob.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Given globs with and without wildcards,
    /// when parsed,
    /// then only the wildcard-free one is exact.
    /// </summary>
    [Theory(DisplayName = "🖐️ A glob without wildcards is exact")]
    [InlineData("ado/Payments/ledger", true)]
    [InlineData("ado/Payments/*", false)]
    public void IsExact_Glob_ReflectsWildcards(string selector, bool expected)
    {
        // Arrange
        RepoGlob glob = RepoGlob.TryParse(selector, 3, out _)!;

        // Act
        bool exact = glob.IsExact;

        // Assert
        exact.ShouldBe(expected);
    }
}
