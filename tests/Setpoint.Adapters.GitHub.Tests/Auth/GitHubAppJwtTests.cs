using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Setpoint.Adapters.GitHub.Auth;

namespace Setpoint.Adapters.GitHub.Tests.Auth;

/// <summary>Tests for <see cref="GitHubAppJwt"/>: the RS256 JWT a GitHub App signs, and reading its private key.</summary>
public sealed class GitHubAppJwtTests
{
    /// <summary>
    /// Given an App id and its private key,
    /// when a JWT is created,
    /// then it is RS256, verifies with the public key, and claims the App with a start a minute back and a nine-minute life.
    /// </summary>
    [Fact(DisplayName = "👍 Signs an RS256 JWT that verifies with the App's public key")]
    public void Create_AppAndKey_SignsVerifiableJwt()
    {
        // Arrange
        using var key = RSA.Create(2048);
        long appId = new Bogus.Randomizer().Long(1, 9_999_999);
        var now = new DateTimeOffset(2026, 11, 8, 20, 0, 0, TimeSpan.Zero);

        // Act
        string jwt = GitHubAppJwt.Create(appId, key, now);

        // Assert
        string[] parts = jwt.Split('.');
        parts.Length.ShouldBe(3);
        JsonNode header = JsonNode.Parse(Decode(parts[0]))!;
        JsonNode claims = JsonNode.Parse(Decode(parts[1]))!;
        header["alg"]!.GetValue<string>().ShouldBe("RS256");
        claims["iss"]!.GetValue<string>().ShouldBe(appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        claims["iat"]!.GetValue<long>().ShouldBe(now.AddSeconds(-60).ToUnixTimeSeconds());
        claims["exp"]!.GetValue<long>().ShouldBe(now.AddMinutes(9).ToUnixTimeSeconds());
        key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), DecodeBytes(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).ShouldBeTrue();
    }

    /// <summary>
    /// Given a private key in PKCS#1 (as GitHub issues it), PKCS#8, or with escaped newlines from a pipeline secret,
    /// when it is imported,
    /// then each reads as the same key.
    /// </summary>
    [Theory(DisplayName = "👍 Reads PKCS#1, PKCS#8 and escaped-newline PEM keys")]
    [InlineData("pkcs1")]
    [InlineData("pkcs8")]
    [InlineData("escaped")]
    public void ImportKey_PemForms_ReadsKey(string form)
    {
        // Arrange
        using var original = RSA.Create(2048);
        string pem = form switch
        {
            "pkcs8" => original.ExportPkcs8PrivateKeyPem(),
            "escaped" => original.ExportRSAPrivateKeyPem().Replace("\n", "\\n", StringComparison.Ordinal),
            _ => original.ExportRSAPrivateKeyPem(),
        };

        // Act
        using RSA imported = GitHubAppJwt.ImportKey(pem, "SETPOINT_GITHUB_APP_KEY");

        // Assert
        imported.ExportParameters(false).Modulus.ShouldBe(original.ExportParameters(false).Modulus);
    }

    /// <summary>
    /// Given a variable that holds something other than a key,
    /// when it is imported,
    /// then the error names the variable and never repeats its content.
    /// </summary>
    [Fact(DisplayName = "👎 Rejects a value that isn't a key without repeating it")]
    public void ImportKey_NotAKey_NamesVariableOnly()
    {
        // Arrange
        string notAKey = "ghp_" + new Bogus.Randomizer().AlphaNumeric(36);

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => GitHubAppJwt.ImportKey(notAKey, "SETPOINT_GITHUB_APP_KEY"));

        // Assert
        exception.Message.ShouldContain("SETPOINT_GITHUB_APP_KEY");
        exception.Message.ShouldNotContain(notAKey);
    }

    private static string Decode(string part) => Encoding.UTF8.GetString(DecodeBytes(part));

    private static byte[] DecodeBytes(string part)
    {
        string base64 = part.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }
}
