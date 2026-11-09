using System.Text.Json.Nodes;

namespace Setpoint.Adapters.AzureDevOps.Tests.TestData;

/// <summary>Builds a Git repo as the Azure DevOps API lists it. By default an active repo with content on main.</summary>
internal sealed class AdoRepoBuilder
{
    private static readonly Bogus.Faker s_faker = new();

    private string _project = s_faker.Commerce.Department().Replace(' ', '-');
    private string _name = s_faker.Hacker.Noun().ToLowerInvariant().Replace(' ', '-') + "-" + s_faker.Random.Number(100, 999);
    private string? _defaultBranch = "refs/heads/main";
    private bool _disabled;
    private bool _fork;
    private bool _maintenance;

    public AdoRepoBuilder InProject(string project)
    {
        _project = project;
        return this;
    }

    public AdoRepoBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public AdoRepoBuilder Disabled()
    {
        _disabled = true;
        return this;
    }

    public AdoRepoBuilder InMaintenance()
    {
        _maintenance = true;
        return this;
    }

    public AdoRepoBuilder Fork()
    {
        _fork = true;
        return this;
    }

    /// <summary>An empty repo: Azure DevOps leaves out its default branch.</summary>
    public AdoRepoBuilder Empty()
    {
        _defaultBranch = null;
        return this;
    }

    public JsonObject Build()
    {
        var repo = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = _name,
            ["url"] = $"https://ado.example/tfs/DefaultCollection/_apis/git/repositories/{_name}",
            ["project"] = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = _project, ["state"] = "wellFormed" },
            ["size"] = _defaultBranch is null ? 0 : s_faker.Random.Long(1_000, 9_000_000),
            ["remoteUrl"] = $"https://ado.example/tfs/DefaultCollection/{_project}/_git/{_name}",
            ["isDisabled"] = _disabled,
            ["isInMaintenance"] = _maintenance,
        };
        if (_defaultBranch is not null)
        {
            repo["defaultBranch"] = _defaultBranch;
        }

        if (_fork)
        {
            repo["isFork"] = true;
        }

        return repo;
    }
}
