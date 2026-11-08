using Setpoint.Core.Planning;

namespace Setpoint.Core.Providers;

/// <summary>The objects apply writes for one repo, exactly as the plan file holds them.</summary>
/// <param name="Objects">The planned objects, in plan order.</param>
public sealed record RepoChangeSet(IReadOnlyList<PlanObject> Objects);

/// <summary>What happened when one repo's change set was applied.</summary>
/// <param name="Objects">One outcome per object that was attempted.</param>
public sealed record ApplyResult(IReadOnlyList<ObjectApplyOutcome> Objects)
{
    /// <summary>True when every object was written.</summary>
    public bool Succeeded => Objects.All(o => o.Succeeded);
}

/// <summary>The outcome for one object.</summary>
/// <param name="Key">The object's plan key.</param>
/// <param name="Succeeded">Whether the write went through.</param>
/// <param name="Error">Why it failed, redacted, or null.</param>
public sealed record ObjectApplyOutcome(string Key, bool Succeeded, string? Error = null);
