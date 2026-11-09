using System.Text.RegularExpressions;

namespace Setpoint.Http.Tests;

/// <summary>
/// Recorded fixtures are committed to a public repo, so none may hold a credential. The redactor masks the exact
/// values Setpoint was given; this scan is the second line of defence, for anything shaped like a credential.
/// </summary>
public sealed partial class FixtureSecretTests
{
    /// <summary>
    /// Given every committed fixture file under tests/,
    /// when it is scanned for credential shapes,
    /// then none is found.
    /// </summary>
    [Fact(DisplayName = "🔒 No committed fixture holds anything shaped like a credential")]
    public void Fixtures_Committed_HoldNoSecrets()
    {
        // Arrange
        string tests = Path.Combine(RepoRoot(), "tests");
        string[] files = [.. Directory.EnumerateFiles(tests, "*", SearchOption.AllDirectories)
            .Where(f => f.Split(Path.DirectorySeparatorChar).Contains("Fixtures"))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))];

        // Act
        string[] hits = [.. files.SelectMany(file => FindSecrets(File.ReadAllText(file))
            .Select(match => $"{Path.GetRelativePath(tests, file)}: {match[..Math.Min(match.Length, 12)]}...")) ];

        // Assert
        files.ShouldNotBeEmpty();
        hits.ShouldBeEmpty("Delete these fixtures, fix the redactor, and record again.");
    }

    /// <summary>
    /// Given text holding each credential shape the scan knows,
    /// when it is scanned,
    /// then each one is found, so the scan above can't pass by matching nothing.
    /// </summary>
    [Theory(DisplayName = "🔒 The fixture scan finds each credential shape")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghs_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrst")]
    [InlineData("\"Authorization\": \"Basic OmFiYw==\"")]
    public void FindSecrets_CredentialShape_IsFound(string secret)
    {
        // Arrange
        string text = $$"""{ "body": "before {{secret}} after" }""";

        // Act
        string[] hits = [.. FindSecrets(text)];

        // Assert
        hits.ShouldNotBeEmpty();
    }

    /// <summary>
    /// Given the values the redactor leaves in place, such as a masked token and a SHA-256 fingerprint,
    /// when they are scanned,
    /// then nothing is found.
    /// </summary>
    [Fact(DisplayName = "🖐️ The fixture scan ignores masks and hashes")]
    public void FindSecrets_RedactedText_FindsNothing()
    {
        // Arrange
        string text = """
            { "token": "***", "fingerprint": "sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
              "id": "2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87", "WWW-Authenticate": "Bearer authorization_uri=https://login.example" }
            """;

        // Act
        string[] hits = [.. FindSecrets(text)];

        // Assert
        hits.ShouldBeEmpty();
    }

    private static IEnumerable<string> FindSecrets(string text) => SecretShape().Matches(text).Select(m => m.Value);

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Setpoint.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repo root (Setpoint.slnx) above the test output directory.");
    }

    // GitHub tokens (classic, App, OAuth, fine-grained), PEM private keys, the shape of a classic Azure DevOps PAT
    // (52 base32 characters), and an Authorization header with a value.
    [GeneratedRegex("""gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|\b[a-z2-7]{52}\b|"Authorization"\s*:\s*"(?!\*\*\*)[^"]+""")]
    private static partial Regex SecretShape();
}
