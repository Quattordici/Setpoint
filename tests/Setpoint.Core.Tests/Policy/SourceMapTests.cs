using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Loading;

namespace Setpoint.Core.Tests.Policy;

/// <summary>
/// Tests for <see cref="SourceMap"/> and the JSON path helpers it relies on, which trace a serializer
/// error back to a line in a policy file.
/// </summary>
public sealed class SourceMapTests
{
    /// <summary>
    /// Given JSON paths of each shape,
    /// when their parent is taken,
    /// then it is the enclosing property or list.
    /// </summary>
    [Theory(DisplayName = "👍 Finds the parent of a JSON path")]
    [InlineData("$.rules.minimumReviewers", "$.rules")]
    [InlineData("$.rules", "$")]
    [InlineData("$.rules.requiredChecks[2]", "$.rules.requiredChecks")]
    [InlineData("$.targets['my.target'].url", "$.targets['my.target']")]
    [InlineData("$.targets['my.target']", "$.targets")]
    public void Parent_Path_ReturnsEnclosingPath(string path, string expected)
    {
        // Arrange
        // The path and its expected parent come from the theory data.

        // Act
        string? parent = JsonPath.Parent(path);

        // Assert
        parent.ShouldBe(expected);
    }

    /// <summary>
    /// Given property names with and without special characters,
    /// when a path is built,
    /// then special names are bracketed as System.Text.Json writes them.
    /// </summary>
    [Theory(DisplayName = "🖐️ Brackets property names with special characters")]
    [InlineData("ado-onprem", "$.ado-onprem")]
    [InlineData("my.target", "$['my.target']")]
    [InlineData("two words", "$['two words']")]
    public void Property_Name_FormatsLikeSystemTextJson(string name, string expected)
    {
        // Arrange
        // The name and its expected path come from the theory data.

        // Act
        string path = JsonPath.Property(JsonPath.Root, name);

        // Assert
        path.ShouldBe(expected);
    }

    /// <summary>
    /// Given a path below a recorded key,
    /// when it is looked up,
    /// then the nearest recorded ancestor's location is returned, and an unknown path falls back to the file start.
    /// </summary>
    [Fact(DisplayName = "🖐️ Falls back to the nearest recorded ancestor")]
    public void Find_UnrecordedPath_UsesNearestAncestor()
    {
        // Arrange
        var map = new SourceMap("baseline.yml");
        var rulesLocation = new SourceLocation("baseline.yml", 4, 1);
        map.Record("$.rules", rulesLocation);

        // Act
        SourceLocation nested = map.Find("$.rules.minimumReviewers.count");
        SourceLocation unknown = map.Find("$.other");

        // Assert
        nested.ShouldBe(rulesLocation);
        unknown.ShouldBe(SourceLocation.StartOf("baseline.yml"));
    }
}
