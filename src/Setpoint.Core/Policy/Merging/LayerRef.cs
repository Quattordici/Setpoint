using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Core.Policy.Merging;

/// <summary>Which layer a value came from, for plan output (<c>from → to [layer]</c>) and diagnostics.</summary>
/// <param name="Kind">Baseline, group or repo override.</param>
/// <param name="Name">The group name for a group; otherwise null.</param>
/// <param name="Location">Where the rule is declared in that layer's file.</param>
public sealed record LayerRef(LayerKind Kind, string? Name, SourceLocation Location)
{
    /// <summary>A short label: <c>baseline</c>, <c>group payments</c> or <c>repo</c>.</summary>
    public string Label => Kind switch
    {
        LayerKind.Baseline => "baseline",
        LayerKind.Group => $"group {Name}",
        _ => "repo",
    };

    /// <inheritdoc />
    public override string ToString() => Label;
}
