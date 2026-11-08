using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Setpoint.Core.Planning;

/// <summary>
/// Fingerprints of live objects, taken at plan time and checked again before apply writes anything. Any difference
/// makes the whole plan stale.
/// </summary>
public static class Fingerprint
{
    /// <summary>The prefix every fingerprint carries, so the algorithm can change in a later format version.</summary>
    public const string Prefix = "sha256:";

    /// <summary>The fingerprint of a live object, over its canonical JSON.</summary>
    public static string Of(JsonNode native) => Hash("object\n" + CanonicalJson.ToText(CanonicalJson.ForFingerprint(native)));

    /// <summary>
    /// The fingerprint of "nothing matches <paramref name="matchKey"/>", for an object plan creates. If a hand-made
    /// object with that match key appears before apply, the fingerprint no longer holds and the plan is stale.
    /// </summary>
    public static string NothingMatches(string matchKey) => Hash("nothing-matches\n" + matchKey);

    private static string Hash(string text) =>
        Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
