namespace Setpoint.Adapters.GitHub;

/// <summary>Where a GitHub target's REST API lives.</summary>
internal static class GitHubEndpoint
{
    /// <summary>The github.com API.</summary>
    public static readonly Uri GitHubCom = new("https://api.github.com/");

    /// <summary>
    /// The API root for a target's <c>url:</c>: github.com when there is none or it is github.com, otherwise the
    /// GitHub Enterprise Server convention of <c>&lt;url&gt;/api/v3/</c>, unless the URL already points into <c>/api/</c>.
    /// </summary>
    public static Uri ApiRoot(Uri? url)
    {
        if (url is null || url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || url.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return GitHubCom;
        }

        string root = url.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        return root.Contains("/api/", StringComparison.OrdinalIgnoreCase) ? new Uri(root) : new Uri(root + "api/v3/");
    }

    /// <summary>What the preflight reports the target as: github.com, or a GitHub Enterprise Server and its version.</summary>
    public static string Describe(Uri apiRoot, string? enterpriseVersion) =>
        enterpriseVersion is not null ? $"GitHub Enterprise Server {enterpriseVersion}"
        : apiRoot == GitHubCom ? "github.com"
        : $"GitHub ({apiRoot.Host})";
}
