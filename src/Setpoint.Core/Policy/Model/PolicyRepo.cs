using Setpoint.Core.Policy.Loading;

namespace Setpoint.Core.Policy.Model;

/// <summary>A policy file after loading: its path, its contents, and where each key sits in the file.</summary>
/// <typeparam name="T">The file's model type.</typeparam>
/// <param name="Path">The path relative to the policy repo root, with forward slashes.</param>
/// <param name="Content">The file's contents.</param>
/// <param name="Source">Where each key and value sits, so later checks can point at a line.</param>
public sealed record PolicyFile<T>(string Path, T Content, SourceMap Source);

/// <summary>Every policy file in a policy repo, loaded and shape-checked but not yet merged.</summary>
public sealed record PolicyRepo
{
    /// <summary><c>targets.yml</c>.</summary>
    public required PolicyFile<TargetsFile> Targets { get; init; }

    /// <summary><c>baseline.yml</c>.</summary>
    public required PolicyFile<PolicyLayer> Baseline { get; init; }

    /// <summary>The files under <c>groups/</c>, ordered by path.</summary>
    public required IReadOnlyList<PolicyFile<PolicyLayer>> Groups { get; init; }

    /// <summary>The files under <c>repos/</c>, ordered by path.</summary>
    public required IReadOnlyList<PolicyFile<PolicyLayer>> RepoOverrides { get; init; }
}
