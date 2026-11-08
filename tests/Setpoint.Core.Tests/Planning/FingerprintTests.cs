using System.Text.Json.Nodes;
using Setpoint.Core.Planning;

namespace Setpoint.Core.Tests.Planning;

/// <summary>Tests for <see cref="Fingerprint"/> and <see cref="CanonicalJson"/>: what changes a fingerprint and what doesn't.</summary>
public sealed class FingerprintTests
{
    /// <summary>
    /// Given the same object with its keys in a different order,
    /// when both are fingerprinted,
    /// then the fingerprints are equal.
    /// </summary>
    [Fact(DisplayName = "👍 Key order doesn't change a fingerprint")]
    public void Of_KeysReordered_Equal()
    {
        // Arrange
        JsonNode first = JsonNode.Parse("""{ "id": 17, "settings": { "a": 1, "b": [1, 2] } }""")!;
        JsonNode second = JsonNode.Parse("""{ "settings": { "b": [1, 2], "a": 1 }, "id": 17 }""")!;

        // Act
        string a = Fingerprint.Of(first);
        string b = Fingerprint.Of(second);

        // Assert
        a.ShouldBe(b);
        a.ShouldMatch("^sha256:[0-9a-f]{64}$");
    }

    /// <summary>
    /// Given an object, and the same object with one value or one array's order changed,
    /// when they are fingerprinted,
    /// then each change gives a different fingerprint, because a native array's order can matter.
    /// </summary>
    [Theory(DisplayName = "👎 A changed value or array order changes a fingerprint")]
    [InlineData("""{ "id": 17, "settings": { "a": 2, "b": [1, 2] } }""")]
    [InlineData("""{ "id": 17, "settings": { "a": 1, "b": [2, 1] } }""")]
    [InlineData("""{ "id": 17, "settings": { "a": 1, "b": [1, 2] }, "revision": 2 }""")]
    public void Of_Changed_Differs(string changed)
    {
        // Arrange
        JsonNode original = JsonNode.Parse("""{ "id": 17, "settings": { "a": 1, "b": [1, 2] } }""")!;

        // Act
        string before = Fingerprint.Of(original);
        string after = Fingerprint.Of(JsonNode.Parse(changed)!);

        // Assert
        after.ShouldNotBe(before);
    }

    /// <summary>
    /// Given two match keys,
    /// when "nothing matches" is fingerprinted for each,
    /// then they differ from each other and from any object fingerprint.
    /// </summary>
    [Fact(DisplayName = "🖐️ Nothing-matches fingerprints are per match key")]
    public void NothingMatches_PerKey_Distinct()
    {
        // Act
        string first = Fingerprint.NothingMatches("ado:minimumReviewers/refs/heads/main");
        string second = Fingerprint.NothingMatches("ado:build/payments-ci");

        // Assert
        first.ShouldNotBe(second);
        first.ShouldNotBe(Fingerprint.Of(JsonValue.Create("ado:minimumReviewers/refs/heads/main")));
    }

    /// <summary>
    /// Given two rule values that differ only in list order and null-valued keys,
    /// when they are compared as rule values,
    /// then they are equal.
    /// </summary>
    [Fact(DisplayName = "🖐️ Rule values ignore list order and null keys")]
    public void RuleEquals_OrderAndNulls_Equal()
    {
        // Arrange
        JsonNode left = JsonNode.Parse("""{ "types": ["squash", "merge"], "note": null }""")!;
        JsonNode right = JsonNode.Parse("""{ "types": ["merge", "squash"] }""")!;

        // Act
        bool equal = CanonicalJson.RuleEquals(left, right);

        // Assert
        equal.ShouldBeTrue();
    }
}
