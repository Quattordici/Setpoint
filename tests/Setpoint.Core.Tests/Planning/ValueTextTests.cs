using System.Text.Json.Nodes;
using Setpoint.Core.Planning;

namespace Setpoint.Core.Tests.Planning;

/// <summary>Tests for <see cref="ValueText"/>: how plan output writes rule values for people.</summary>
public sealed class ValueTextTests
{
    /// <summary>
    /// Given rule values of each shape,
    /// when they are formatted,
    /// then they read the way plan output shows them.
    /// </summary>
    [Theory(DisplayName = "👍 Values read the way plan output shows them")]
    [InlineData("""{ "count": 2, "resetOnPush": true }""", "count 2, resetOnPush")]
    [InlineData("""{ "count": 1, "resetOnPush": false }""", "count 1, not resetOnPush")]
    [InlineData("""["squash", "rebase"]""", "squash, rebase")]
    [InlineData("""[{ "name": "payments-ci" }, { "name": "lint" }]""", "payments-ci, lint")]
    [InlineData("true", "true")]
    [InlineData("[]", "(empty)")]
    public void Format_Shapes_ReadForPeople(string json, string expected)
    {
        // Act
        string text = ValueText.Format(JsonNode.Parse(json));

        // Assert
        text.ShouldBe(expected);
    }

    /// <summary>
    /// Given no value,
    /// when it is formatted,
    /// then it reads as the placeholder asked for.
    /// </summary>
    [Fact(DisplayName = "🖐️ A missing value reads as its placeholder")]
    public void Format_Null_Placeholder()
    {
        // Act / Assert
        ValueText.Format(null).ShouldBe("(none)");
        ValueText.Format(null, ValueText.Removed).ShouldBe("(removed)");
    }
}
