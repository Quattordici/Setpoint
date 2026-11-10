using System.Text;
using System.Text.Json.Nodes;

namespace Setpoint.Adapters.AzureDevOps.Policies;

/// <summary>
/// The Force push permission on a default branch, in the Git Repositories security namespace. One bit covers both
/// force-pushing and deleting the branch, so it carries <c>protectFromForcePushAndDelete</c> on its own.
/// </summary>
internal static class ForcePushAcl
{
    /// <summary>The Git Repositories security namespace.</summary>
    public const string NamespaceId = "2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87";

    /// <summary>The ForcePush permission bit: "Force push (rewrite history, delete branches and tags)".</summary>
    public const int ForcePushBit = 8;

    /// <summary>
    /// The security token of a branch: <c>repoV2/{project}/{repo}/refs/heads/{branch}</c>, where each
    /// <c>/</c>-separated part of the branch name is its UTF-16LE bytes in hex, so <c>main</c> is <c>6d00610069006e00</c>.
    /// </summary>
    public static string BranchToken(string projectId, string repoId, string branch) =>
        $"repoV2/{projectId}/{repoId}/refs/heads/{string.Join('/', branch.Split('/').Select(Hex))}";

    public static string MatchKey(string refName) => $"ado:forcePush/{refName}";

    public static string LiveKey(RepoContext context) => $"ado:acl/{context.Repo.Id}/{context.RefName}";

    public static string CreateKey(RepoContext context) => $"ado:new/acl/{context.Repo.Id}/{context.RefName}";

    /// <summary>The entries of an ACL, as returned by Access Control Lists - Query.</summary>
    public static IEnumerable<AccessControlEntry> Entries(JsonObject acl)
    {
        if (acl["acesDictionary"] is not JsonObject aces)
        {
            yield break;
        }

        foreach ((string key, JsonNode? value) in aces)
        {
            if (value is JsonObject ace)
            {
                yield return new AccessControlEntry(
                    PolicyMapper.Text(ace["descriptor"]) ?? key,
                    PolicyMapper.Number(ace["allow"]) ?? 0,
                    PolicyMapper.Number(ace["deny"]) ?? 0);
            }
        }
    }

    /// <summary>
    /// The payload that sets one entry: the body of <c>POST _apis/accesscontrolentries/{namespace}</c> with
    /// <c>merge: false</c>. That replaces this identity's entry and leaves every other entry alone. The entry is the
    /// live one with only the Force push bit changed, so its other bits are kept, and the plan's fingerprint makes
    /// sure the live entry hasn't changed since. Merging can't be used: it ORs the bits in, so it could never clear
    /// the deny when the protection is taken off.
    /// </summary>
    public static JsonObject Payload(string token, AccessControlEntry entry) => new()
    {
        ["token"] = token,
        ["merge"] = false,
        ["accessControlEntries"] = new JsonArray((JsonNode)new JsonObject
        {
            ["descriptor"] = entry.Descriptor,
            ["allow"] = entry.Allow,
            ["deny"] = entry.Deny,
        }),
    };

    /// <summary>The entry a payload sets, or null when it isn't one.</summary>
    public static AccessControlEntry? FromPayload(JsonObject payload) =>
        payload["accessControlEntries"] is JsonArray { Count: 1 } entries && entries[0] is JsonObject ace
            ? new AccessControlEntry(PolicyMapper.Text(ace["descriptor"]) ?? "", PolicyMapper.Number(ace["allow"]) ?? 0, PolicyMapper.Number(ace["deny"]) ?? 0)
            : null;

    private static string Hex(string part)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(part);
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>One access control entry.</summary>
/// <param name="Descriptor">The identity descriptor it is for.</param>
/// <param name="Allow">The allowed permission bits.</param>
/// <param name="Deny">The denied permission bits.</param>
internal sealed record AccessControlEntry(string Descriptor, int Allow, int Deny)
{
    public bool DeniesForcePush => (Deny & ForcePushAcl.ForcePushBit) != 0;

    public bool AllowsForcePush => (Allow & ForcePushAcl.ForcePushBit) != 0 && !DeniesForcePush;

    /// <summary>This entry with Force push denied or no longer denied, and every other bit kept.</summary>
    public AccessControlEntry WithForcePushDenied(bool denied) => denied
        ? this with { Allow = Allow & ~ForcePushAcl.ForcePushBit, Deny = Deny | ForcePushAcl.ForcePushBit }
        : this with { Deny = Deny & ~ForcePushAcl.ForcePushBit };

    /// <summary>The entry as the live object's native form, which the fingerprint is taken over.</summary>
    public JsonObject ToNative(string token) => new()
    {
        ["token"] = token,
        ["descriptor"] = Descriptor,
        ["allow"] = Allow,
        ["deny"] = Deny,
    };
}
