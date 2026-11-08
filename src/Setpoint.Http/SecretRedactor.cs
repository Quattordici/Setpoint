using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Setpoint.Http;

/// <summary>
/// Removes secrets from anything Setpoint logs or records: the exact values it was given (tokens, keys)
/// and anything that looks like a GitHub token, a JWT or a basic-auth credential.
/// </summary>
public sealed partial class SecretRedactor
{
    /// <summary>What a secret is replaced with.</summary>
    public const string Mask = "***";

    /// <summary>Headers whose values are never written anywhere.</summary>
    public static readonly IReadOnlySet<string> SensitiveHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-TFS-FedAuthRedirect",
        "X-VSS-UserData",
    };

    private const int MinimumSecretLength = 4;

    private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);

    /// <summary>Registers a value to mask wherever it appears. Short values are ignored, so they can't mask ordinary text.</summary>
    public void Add(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= MinimumSecretLength)
        {
            _secrets.TryAdd(secret, 0);

            // Basic auth carries ":<token>" base64-encoded, so mask that form too.
            _secrets.TryAdd(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + secret)), 0);
        }
    }

    /// <summary>Returns <paramref name="text"/> with every registered secret and token-shaped value masked.</summary>
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Longest first, so a secret that contains another is masked whole.
        foreach (string secret in _secrets.Keys.OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return TokenPattern().Replace(text, Mask);
    }

    /// <summary>Returns a header value fit to write: masked when the header is sensitive, otherwise redacted text.</summary>
    public string RedactHeader(string name, string value) =>
        SensitiveHeaders.Contains(name) ? Mask : Redact(value);

    /// <summary>Returns <paramref name="uri"/> with secrets masked, including query values such as <c>access_token</c>.</summary>
    public string RedactUri(Uri uri) => Redact(SecretQueryPattern().Replace(uri.ToString(), m => $"{m.Groups["key"].Value}={Mask}"));

    // GitHub tokens (ghp_, gho_, ghu_, ghs_, ghr_, github_pat_) and JWTs (three base64url parts starting with eyJ).
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(?<key>(?:access_token|token|code|sig|client_secret|password))=[^&#]*", RegexOptions.IgnoreCase)]
    private static partial Regex SecretQueryPattern();
}
