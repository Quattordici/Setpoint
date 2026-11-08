using Bogus;
using Setpoint.Core.Policy.Loading;

namespace Setpoint.Core.Tests.TestData;

/// <summary>
/// Builds an in-memory policy repo. The default is the smallest valid repo: one GitHub target and a baseline
/// with one rule. Tests replace or add the one file they care about.
/// </summary>
public sealed class PolicyRepoBuilder
{
    private static readonly Faker s_faker = new();

    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public PolicyRepoBuilder()
    {
        TargetName = s_faker.Internet.DomainWord().ToLowerInvariant();
        Owner = s_faker.Internet.UserName().ToLowerInvariant().Replace('.', '-');
        _files["targets.yml"] = $"""
            version: 1
            targets:
              {TargetName}:
                platform: github
                scope: [{Owner}/*]
            """;
        _files["baseline.yml"] = """
            version: 1
            layer: baseline
            rules:
              requireCommentResolution: true
            """;
    }

    /// <summary>The name of the default target.</summary>
    public string TargetName { get; }

    /// <summary>The GitHub owner in the default target's scope.</summary>
    public string Owner { get; }

    public PolicyRepoBuilder WithTargets(string yaml) => WithFile("targets.yml", yaml);

    public PolicyRepoBuilder WithBaseline(string yaml) => WithFile("baseline.yml", yaml);

    public PolicyRepoBuilder WithGroup(string name, string yaml) => WithFile($"groups/{name}.yml", yaml);

    public PolicyRepoBuilder WithRepoOverride(string repo, string yaml) => WithFile($"repos/{repo}.yml", yaml);

    public PolicyRepoBuilder WithFile(string path, string yaml)
    {
        _files[path] = yaml;
        return this;
    }

    public PolicyRepoBuilder Without(string path)
    {
        _files.Remove(path);
        return this;
    }

    public IReadOnlyList<PolicySourceFile> Build() =>
        [.. _files.Select(f => new PolicySourceFile(f.Key, f.Value))];
}
