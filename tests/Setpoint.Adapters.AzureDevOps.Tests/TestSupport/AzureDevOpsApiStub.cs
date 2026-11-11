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

    /// <summary>The header every signed stub requires: Basic auth with the PAT by default.</summary>
    public (string Name, string Value)? RequiredHeader { get; set; }

    /// <summary>A stub whose calls must carry <paramref name="token"/> as a bearer token, as the job token and Entra ID do.</summary>
    public static AzureDevOpsApiStub Bearer(WireMockServer server, string token) => new(server, token) { RequiredHeader = ("Authorization", $"Bearer {token}") };

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

    /// <summary>A project's policy configurations, in two pages linked by x-ms-continuationtoken.</summary>
    public AzureDevOpsApiStub PolicyConfigurations(string projectId, JsonObject[] first, JsonObject[] second)
    {
        server.Given(Versioned($"/{projectId}/_apis/policy/configurations").WithParam("continuationToken", "page-2"))
            .AtPriority(1)
            .RespondWith(Json(List(new JsonArray([.. second.Select(c => (JsonNode)c.DeepClone())]))));
        server.Given(Versioned($"/{projectId}/_apis/policy/configurations"))
            .AtPriority(2)
            .RespondWith(Json(List(new JsonArray([.. first.Select(c => (JsonNode)c.DeepClone())]))).WithHeader("x-ms-continuationtoken", "page-2"));
        return this;
    }

    public AzureDevOpsApiStub BuildDefinitions(string projectId, params (int Id, string Name)[] pipelines)
    {
        server.Given(Versioned($"/{projectId}/_apis/build/definitions"))
            .RespondWith(Json(List(new JsonArray([.. pipelines.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["path"] = "\\" })]))));
        return this;
    }

    /// <summary>The Project Valid Users group of a project, found by a general identity search.</summary>
    public AzureDevOpsApiStub ValidUsers(string projectName, string descriptor)
    {
        server.Given(Versioned("/_apis/identities").WithParam("searchFilter", "General").WithParam("filterValue", $"[{projectName}]\\Project Valid Users"))
            .RespondWith(Json(List(new JsonArray((JsonNode)Identity(descriptor, $"[{projectName}]\\Project Valid Users")))));
        return this;
    }

    /// <summary>The identities a general search for <paramref name="filterValue"/> returns.</summary>
    public AzureDevOpsApiStub IdentitySearch(string filterValue, params (string Descriptor, string Name)[] identities)
    {
        server.Given(Versioned("/_apis/identities").WithParam("searchFilter", "General").WithParam("filterValue", filterValue))
            .RespondWith(Json(List(new JsonArray([.. identities.Select(i => (JsonNode)Identity(i.Descriptor, i.Name))]))));
        return this;
    }

    /// <summary>The identities a descriptor lookup returns.</summary>
    public AzureDevOpsApiStub Identities(params (string Descriptor, string Name)[] identities)
    {
        server.Given(Versioned("/_apis/identities").WithParam("descriptors"))
            .RespondWith(Json(List(new JsonArray([.. identities.Select(i => (JsonNode)Identity(i.Descriptor, i.Name))]))));
        return this;
    }

    /// <summary>The ACL on a token in the Git Repositories namespace.</summary>
    public AzureDevOpsApiStub Acl(string token, params (string Descriptor, int Allow, int Deny)[] entries)
    {
        var aces = new JsonObject();
        foreach ((string descriptor, int allow, int deny) in entries)
        {
            aces[descriptor] = new JsonObject { ["descriptor"] = descriptor, ["allow"] = allow, ["deny"] = deny };
        }

        var acl = new JsonObject { ["inheritPermissions"] = true, ["token"] = token, ["acesDictionary"] = aces };
        server.Given(Versioned("/_apis/accesscontrollists/2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87").WithParam("token", token))
            .RespondWith(Json(List(new JsonArray((JsonNode)acl))));
        return this;
    }

    /// <summary>What a read-only PAT gets for security reads: 401.</summary>
    public AzureDevOpsApiStub AclUnauthorized()
    {
        server.Given(Request.Create().WithPath(Collection + "/_apis/accesscontrollists/2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("""{"message":"TF400813: The user is not authorized to access this resource."}"""));
        return this;
    }

    /// <summary>Answers writes to a project's policy configurations: create (POST), and update or delete by id.</summary>
    public AzureDevOpsApiStub PolicyWrites(string projectId, int status = 200, string? body = null)
    {
        foreach (string method in new[] { "POST", "PUT", "DELETE" })
        {
            string path = method == "POST" ? $"/{projectId}/_apis/policy/configurations" : $"/{projectId}/_apis/policy/configurations/*";
            server.Given(Signed(path).UsingMethod(method).WithParam("api-version", ApiVersion))
                .RespondWith(Write(status, body ?? (method == "DELETE" ? null : """{"id":99,"revision":1}""")));
        }

        return this;
    }

    /// <summary>Answers Access Control Entries - Set in the Git Repositories namespace.</summary>
    public AzureDevOpsApiStub AclWrites(int status = 200, string? body = null)
    {
        server.Given(Signed("/_apis/accesscontrolentries/2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87").UsingPost().WithParam("api-version", ApiVersion))
            .RespondWith(Write(status, body ?? """{"count":1,"value":[]}"""));
        return this;
    }

    /// <summary>The identities a general search for <paramref name="filterValue"/> returns, each with a known identity id.</summary>
    public AzureDevOpsApiStub IdentitySearchWithIds(string filterValue, params (string Descriptor, string Name, string Id)[] identities)
    {
        server.Given(Versioned("/_apis/identities").WithParam("searchFilter", "General").WithParam("filterValue", filterValue))
            .RespondWith(Json(List(new JsonArray([.. identities.Select(i =>
            {
                JsonObject identity = Identity(i.Descriptor, i.Name);
                identity["id"] = i.Id;
                return (JsonNode)identity;
            })]))));
        return this;
    }

    /// <summary>A project's deployment environments, listed at the pinned preview version.</summary>
    public AzureDevOpsApiStub Environments(string projectId, params (long Id, string Name)[] environments)
    {
        server.Given(Preview($"/{projectId}/_apis/distributedtask/environments").UsingGet())
            .RespondWith(Json(List(new JsonArray([.. environments.Select(e => (JsonNode)new JsonObject { ["id"] = e.Id, ["name"] = e.Name, ["description"] = string.Empty })]))));
        return this;
    }

    /// <summary>The checks on one environment.</summary>
    public AzureDevOpsApiStub EnvironmentChecks(string projectId, long environmentId, params JsonObject[] checks)
    {
        server.Given(Preview($"/{projectId}/_apis/pipelines/checks/configurations").UsingGet()
                .WithParam("resourceType", "environment").WithParam("resourceId", environmentId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .RespondWith(Json(List(new JsonArray([.. checks.Select(c => (JsonNode)c.DeepClone())]))));
        return this;
    }

    /// <summary>Answers creating an environment (POST) and adding, replacing or removing a check.</summary>
    public AzureDevOpsApiStub EnvironmentWrites(string projectId, int status = 200, string? body = null, long createdId = 99)
    {
        server.Given(Preview($"/{projectId}/_apis/distributedtask/environments").UsingPost())
            .RespondWith(Write(status, body ?? $$"""{"id":{{createdId}},"name":"created"}"""));
        server.Given(Preview($"/{projectId}/_apis/pipelines/checks/configurations").UsingPost())
            .RespondWith(Write(status, body ?? """{"id":501,"version":1}"""));
        foreach (string method in new[] { "PATCH", "DELETE" })
        {
            server.Given(Preview($"/{projectId}/_apis/pipelines/checks/configurations/*").UsingMethod(method))
                .RespondWith(Write(status, body ?? (method == "DELETE" ? null : """{"id":501,"version":2}""")));
        }

        return this;
    }

    /// <summary>An Approval check with these approver identity ids, all of whom must approve.</summary>
    public static JsonObject ApprovalCheck(int id, long environmentId, params (string Id, string Name)[] approvers) => new()
    {
        ["id"] = id,
        ["version"] = 3,
        ["type"] = new JsonObject { ["id"] = "8C6F20A7-A545-4486-9777-F762FAFE0D4D", ["name"] = "Approval" },
        ["settings"] = new JsonObject
        {
            ["approvers"] = new JsonArray([.. approvers.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["displayName"] = a.Name })]),
            ["executionOrder"] = "anyOrder",
            ["minRequiredApprovers"] = 0,
            ["instructions"] = "Check the release notes.",
            ["blockedApprovers"] = new JsonArray(),
            ["requesterCannotBeApprover"] = false,
        },
        ["timeout"] = 1440,
        ["resource"] = new JsonObject { ["type"] = "environment", ["id"] = environmentId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        ["createdBy"] = new JsonObject { ["displayName"] = "Mattias" },
        ["modifiedOn"] = "2026-11-10T03:04:05Z",
    };

    /// <summary>A server that is fully reachable: signed in, released at <paramref name="releasedVersion"/>, one project with every policy type.</summary>
    public AzureDevOpsApiStub Server(string releasedVersion = "7.0")
    {
        string projectId = Guid.NewGuid().ToString();
        return ConnectionData("Setpoint Service", @"CORP\svc-setpoint").PolicyArea(releasedVersion).Projects((projectId, "Payments")).PolicyTypes(projectId);
    }

    private static JsonObject Identity(string descriptor, string name) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["descriptor"] = descriptor,
        ["providerDisplayName"] = name,
        ["isActive"] = true,
    };

    private IRequestBuilder Signed(string path) => RequiredHeader is { } header
        ? Request.Create().WithPath(Collection + path).WithHeader(header.Name, header.Value)
        : Request.Create().WithPath(Collection + path).WithHeader("Authorization", _basic);

    private IRequestBuilder Versioned(string path) => Signed(path).UsingGet().WithParam("api-version", ApiVersion);

    private IRequestBuilder Preview(string path) => Signed(path).WithParam("api-version", "7.0-preview.1");

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

    private static IResponseBuilder Write(int status, string? body)
    {
        IResponseBuilder response = Response.Create().WithStatusCode(status);
        return body is null ? response : response.WithHeader("Content-Type", "application/json; charset=utf-8; api-version=7.0").WithBody(body);
    }

    private static JsonObject List(JsonArray value) => new() { ["count"] = value.Count, ["value"] = value };

    private static IResponseBuilder Json(JsonNode body) => Response.Create()
        .WithStatusCode(200)
        .WithHeader("Content-Type", "application/json; charset=utf-8; api-version=7.0")
        .WithBody(body.ToJsonString());
}
