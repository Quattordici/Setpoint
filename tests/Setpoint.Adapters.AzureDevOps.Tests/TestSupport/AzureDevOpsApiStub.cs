using System.Text;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Setpoint.Adapters.AzureDevOps.Tests.TestSupport;

/// <summary>
/// Stubs one Azure DevOps Server collection on a WireMock server, under <c>/tfs/DefaultCollection</c>. Every REST stub
/// requires the PAT as Basic auth and the api-version Setpoint pins, so a call that forgets either finds no stub.
/// The discovery calls (connectionData and OPTIONS) carry no version, as the adapter sends them.
/// </summary>
internal sealed class AzureDevOpsApiStub(WireMockServer server, string token)
{
    public const string Collection = "/tfs/DefaultCollection";

    private readonly string _basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token));

    /// <summary>The api-version the REST stubs require. An allowed older server is stubbed with its own.</summary>
    public string ApiVersion { get; set; } = "7.0";

    public Uri CollectionUrl => new(server.Urls[0] + Collection);

    /// <summary>The connection data a signed-in user gets on Server (onPremises) or Services (hosted).</summary>
    public AzureDevOpsApiStub ConnectionData(string displayName, string account, string deploymentType = "onPremises", string? id = null)
    {
        var body = new JsonObject
        {
            ["authenticatedUser"] = new JsonObject
            {
                ["id"] = id ?? Guid.NewGuid().ToString(),
                ["providerDisplayName"] = displayName,
                ["isActive"] = true,
                ["properties"] = new JsonObject { ["Account"] = new JsonObject { ["$type"] = "System.String", ["$value"] = account } },
            },
            ["deploymentType"] = deploymentType,
            ["instanceId"] = Guid.NewGuid().ToString(),
        };
        server.Given(Signed("/_apis/connectionData").UsingGet()).RespondWith(Json(body));
        return this;
    }

    /// <summary>The answer Azure DevOps gives a token it doesn't accept: 203 and its HTML sign-in page.</summary>
    public AzureDevOpsApiStub SignInPage()
    {
        server.Given(Request.Create().WithPath(Collection + "/_apis/connectionData").UsingGet()).RespondWith(Response.Create()
            .WithStatusCode(203)
            .WithHeader("Content-Type", "text/html; charset=utf-8")
            .WithBody("<html><body>Sign in to your account</body></html>"));
        return this;
    }

    /// <summary>The policy area's resource locations, each released at <paramref name="releasedVersion"/>.</summary>
    public AzureDevOpsApiStub PolicyArea(string releasedVersion)
    {
        var locations = new JsonArray(
            (JsonNode)Location("configurations", releasedVersion),
            (JsonNode)Location("types", releasedVersion),
            (JsonNode)Location("evaluations", "0.0"));
        server.Given(Signed("/_apis/policy").UsingOptions()).RespondWith(Json(List(locations)));
        return this;
    }

    public AzureDevOpsApiStub Projects(params (string Id, string Name)[] projects)
    {
        server.Given(Versioned("/_apis/projects"))
            .RespondWith(Json(List(new JsonArray([.. projects.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["state"] = "wellFormed" })]))));
        return this;
    }

    /// <summary>The policy types, all of Setpoint's unless some are left out.</summary>
    public AzureDevOpsApiStub PolicyTypes(string projectId, params string[] leaveOut)
    {
        string[] ids =
        [
            "fa4e907d-c16b-4a4c-9dfa-4906e5d171dd",
            "0609b952-1397-4640-95ec-e00a01b2c241",
            "cbdc66da-9728-4af8-aada-9a5a32e4a226",
            "c6a1889d-b943-4856-b76f-9e46bb6b0df2",
            "fa4e907d-c16b-4a4c-9dfa-4916e5d171ab",
            "40e92b44-2fe1-4dd6-b3d8-74a9c21d0c6e",
        ];
        server.Given(Versioned($"/{projectId}/_apis/policy/types"))
            .RespondWith(Json(List(new JsonArray([.. ids.Except(leaveOut).Select(id => (JsonNode)new JsonObject { ["id"] = id, ["displayName"] = id })]))));
        return this;
    }

    public AzureDevOpsApiStub Repositories(params JsonObject[] repos)
    {
        server.Given(Versioned("/_apis/git/repositories")).RespondWith(Json(List(new JsonArray([.. repos.Select(r => (JsonNode)r.DeepClone())]))));
        return this;
    }

    /// <summary>A server that is fully reachable: signed in, released at <paramref name="releasedVersion"/>, one project with every policy type.</summary>
    public AzureDevOpsApiStub Server(string releasedVersion = "7.0")
    {
        string projectId = Guid.NewGuid().ToString();
        return ConnectionData("Setpoint Service", @"CORP\svc-setpoint").PolicyArea(releasedVersion).Projects((projectId, "Payments")).PolicyTypes(projectId);
    }

    private IRequestBuilder Signed(string path) => Request.Create().WithPath(Collection + path).WithHeader("Authorization", _basic);

    private IRequestBuilder Versioned(string path) => Signed(path).UsingGet().WithParam("api-version", ApiVersion);

    private static JsonObject Location(string resource, string released) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["area"] = "policy",
        ["resourceName"] = resource,
        ["routeTemplate"] = "{project}/_apis/{area}/{resource}/{id}",
        ["resourceVersion"] = 1,
        ["minVersion"] = "2.0",
        ["maxVersion"] = released == "0.0" ? "7.0" : released,
        ["releasedVersion"] = released,
    };

    private static JsonObject List(JsonArray value) => new() { ["count"] = value.Count, ["value"] = value };

    private static IResponseBuilder Json(JsonNode body) => Response.Create()
        .WithStatusCode(200)
        .WithHeader("Content-Type", "application/json; charset=utf-8; api-version=7.0")
        .WithBody(body.ToJsonString());
}
