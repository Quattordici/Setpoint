using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Api;

namespace Setpoint.Adapters.AzureDevOps.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsEndpoint"/>: telling Services from Server, resolving the identity from the
/// connection data, and mapping a released REST version to its Server release.
/// </summary>
public sealed class AzureDevOpsEndpointTests
{
    private static readonly Uri s_server = new("https://ado.corp.example/tfs/DefaultCollection");

    /// <summary>
    /// Given connection data whose deploymentType is a name or a flag,
    /// when the deployment is read,
    /// then hosted (1) is Services and onPremises (2) is Server, whatever the URL.
    /// </summary>
    [Theory(DisplayName = "👍 Reads the deployment type as a name or a flag")]
    [InlineData("\"hosted\"", true)]
    [InlineData("\"onPremises\"", false)]
    [InlineData("1", true)]
    [InlineData("2", false)]
    public void IsServices_DeploymentType_DecidesIt(string deploymentType, bool expected)
    {
        // Arrange
        var data = JsonNode.Parse($$"""{ "deploymentType": {{deploymentType}} }""")!.AsObject();

        // Act
        bool services = AzureDevOpsEndpoint.IsServices(data, s_server);

        // Assert
        services.ShouldBe(expected);
    }

    /// <summary>
    /// Given connection data without a deployment type,
    /// when the deployment is read,
    /// then the URL decides: dev.azure.com and *.visualstudio.com are Services.
    /// </summary>
    [Theory(DisplayName = "🖐️ Falls back to the URL when the deployment type is missing")]
    [InlineData("https://dev.azure.com/contoso", true)]
    [InlineData("https://contoso.visualstudio.com", true)]
    [InlineData("https://ado.corp.example/tfs/DefaultCollection", false)]
    public void IsServices_NoDeploymentType_UsesUrl(string url, bool expected)
    {
        // Act
        bool services = AzureDevOpsEndpoint.IsServices([], new Uri(url));

        // Assert
        services.ShouldBe(expected);
    }

    /// <summary>
    /// Given an authenticated user with a display name and an account,
    /// when the identity is resolved,
    /// then it shows both.
    /// </summary>
    [Fact(DisplayName = "👍 Names the identity with its display name and account")]
    public void Identity_NameAndAccount_ShowsBoth()
    {
        // Arrange
        var data = JsonNode.Parse("""
            { "authenticatedUser": { "id": "7d1c3f0e-1111-4c4c-9c9c-000000000001", "providerDisplayName": "Mattias",
              "properties": { "Account": { "$type": "System.String", "$value": "mattias@contoso.example" } } } }
            """)!.AsObject();

        // Act
        string? identity = AzureDevOpsEndpoint.Identity(data);

        // Assert
        identity.ShouldBe("Mattias (mattias@contoso.example)");
    }

    /// <summary>
    /// Given connection data with the anonymous identity, or no user at all,
    /// when the identity is resolved,
    /// then there is none.
    /// </summary>
    [Theory(DisplayName = "👎 Resolves no identity for an anonymous or missing user")]
    [InlineData("""{ "authenticatedUser": { "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "providerDisplayName": "Anonymous" } }""")]
    [InlineData("""{ "deploymentType": "hosted" }""")]
    public void Identity_AnonymousOrMissing_IsNull(string json)
    {
        // Act
        string? identity = AzureDevOpsEndpoint.Identity(JsonNode.Parse(json)!.AsObject());

        // Assert
        identity.ShouldBeNull();
    }

    /// <summary>
    /// Given resource locations with released, preview-suffixed and unreleased versions,
    /// when the released version is taken,
    /// then it is the highest released one, ignoring 0.0 and preview suffixes.
    /// </summary>
    [Fact(DisplayName = "🖐️ Takes the highest released version, ignoring unreleased resources")]
    public void ReleasedVersion_MixedLocations_TakesHighest()
    {
        // Arrange
        AdoResourceLocation[] locations =
        [
            new("policy", "configurations", "7.1", "7.1"),
            new("policy", "evaluations", "7.1", "0.0"),
            new("policy", "revisions", "7.1", "7.0-preview.1"),
            new("policy", "types", null, null),
        ];

        // Act
        Version? released = AzureDevOpsEndpoint.ReleasedVersion(locations);

        // Assert
        released.ShouldBe(new Version(7, 1));
    }

    /// <summary>
    /// Given an area whose locations give no released version,
    /// when the released version is taken,
    /// then there is none.
    /// </summary>
    [Fact(DisplayName = "👎 Finds no released version when no location gives one")]
    public void ReleasedVersion_NoneReleased_IsNull()
    {
        // Act
        Version? released = AzureDevOpsEndpoint.ReleasedVersion([new AdoResourceLocation("policy", "types", "7.0", "0.0")]);

        // Assert
        released.ShouldBeNull();
    }

    /// <summary>
    /// Given each released REST version in Microsoft's mapping,
    /// when it is named,
    /// then it is the Server release that shipped it.
    /// </summary>
    [Theory(DisplayName = "👍 Names the Server release a REST version belongs to")]
    [InlineData(7, 2, "Azure DevOps Server (REST 7.2)")]
    [InlineData(7, 1, "Azure DevOps Server 2022.1")]
    [InlineData(7, 0, "Azure DevOps Server 2022")]
    [InlineData(6, 0, "Azure DevOps Server 2020")]
    [InlineData(5, 1, "Azure DevOps Server 2019")]
    [InlineData(4, 1, "Team Foundation Server (REST 4.1)")]
    public void ServerName_Version_IsRelease(int major, int minor, string expected)
    {
        // Act
        string name = AzureDevOpsEndpoint.ServerName(new Version(major, minor));

        // Assert
        name.ShouldBe(expected);
    }

    /// <summary>
    /// Given a collection URL with or without a trailing slash,
    /// when it becomes the API root,
    /// then it ends in exactly one slash, so relative paths stay under the collection.
    /// </summary>
    [Theory(DisplayName = "🖐️ Ends the API root in a slash so paths stay under the collection")]
    [InlineData("https://ado.corp.example/tfs/DefaultCollection")]
    [InlineData("https://ado.corp.example/tfs/DefaultCollection/")]
    public void ApiRoot_Url_EndsInOneSlash(string url)
    {
        // Act
        Uri root = AzureDevOpsEndpoint.ApiRoot(new Uri(url));

        // Assert
        root.AbsoluteUri.ShouldBe("https://ado.corp.example/tfs/DefaultCollection/");
    }
}
