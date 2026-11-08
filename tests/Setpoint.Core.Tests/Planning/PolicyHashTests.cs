using Setpoint.Core.Planning;
using Setpoint.Core.Policy.Loading;

namespace Setpoint.Core.Tests.Planning;

/// <summary>Tests for <see cref="PolicyHash"/>: stable across checkouts, sensitive to content and paths.</summary>
public sealed class PolicyHashTests
{
    private static readonly PolicySourceFile[] s_files =
    [
        new("targets.yml", "version: 1\ntargets: {}\n"),
        new("baseline.yml", "version: 1\nlayer: baseline\n"),
    ];

    /// <summary>
    /// Given the same files with Windows line endings and in another order,
    /// when they are hashed,
    /// then the hash is the same.
    /// </summary>
    [Fact(DisplayName = "👍 Line endings and file order don't change the hash")]
    public void Compute_CrlfAndOrder_Same()
    {
        // Arrange
        PolicySourceFile[] windows = [.. s_files.Reverse().Select(f => f with { Text = f.Text.Replace("\n", "\r\n", StringComparison.Ordinal) })];

        // Act
        string unix = PolicyHash.Compute(s_files);
        string other = PolicyHash.Compute(windows);

        // Assert
        other.ShouldBe(unix);
    }

    /// <summary>
    /// Given the files with one character of content changed, or one file renamed,
    /// when they are hashed,
    /// then the hash changes.
    /// </summary>
    [Fact(DisplayName = "👎 A content change or a rename changes the hash")]
    public void Compute_ContentOrPathChanged_Differs()
    {
        // Arrange
        PolicySourceFile[] edited = [s_files[0], s_files[1] with { Text = "version: 1\nlayer: baselin3\n" }];
        PolicySourceFile[] renamed = [s_files[0], s_files[1] with { Path = "groups/baseline.yml" }];

        // Act
        string original = PolicyHash.Compute(s_files);

        // Assert
        PolicyHash.Compute(edited).ShouldNotBe(original);
        PolicyHash.Compute(renamed).ShouldNotBe(original);
    }
}
