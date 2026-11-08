using System.Text.Json.Nodes;

namespace Setpoint.Adapters.AzureDevOps.Tests.TestData;

/// <summary>
/// Builds a policy configuration as Azure DevOps returns it. By default an enabled, blocking minimum reviewers
/// policy scoped to exactly one repo's main branch.
/// </summary>
internal sealed class PolicyConfigBuilder
{
    private static readonly Bogus.Faker s_faker = new();

    private int _id = s_faker.Random.Int(1, 9_999);
    private string _typeId = AzureDevOpsPolicyTypes.MinimumReviewers;
    private string _typeName = "Minimum number of reviewers";
    private bool _enabled = true;
    private bool _blocking = true;
    private JsonArray _scope;
    private readonly JsonObject _settings = new()
    {
        ["minimumApproverCount"] = 2,
        ["creatorVoteCounts"] = false,
        ["allowDownvotes"] = false,
        ["resetOnSourcePush"] = true,
        ["blockLastPusherVote"] = false,
        ["requireVoteOnLastIteration"] = true,
    };

    public PolicyConfigBuilder(string repoId, string refName = "refs/heads/main") => _scope = Scope(repoId, refName, "Exact");

    public int Id => _id;

    public PolicyConfigBuilder WithId(int id)
    {
        _id = id;
        return this;
    }

    public PolicyConfigBuilder OfType(string typeId, string typeName)
    {
        _typeId = typeId;
        _typeName = typeName;
        _settings.Clear();
        return this;
    }

    public PolicyConfigBuilder BuildValidation(int definitionId, int validDuration = 720) =>
        OfType(AzureDevOpsPolicyTypes.Build, "Build")
            .With("buildDefinitionId", definitionId)
            .With("queueOnSourceUpdateOnly", true)
            .With("manualQueueOnly", false)
            .With("displayName", null)
            .With("validDuration", (double)validDuration);

    public PolicyConfigBuilder Status(string name, string? genre) =>
        OfType(AzureDevOpsPolicyTypes.Status, "Status").With("statusName", name).With("statusGenre", genre).With("invalidateOnSourceUpdate", false);

    public PolicyConfigBuilder CommentRequirements() => OfType(AzureDevOpsPolicyTypes.CommentRequirements, "Comment requirements");

    public PolicyConfigBuilder MergeStrategy(bool noFastForward, bool squash, bool rebase, bool rebaseMerge = false) =>
        OfType(AzureDevOpsPolicyTypes.MergeStrategy, "Require a merge strategy")
            .With("allowNoFastForward", noFastForward)
            .With("allowSquash", squash)
            .With("allowRebase", rebase)
            .With("allowRebaseMerge", rebaseMerge);

    public PolicyConfigBuilder With(string setting, JsonNode? value)
    {
        _settings[setting] = value;
        return this;
    }

    public PolicyConfigBuilder Without(string setting)
    {
        _settings.Remove(setting);
        return this;
    }

    public PolicyConfigBuilder Disabled()
    {
        _enabled = false;
        return this;
    }

    public PolicyConfigBuilder Optional()
    {
        _blocking = false;
        return this;
    }

    /// <summary>Scopes the policy to every repo in the project.</summary>
    public PolicyConfigBuilder ProjectWide()
    {
        _scope = [new JsonObject { ["repositoryId"] = null, ["refName"] = "refs/heads/main", ["matchKind"] = "Exact" }];
        return this;
    }

    public PolicyConfigBuilder ScopedTo(string? repoId, string? refName, string matchKind)
    {
        _scope = Scope(repoId, refName, matchKind);
        return this;
    }

    public PolicyConfigBuilder AlsoScopedTo(string repoId, string refName)
    {
        _scope.Add(new JsonObject { ["repositoryId"] = repoId, ["refName"] = refName, ["matchKind"] = "Exact" });
        return this;
    }

    public JsonObject Build()
    {
        var settings = (JsonObject)_settings.DeepClone();
        settings["scope"] = _scope.DeepClone();
        return new JsonObject
        {
            ["createdBy"] = new JsonObject { ["displayName"] = s_faker.Name.FullName(), ["id"] = Guid.NewGuid().ToString() },
            ["createdDate"] = "2026-10-01T09:00:00Z",
            ["isEnabled"] = _enabled,
            ["isBlocking"] = _blocking,
            ["isDeleted"] = false,
            ["settings"] = settings,
            ["isEnterpriseManaged"] = false,
            ["_links"] = new JsonObject { ["self"] = new JsonObject { ["href"] = $"https://ado.example/_apis/policy/configurations/{_id}" } },
            ["revision"] = 3,
            ["id"] = _id,
            ["url"] = $"https://ado.example/_apis/policy/configurations/{_id}",
            ["type"] = new JsonObject { ["id"] = _typeId, ["url"] = "https://ado.example/_apis/policy/types/x", ["displayName"] = _typeName },
        };
    }

    private static JsonArray Scope(string? repoId, string? refName, string matchKind) =>
        [new JsonObject { ["repositoryId"] = repoId, ["refName"] = refName, ["matchKind"] = matchKind }];
}
