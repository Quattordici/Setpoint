namespace Setpoint.Adapters.GitHub.Tests;

/// <summary>Tests for <see cref="GitHubEndpoint"/>: where a target's API lives, and how the preflight names it.</summary>
public sealed class GitHubEndpointTests
{
    /// <summary>
    /// Given a target url, or none,
    /// when the API root is resolved,
    /// then github.com uses api.github.com and anything else follows the Enterprise Server /api/v3 convention.
    /// </summary>
    [Theory(DisplayName = "👍 Resolves the API root for github.com and Enterprise Server")]
    [InlineData(null, "https://api.github.com/")]
    [InlineData("https://github.com", "https://api.github.com/")]
    [InlineData("https://api.github.com/", "https://api.github.com/")]
    [InlineData("https://github.example.com", "https://github.example.com/api/v3/")]
    [InlineData("https://github.example.com/api/v3", "https://github.example.com/api/v3/")]
    public void ApiRoot_TargetUrl_ResolvesRoot(string? url, string expected)
    {
        // Act
        Uri root = GitHubEndpoint.ApiRoot(url is null ? null : new Uri(url));

        // Assert
        root.ShouldBe(new Uri(expected));
    }

    /// <summary>
    /// Given the API root and an optional Enterprise Server version header,
    /// when the target is described,
    /// then it reads as github.com or as the server and its version.
    /// </summary>
    [Theory(DisplayName = "🖐️ Names github.com and Enterprise Server targets")]
    [InlineData("https://api.github.com/", null, "github.com")]
    [InlineData("https://ghe.example.com/api/v3/", "3.17.2", "GitHub Enterprise Server 3.17.2")]
    [InlineData("https://ghe.example.com/api/v3/", null, "GitHub (ghe.example.com)")]
    public void Describe_RootAndVersion_NamesTarget(string root, string? version, string expected)
    {
        // Act
        string detected = GitHubEndpoint.Describe(new Uri(root), version);

        // Assert
        detected.ShouldBe(expected);
    }
}
