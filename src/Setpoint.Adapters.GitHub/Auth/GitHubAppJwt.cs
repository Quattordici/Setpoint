using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Setpoint.Adapters.GitHub.Auth;

/// <summary>
/// Signs the short-lived JWT a GitHub App authenticates as, with RS256 from the BCL and no JWT package.
/// GitHub accepts at most ten minutes; Setpoint asks for nine and backdates the start a minute for clock drift.
/// </summary>
internal static class GitHubAppJwt
{
    /// <summary>How long a JWT is valid for.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(9);

    private static readonly TimeSpan s_drift = TimeSpan.FromSeconds(60);

    /// <summary>Creates a JWT for the App, signed with its private key.</summary>
    public static string Create(long appId, RSA key, DateTimeOffset now)
    {
        long issuedAt = (now - s_drift).ToUnixTimeSeconds();
        long expires = (now + Lifetime).ToUnixTimeSeconds();
        string header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8);
        string payload = Base64Url(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"iat":{{issuedAt}},"exp":{{expires}},"iss":"{{appId}}"}""")));
        string signingInput = $"{header}.{payload}";
        byte[] signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>Reads a PEM private key, PKCS#1 (<c>BEGIN RSA PRIVATE KEY</c>, as GitHub issues it) or PKCS#8.</summary>
    /// <exception cref="InvalidOperationException">The text is not an RSA private key.</exception>
    public static RSA ImportKey(string pem, string variable)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem.Replace("\\n", "\n", StringComparison.Ordinal));
            return rsa;
        }
        catch (ArgumentException)
        {
            rsa.Dispose();

            // The exception text can quote the input, so it is never passed on.
            throw new InvalidOperationException($"the environment variable {variable} doesn't hold a PEM RSA private key");
        }
        catch (CryptographicException)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"the environment variable {variable} doesn't hold a valid RSA private key");
        }
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
