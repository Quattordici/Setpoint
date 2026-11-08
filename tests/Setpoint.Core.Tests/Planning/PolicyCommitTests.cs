using Setpoint.Core.Planning;

namespace Setpoint.Core.Tests.Planning;

/// <summary>Tests for <see cref="PolicyCommit"/>, against hand-made <c>.git</c> directories.</summary>
public sealed class PolicyCommitTests : IDisposable
{
    private const string Sha = "3f9c2a1e5b7d9f0a1c3e5b7d9f0a1c3e5b7d9f0a";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("setpoint-commit-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>
    /// Given a checkout with a detached HEAD, as pipelines check out,
    /// when the commit is looked up from a subdirectory,
    /// then it is the HEAD commit.
    /// </summary>
    [Fact(DisplayName = "👍 Reads a detached HEAD from a subdirectory")]
    public void Find_DetachedHead_ReturnsSha()
    {
        // Arrange
        Git("HEAD", Sha);
        string policy = Directory.CreateDirectory(Path.Combine(_root.FullName, "policy")).FullName;

        // Act
        string? commit = PolicyCommit.Find(policy);

        // Assert
        commit.ShouldBe(Sha);
    }

    /// <summary>
    /// Given a HEAD that points at a branch, stored loose or only in packed-refs,
    /// when the commit is looked up,
    /// then it is the branch's commit.
    /// </summary>
    [Theory(DisplayName = "👍 Follows a branch ref, loose or packed")]
    [InlineData(false)]
    [InlineData(true)]
    public void Find_BranchRef_ReturnsSha(bool packed)
    {
        // Arrange
        Git("HEAD", "ref: refs/heads/main");
        if (packed)
        {
            Git("packed-refs", $"# pack-refs with: peeled fully-peeled sorted\n{Sha} refs/heads/main\n");
        }
        else
        {
            Git("refs/heads/main", Sha);
        }

        // Act
        string? commit = PolicyCommit.Find(_root.FullName);

        // Assert
        commit.ShouldBe(Sha);
    }

    /// <summary>
    /// Given a directory outside any git checkout, or a branch with no commits yet,
    /// when the commit is looked up,
    /// then there is none.
    /// </summary>
    [Fact(DisplayName = "🖐️ No checkout or no commit gives null")]
    public void Find_NoCommit_ReturnsNull()
    {
        // Arrange
        string outside = _root.FullName;
        string unborn = Directory.CreateDirectory(Path.Combine(_root.FullName, "unborn")).FullName;
        Directory.CreateDirectory(Path.Combine(unborn, ".git"));
        File.WriteAllText(Path.Combine(unborn, ".git", "HEAD"), "ref: refs/heads/main\n");

        // Act
        string? none = PolicyCommit.Find(outside);
        string? empty = PolicyCommit.Find(unborn);

        // Assert
        none.ShouldBeNull();
        empty.ShouldBeNull();
    }

    private void Git(string path, string content)
    {
        string file = Path.Combine(_root.FullName, ".git", path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content + "\n");
    }
}
