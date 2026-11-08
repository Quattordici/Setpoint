using System.Text.Json.Nodes;

namespace Setpoint.Adapters.AzureDevOps;

/// <summary>What kind of Azure DevOps a target is, and which release of Server.</summary>
internal static class AzureDevOpsEndpoint
{
    /// <summary>The identity Azure DevOps reports when no credential was accepted.</summary>
    public const string AnonymousId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    /// <summary>The oldest REST version Setpoint supports: Azure DevOps Server 2022's.</summary>
    public static readonly Version Minimum = new(7, 0);

    /// <summary>The organization or collection URL as an API root, ending in a slash so relative paths stay under it.</summary>
    public static Uri ApiRoot(Uri url) => url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");

    /// <summary>
    /// True for Azure DevOps Services. <c>connectionData</c> says so in <c>deploymentType</c>, as a name
    /// (<c>hosted</c>, <c>onPremises</c>) or a flag (1, 2); the URL decides when it says neither.
    /// </summary>
    public static bool IsServices(JsonObject connectionData, Uri url)
    {
        JsonNode? deployment = connectionData["deploymentType"];
        if (deployment is JsonValue value)
        {
            if (value.TryGetValue(out string? name))
            {
                if (name.Equals("hosted", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (name.Equals("onPremises", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else if (value.TryGetValue(out int flags) && flags is 1 or 2)
            {
                return flags == 1;
            }
        }

        return url.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Who the token signs in as, or null when Azure DevOps signed the request in anonymously.</summary>
    public static string? Identity(JsonObject connectionData)
    {
        if (connectionData["authenticatedUser"] is not JsonObject user)
        {
            return null;
        }

        string? id = Text(user["id"]);
        if (id is null || id.Equals(AnonymousId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? account = Text(user["properties"]?["Account"]?["$value"]);
        string? name = Text(user["customDisplayName"]) ?? Text(user["providerDisplayName"]);
        return (name, account) switch
        {
            (null, null) => id,
            (null, _) => account,
            (_, null) => name,
            _ when name.Equals(account, StringComparison.OrdinalIgnoreCase) => name,
            _ => $"{name} ({account})",
        };
    }

    /// <summary>
    /// The highest released REST version among an area's resource locations, which is what the server is: Server
    /// 2022 releases 7.0 and 2022.1 releases 7.1. Null when no location gives a version.
    /// </summary>
    public static Version? ReleasedVersion(IEnumerable<Api.AdoResourceLocation> locations) =>
        locations
            .Select(l => ParseVersion(l.ReleasedVersion))
            .OfType<Version>()
            .Where(v => v > new Version(0, 0))
            .DefaultIfEmpty()
            .Max();

    /// <summary>The Server release a released REST version belongs to, from Microsoft's version mapping.</summary>
    public static string ServerName(Version released) => released switch
    {
        { Major: > 7 } or { Major: 7, Minor: >= 2 } => $"Azure DevOps Server (REST {Format(released)})",
        { Major: 7, Minor: 1 } => "Azure DevOps Server 2022.1",
        { Major: 7 } => "Azure DevOps Server 2022",
        { Major: 6 } => "Azure DevOps Server 2020",
        { Major: 5 } => "Azure DevOps Server 2019",
        _ => $"Team Foundation Server (REST {Format(released)})",
    };

    /// <summary>A version as Azure DevOps writes it in <c>api-version</c>: major.minor.</summary>
    public static string Format(Version version) => version.ToString(2);

    private static Version? ParseVersion(string? text)
    {
        if (text is null)
        {
            return null;
        }

        // A preview version reads "7.1-preview.1"; only the number in front counts.
        int dash = text.IndexOf('-', StringComparison.Ordinal);
        string number = dash < 0 ? text : text[..dash];
        return Version.TryParse(number, out Version? version) ? new Version(version.Major, version.Minor) : null;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : null;
}
