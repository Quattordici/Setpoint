using System.ComponentModel;
using System.Text.Json.Serialization;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Policy.Model;

/// <summary>Which layer a file is. Layers merge top-down: baseline, then groups, then the repo override.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<LayerKind>))]
public enum LayerKind
{
    /// <summary><c>baseline.yml</c>: applies to every repo in scope.</summary>
    Baseline,

    /// <summary><c>groups/&lt;name&gt;.yml</c>: applies to the repos its selectors match.</summary>
    Group,

    /// <summary><c>repos/&lt;target&gt;/&lt;project-or-owner&gt;/&lt;repo&gt;.yml</c>: applies to one repo.</summary>
    Repo,
}

/// <summary>One layer file: the baseline, a group or a repo override.</summary>
public sealed record PolicyLayer
{
    /// <summary>The policy format version. Always 1.</summary>
    [Description("The policy format version. Must be 1.")]
    public required int Version { get; init; }

    /// <summary>Which layer this file is.</summary>
    [Description("Which layer this file is: baseline, group or repo.")]
    public required LayerKind Layer { get; init; }

    /// <summary>The group's name. Groups only; it must match the file name.</summary>
    [Description("The group's name. Groups only; it must match the file name.")]
    public string? Name { get; init; }

    /// <summary>The repo this override is for, as target/project-or-owner/repo. Repo overrides only; it must match the file path.</summary>
    [Description("The repo this override is for, as <target>/<project-or-owner>/<repo>. Repo overrides only; it must match the file path.")]
    public string? Repo { get; init; }

    /// <summary>Explicit repos or globs over target/project-or-owner/repo. Groups only.</summary>
    [Description("Explicit repos or globs over <target>/<project-or-owner>/<repo> that put repos in this group. Groups only.")]
    public IReadOnlyList<string>? Selectors { get; init; }

    /// <summary>Rules that groups and repo overrides may not change. Baseline only.</summary>
    [Description("Rules that groups and repo overrides may not change. Baseline only.")]
    public IReadOnlyList<string>? Locked { get; init; }

    /// <summary>The neutral rules this layer sets.</summary>
    [Description("The neutral rules this layer sets.")]
    // A setter, not init: the source-generated reader would otherwise overwrite the initializer when the key is absent.
    public NeutralRules Rules { get; set; } = new();

    /// <summary>The provider blocks, such as <c>github:</c> and <c>azuredevops:</c>, keyed by provider key.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, IProviderBlock> Providers { get; init; } = new Dictionary<string, IProviderBlock>();
}
