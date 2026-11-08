using System.Text.Json.Nodes;
using Setpoint.Core.Planning;

namespace Setpoint.Core.Tests.Planning;

/// <summary>Tests for <see cref="Strictness"/>: when an unmanaged setting asks more than the policy for each neutral rule.</summary>
public sealed class StrictnessTests
{
    public static TheoryData<string, string, string?, bool> Cases => new()
    {
        { "minimumReviewers", """{ "count": 3 }""", """{ "count": 2 }""", true },
        { "minimumReviewers", """{ "count": 2 }""", """{ "count": 2 }""", false },
        { "minimumReviewers", """{ "count": 1, "resetOnPush": true }""", """{ "count": 2 }""", true },
        { "minimumReviewers", """{ "count": 1 }""", null, true },
        { "requiredChecks", """[{ "name": "build" }, { "name": "lint" }]""", """[{ "name": "build" }]""", true },
        { "requiredChecks", """[{ "name": "BUILD" }]""", """[{ "name": "build" }]""", false },
        { "requireCommentResolution", "true", null, true },
        { "requireCommentResolution", "true", "true", false },
        { "allowedMergeTypes", """["squash"]""", """["squash", "rebase"]""", true },
        { "allowedMergeTypes", """["squash", "rebase", "merge"]""", """["squash"]""", false },
        { "github.pullRequest", """{ "requireCodeOwnerReview": true }""", null, false },
    };

    /// <summary>
    /// Given an unmanaged value and the declared value for a rule,
    /// when they are compared,
    /// then the unmanaged one is stricter exactly when it asks more of a pull request.
    /// </summary>
    [Theory(DisplayName = "🖐️ Stricter means asking more of a pull request")]
    [MemberData(nameof(Cases))]
    public void IsStricter_Cases_Match(string rule, string unmanaged, string? declared, bool expected)
    {
        // Act
        bool stricter = Strictness.IsStricter(rule, JsonNode.Parse(unmanaged), declared is null ? null : JsonNode.Parse(declared));

        // Assert
        stricter.ShouldBe(expected);
    }
}
