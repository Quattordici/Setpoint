using Bogus;

namespace Setpoint.Http.Tests.TestSupport;

/// <summary>Realistic-looking secrets for redaction tests.</summary>
internal static class TestTokens
{
    private static readonly Faker s_faker = new();

    /// <summary>An Azure DevOps style PAT: 52 lowercase alphanumerics.</summary>
    public static string AzureDevOpsPat() => s_faker.Random.String2(52, "abcdefghijklmnopqrstuvwxyz0123456789");

    /// <summary>A GitHub classic PAT: ghp_ plus 36 alphanumerics.</summary>
    public static string GitHubPat() => "ghp_" + s_faker.Random.AlphaNumeric(36);
}
