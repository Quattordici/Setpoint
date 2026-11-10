using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Setpoint.Adapters.GitHub.Tests.TestSupport;

/// <summary>
/// Stubs the GitHub REST API on a WireMock server, under <c>/api/v3</c> as GitHub Enterprise Server serves it, so a
/// target whose <c>url:</c> is the server reaches it. Every stub requires the token and the pinned API version.
/// </summary>
internal sealed class GitHubApiStub(WireMockServer server, string token)
{
    public const string Root = "/api/v3";

    public Uri Url => new(server.Urls[0]);

    public GitHubApiStub User(string login, string? enterpriseVersion = null)
    {
        IResponseBuilder response = Json(new JsonObject { ["login"] = login, ["id"] = 1 });
        if (enterpriseVersion is not null)
        {
            response = response.WithHeader("X-GitHub-Enterprise-Version", enterpriseVersion);
        }

        server.Given(Get("/user")).RespondWith(response);
        return this;
    }

    public GitHubApiStub Organization(string org, params JsonObject[] repos)
    {
        server.Given(Get($"/orgs/{org}")).RespondWith(Json(new JsonObject { ["login"] = org }));
        server.Given(Get($"/orgs/{org}/repos")).RespondWith(Json(new JsonArray([.. repos.Select(r => (JsonNode)r.DeepClone())])));
        return this;
    }

    /// <summary>An organization whose repos come in two pages, linked by a Link header.</summary>
    public GitHubApiStub OrganizationInPages(string org, JsonObject[] first, JsonObject[] second)
    {
        server.Given(Get($"/orgs/{org}")).RespondWith(Json(new JsonObject { ["login"] = org }));
        string next = $"{Url.ToString().TrimEnd('/')}{Root}/orgs/{org}/repos?type=all&per_page=100&page=2";
        server.Given(Get($"/orgs/{org}/repos").WithParam("page", "2"))
            .AtPriority(1)
            .RespondWith(Json(new JsonArray([.. second.Select(r => (JsonNode)r.DeepClone())])));
        server.Given(Get($"/orgs/{org}/repos"))
            .AtPriority(2)
            .RespondWith(Json(new JsonArray([.. first.Select(r => (JsonNode)r.DeepClone())])).WithHeader("Link", $"<{next}>; rel=\"next\""));
        return this;
    }

    public GitHubApiStub UserAccount(string user, params JsonObject[] repos)
    {
        server.Given(Get($"/orgs/{user}")).RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"message":"Not Found"}"""));
        server.Given(Get($"/users/{user}/repos")).RespondWith(Json(new JsonArray([.. repos.Select(r => (JsonNode)r.DeepClone())])));
        return this;
    }

    public GitHubApiStub SignedInUserRepos(params JsonObject[] repos)
    {
        server.Given(Get("/user/repos")).RespondWith(Json(new JsonArray([.. repos.Select(r => (JsonNode)r.DeepClone())])));
        return this;
    }

    public GitHubApiStub Branches(string owner, string repo, params string[] names)
    {
        server.Given(Get($"/repos/{owner}/{repo}/branches"))
            .RespondWith(Json(new JsonArray([.. names.Select(n => (JsonNode)new JsonObject { ["name"] = n })])));
        return this;
    }

    public GitHubApiStub Repo(JsonObject repo)
    {
        server.Given(Get($"/repos/{repo["full_name"]}")).RespondWith(Json(repo));
        return this;
    }

    public GitHubApiStub Rulesets(string owner, string repo, params JsonObject[] summaries)
    {
        server.Given(Get($"/repos/{owner}/{repo}/rulesets")).RespondWith(Json(new JsonArray([.. summaries.Select(s => (JsonNode)s.DeepClone())])));
        return this;
    }

    /// <summary>The answer GitHub gives for rulesets on a private repo on GitHub Free.</summary>
    public GitHubApiStub RulesetsNeedUpgrade(string owner, string repo)
    {
        server.Given(Get($"/repos/{owner}/{repo}/rulesets")).RespondWith(Response.Create().WithStatusCode(403).WithBody(
            """{"message":"Upgrade to GitHub Pro or make this repository public to enable this feature.","status":"403"}"""));
        return this;
    }

    public GitHubApiStub Ruleset(string owner, string repo, JsonObject ruleset)
    {
        server.Given(Get($"/repos/{owner}/{repo}/rulesets/{ruleset["id"]}")).RespondWith(Json(ruleset));
        return this;
    }

    public GitHubApiStub BranchRules(string owner, string repo, string branch, params JsonObject[] rules)
    {
        server.Given(Get($"/repos/{owner}/{repo}/rules/branches/{branch}")).RespondWith(Json(new JsonArray([.. rules.Select(r => (JsonNode)r.DeepClone())])));
        return this;
    }

    /// <summary>The teams with access to a repo, each with its permission.</summary>
    public GitHubApiStub Teams(string owner, string repo, params (string Slug, string Permission)[] teams)
    {
        server.Given(Get($"/repos/{owner}/{repo}/teams"))
            .RespondWith(Json(new JsonArray([.. teams.Select(t => (JsonNode)new JsonObject { ["slug"] = t.Slug, ["name"] = t.Slug, ["permission"] = t.Permission })])));
        return this;
    }

    /// <summary>What a token that may not list a repo's teams gets: 403.</summary>
    public GitHubApiStub TeamsForbidden(string owner, string repo)
    {
        server.Given(Get($"/repos/{owner}/{repo}/teams"))
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("""{"message":"Resource not accessible by personal access token"}"""));
        return this;
    }

    /// <summary>Every team in an organization.</summary>
    public GitHubApiStub OrgTeams(string org, params string[] slugs)
    {
        server.Given(Get($"/orgs/{org}/teams")).RespondWith(Json(new JsonArray([.. slugs.Select(s => (JsonNode)new JsonObject { ["slug"] = s, ["name"] = s })])));
        return this;
    }

    /// <summary>Answers adding a repo to a team (PUT) and removing it (DELETE).</summary>
    public GitHubApiStub TeamRepoWrites(string org, int status = 204, string? body = null)
    {
        foreach (string method in new[] { "PUT", "DELETE" })
        {
            IResponseBuilder response = Response.Create().WithStatusCode(status);
            server.Given(Request.Create()
                    .WithPath(new WireMock.Matchers.WildcardMatcher($"{Root}/orgs/{org}/teams/*/repos/*"))
                    .UsingMethod(method)
                    .WithHeader("Authorization", $"Bearer {token}"))
                .RespondWith(body is null ? response : response.WithHeader("Content-Type", "application/json; charset=utf-8").WithBody(body));
        }

        return this;
    }

    private IRequestBuilder Get(string path) => Request.Create()
        .WithPath(Root + path)
        .UsingGet()
        .WithHeader("Authorization", $"Bearer {token}")
        .WithHeader("X-GitHub-Api-Version", "2022-11-28");

    private static IResponseBuilder Json(JsonNode body) => Response.Create()
        .WithStatusCode(200)
        .WithHeader("Content-Type", "application/json; charset=utf-8")
        .WithBody(body.ToJsonString());
}
