using System.Text.Json.Nodes;

namespace Setpoint.Adapters.GitHub.Tests.TestData;

/// <summary>Builds a repo as the GitHub API returns it. By default a public, active repo with content on main.</summary>
internal sealed class GitHubRepoBuilder
{
    private static readonly Bogus.Faker s_faker = new();

    private string _owner = s_faker.Internet.UserName().ToLowerInvariant().Replace('.', '-');
    private string _name = s_faker.Hacker.Noun().ToLowerInvariant().Replace(' ', '-') + "-" + s_faker.Random.Number(100, 999);
    private string _ownerType = "Organization";
    private bool _private;
    private bool _archived;
    private bool _fork;
    private long _size = s_faker.Random.Long(1, 50_000);
    private readonly string? _defaultBranch = "main";
    private (bool Merge, bool Squash, bool Rebase)? _mergeFlags = (true, true, true);

    public GitHubRepoBuilder WithOwner(string owner)
    {
        _owner = owner;
        return this;
    }

    /// <summary>A repo owned by a user account, which has no teams.</summary>
    public GitHubRepoBuilder OwnedByUser(string user)
    {
        _owner = user;
        _ownerType = "User";
        return this;
    }

    public GitHubRepoBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public GitHubRepoBuilder Private()
    {
        _private = true;
        return this;
    }

    public GitHubRepoBuilder Archived()
    {
        _archived = true;
        return this;
    }

    public GitHubRepoBuilder Fork()
    {
        _fork = true;
        return this;
    }

    public GitHubRepoBuilder Empty()
    {
        _size = 0;
        return this;
    }

    public GitHubRepoBuilder WithSize(long size)
    {
        _size = size;
        return this;
    }

    public GitHubRepoBuilder WithMergeFlags(bool merge, bool squash, bool rebase)
    {
        _mergeFlags = (merge, squash, rebase);
        return this;
    }

    /// <summary>The flags GitHub leaves out for a token that can't read contents.</summary>
    public GitHubRepoBuilder WithoutMergeFlags()
    {
        _mergeFlags = null;
        return this;
    }

    public string Owner => _owner;

    public string Name => _name;

    public JsonObject Build()
    {
        var repo = new JsonObject
        {
            ["id"] = s_faker.Random.Long(1_000, 9_999_999),
            ["name"] = _name,
            ["full_name"] = $"{_owner}/{_name}",
            ["owner"] = new JsonObject { ["login"] = _owner, ["type"] = _ownerType },
            ["private"] = _private,
            ["visibility"] = _private ? "private" : "public",
            ["archived"] = _archived,
            ["disabled"] = false,
            ["fork"] = _fork,
            ["size"] = _size,
            ["default_branch"] = _defaultBranch,
        };
        if (_mergeFlags is { } flags)
        {
            repo["allow_merge_commit"] = flags.Merge;
            repo["allow_squash_merge"] = flags.Squash;
            repo["allow_rebase_merge"] = flags.Rebase;
        }

        return repo;
    }
}
