using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Testing;

/// <summary>
/// An in-memory adapter for plan engine tests, with no HTTP. It models a platform where each rule lives in its
/// own native object, the way Azure DevOps keeps one policy per rule: the object for rule <c>r</c> has match key
/// <c>fake:r</c>, key <c>fake:policy/&lt;id&gt;</c> once it exists, and payload <c>{ rule, value }</c>.
/// </summary>
internal sealed class FakeProviderAdapter(Platform platform = Platform.GitHub, IProviderBlockDefinition? providerBlock = null) : IProviderAdapter
{
    private readonly Dictionary<RepoRef, FakeRepo> _repos = [];
    private int _nextId = 1;

    public Platform Platform { get; } = platform;

    public IProviderBlockDefinition? ProviderBlock { get; } = providerBlock;

    /// <summary>Set to make the preflight fail, as an unreachable target would.</summary>
    public Exception? DescribeFailure { get; set; }

    public TargetInfo Info { get; set; } = new("fake platform 1.0", "1.0", "setpoint-bot");

    /// <summary>The change sets apply received, by repo.</summary>
    public List<(RepoRef Repo, RepoChangeSet Changes)> Applied { get; } = [];

    /// <summary>Adds a repo with no live settings, and returns it for more setup.</summary>
    public FakeRepo AddRepo(RepoRef repo)
    {
        var fake = new FakeRepo(this);
        _repos[repo] = fake;
        return fake;
    }

    public Task<TargetInfo> DescribeAsync(Target target, CancellationToken cancellationToken) =>
        DescribeFailure is { } failure ? Task.FromException<TargetInfo>(failure) : Task.FromResult(Info);

    public async IAsyncEnumerable<ListedRepo> ListReposAsync(Target target, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach ((RepoRef repo, FakeRepo fake) in _repos.Where(r => r.Key.Target == target.Name))
        {
            yield return new ListedRepo(repo, fake.ListingSkip);
        }
    }

    public Task<RepoCapabilities> GetCapabilitiesAsync(RepoRef repo, CancellationToken cancellationToken) =>
        Task.FromResult(new RepoCapabilities(_repos[repo].CapabilitySkip));

    public Task<LiveSnapshot> ReadAsync(RepoRef repo, CancellationToken cancellationToken)
    {
        FakeRepo fake = _repos[repo];
        if (fake.ReadFailure is { } failure)
        {
            return Task.FromException<LiveSnapshot>(failure);
        }

        LiveObject[] objects =
        [
            .. fake.Objects.Select(o => new LiveObject(
                $"fake:policy/{o.Id}",
                $"fake:{o.Rule}",
                new JsonObject { ["id"] = o.Id, ["rule"] = o.Rule, ["value"] = o.Value?.DeepClone(), ["revision"] = o.Revision },
                new Dictionary<string, JsonNode?> { [o.Rule] = o.Value?.DeepClone() })),
        ];
        return Task.FromResult(new LiveSnapshot(objects, fake.Unmanaged, fake.Ambiguous, fake.Unknown));
    }

    public IReadOnlyList<DesiredObject> MapObjects(DesiredState desired, LiveSnapshot live) =>
    [
        .. desired.Rules.Values.Select(rule => new DesiredObject(
            $"fake:{rule.Key}",
            $"fake:new/{rule.Key}",
            new Dictionary<string, JsonNode?> { [rule.Key] = rule.Value?.DeepClone() },
            rule.Value is null ? null : new JsonObject { ["rule"] = rule.Key, ["value"] = rule.Value.DeepClone() })),
    ];

    public Task<ApplyResult> ApplyAsync(RepoRef repo, RepoChangeSet changes, CancellationToken cancellationToken)
    {
        Applied.Add((repo, changes));
        return Task.FromResult(new ApplyResult([.. changes.Objects.Select(o => new ObjectApplyOutcome(o.Key, true))]));
    }

    /// <summary>One fake repo's live state.</summary>
    internal sealed class FakeRepo(FakeProviderAdapter owner)
    {
        public List<(int Id, string Rule, JsonNode? Value, int Revision)> Objects { get; } = [];

        public List<UnmanagedSetting> Unmanaged { get; } = [];

        public List<AmbiguousMatch> Ambiguous { get; } = [];

        public List<string> Unknown { get; } = [];

        public Diagnostic? ListingSkip { get; set; }

        public Diagnostic? CapabilitySkip { get; set; }

        public Exception? ReadFailure { get; set; }

        /// <summary>Adds a live object carrying <paramref name="rule"/>, with its value as JSON text.</summary>
        public FakeRepo WithLive(string rule, string json)
        {
            Objects.Add((owner._nextId++, rule, JsonNode.Parse(json), 1));
            return this;
        }

        /// <summary>Bumps every object's revision, as a hand edit between plan and apply would.</summary>
        public void Touch()
        {
            for (int i = 0; i < Objects.Count; i++)
            {
                Objects[i] = Objects[i] with { Revision = Objects[i].Revision + 1 };
            }
        }
    }
}
