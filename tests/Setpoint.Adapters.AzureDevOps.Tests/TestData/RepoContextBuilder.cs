using Setpoint.Adapters.AzureDevOps.Api;
using Setpoint.Adapters.AzureDevOps.Policies;

namespace Setpoint.Adapters.AzureDevOps.Tests.TestData;

/// <summary>Builds what the adapter knows about a repo once it is read. By default main, two pipelines, and a known Project Valid Users group.</summary>
internal sealed class RepoContextBuilder
{
    private static readonly Bogus.Faker s_faker = new();

    private readonly string _repoId = Guid.NewGuid().ToString();
    private readonly string _projectId = Guid.NewGuid().ToString();
    private string _branch = "main";
    private readonly List<AdoBuildDefinition> _pipelines = [new(11, "payments-ci", "\\"), new(12, "payments-security-scan", "\\")];
    private string? _validUsers = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-" + s_faker.Random.Number(100_000, 999_999) + "-0-0-0-3";

    public string RepoId => _repoId;

    public string ProjectId => _projectId;

    public string? ValidUsers => _validUsers;

    public RepoContextBuilder OnBranch(string branch)
    {
        _branch = branch;
        return this;
    }

    public RepoContextBuilder WithPipeline(int id, string name, string path = "\\")
    {
        _pipelines.Add(new AdoBuildDefinition(id, name, path));
        return this;
    }

    public RepoContextBuilder WithoutValidUsers()
    {
        _validUsers = null;
        return this;
    }

    public RepoContext Build() => new(
        new AdoRepository
        {
            Id = _repoId,
            Name = "payments-api",
            Project = new AdoProject(_projectId, "Payments"),
            DefaultBranch = $"refs/heads/{_branch}",
        },
        [.. _pipelines],
        _validUsers);
}
