using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Core.Providers;

/// <summary>
/// One platform's adapter. The core owns loading, merging, diffing, planning and fingerprints; an adapter only
/// translates between the platform's API and the neutral model. Reads come back as a <see cref="LiveSnapshot"/>,
/// the desired state goes out as <see cref="DesiredObject"/>s with their native payloads, and writes arrive as a
/// <see cref="RepoChangeSet"/> per repo, which the adapter batches as it likes.
/// </summary>
/// <remarks>
/// The spec gives five members. <see cref="MapObjects"/> is the sixth: the plan file holds the full native payload
/// apply sends, and only the adapter can build it. It is pure, so the core can diff and plan without more calls.
/// </remarks>
public interface IProviderAdapter
{
    /// <summary>The platform this adapter serves.</summary>
    Platform Platform { get; }

    /// <summary>The adapter's provider block, such as <c>github:</c>, whose rules it receives besides the neutral ones.</summary>
    IProviderBlockDefinition? ProviderBlock { get; }

    /// <summary>Reaches the target and runs its preflight: what it is, which API version, and who Setpoint signs in as.</summary>
    /// <exception cref="Exception">Any failure means the target is unreachable.</exception>
    Task<TargetInfo> DescribeAsync(Target target, CancellationToken cancellationToken);

    /// <summary>Lists the target's repos. The core keeps only those in the target's scope and outside its excludes.</summary>
    IAsyncEnumerable<ListedRepo> ListReposAsync(Target target, CancellationToken cancellationToken);

    /// <summary>Which rules can apply to the repo, or why the repo is skipped (such as a private repo on GitHub Free).</summary>
    Task<RepoCapabilities> GetCapabilitiesAsync(RepoRef repo, CancellationToken cancellationToken);

    /// <summary>Reads the repo's live state and normalises it.</summary>
    Task<LiveSnapshot> ReadAsync(RepoRef repo, CancellationToken cancellationToken);

    /// <summary>
    /// Maps the desired state to the native objects that should exist, each with the payload apply would send.
    /// <paramref name="live"/> is there so an owned object can keep the parts the policy leaves unmanaged.
    /// Objects whose rules already match live state are dropped by the core, so this can map every rule.
    /// </summary>
    IReadOnlyList<DesiredObject> MapObjects(DesiredState desired, LiveSnapshot live);

    /// <summary>Writes one repo's planned objects. A failure stops that repo; the caller carries on with the others.</summary>
    Task<ApplyResult> ApplyAsync(RepoRef repo, RepoChangeSet changes, CancellationToken cancellationToken);
}
