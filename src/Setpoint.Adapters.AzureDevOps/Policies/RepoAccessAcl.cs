using System.Text.Json.Nodes;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.AzureDevOps.Policies;

/// <summary>
/// The neutral <c>access</c> rule on Azure DevOps: each named identity's entry on the repo's token in the Git
/// Repositories namespace. Each role is a fixed set of allow bits, and Setpoint owns only the bits of the admin set;
/// deny bits, the bits outside the set and identities the policy doesn't name are left alone.
/// </summary>
internal static class RepoAccessAcl
{
    /// <summary>The neutral rule.</summary>
    public const string Access = "access";

    /// <summary>Read (GenericRead).</summary>
    public const int ReadBits = 2;

    /// <summary>
    /// Read, plus Contribute (4), Create branch (16), Create tag (32), Manage notes (64) and Contribute to pull
    /// requests (16384): what the built-in Contributors group has.
    /// </summary>
    public const int WriteBits = ReadBits | 4 | 16 | 32 | 64 | 16384;

    /// <summary>
    /// Write, plus Delete or disable repository (512), Rename repository (1024), Edit policies (2048), Remove
    /// others' locks (4096) and Manage permissions (8192). Force push (8) is the force-push rule's; Bypass policies
    /// (128, 32768) and Create repository (256) are never granted by a role.
    /// </summary>
    public const int AdminBits = WriteBits | 512 | 1024 | 2048 | 4096 | 8192;

    /// <summary>Every bit a role can set: the bits Setpoint owns on a named identity's entry.</summary>
    public const int Mask = AdminBits;

    /// <summary>The repo's token: <c>repoV2/{project}/{repo}</c>.</summary>
    public static string RepoToken(string projectId, string repoId) => $"repoV2/{projectId}/{repoId}";

    /// <summary>The project's token, whose entries every repo in it inherits: <c>repoV2/{project}</c>.</summary>
    public static string ProjectToken(string projectId) => $"repoV2/{projectId}";

    /// <summary>An identity's match key: <c>ado:access/{descriptor}</c>.</summary>
    public static string MatchKey(string descriptor) => $"ado:access/{descriptor.ToLowerInvariant()}";

    /// <summary>True when the match key is an access entry's.</summary>
    public static bool IsMatchKey(string matchKey) => matchKey.StartsWith("ado:access/", StringComparison.Ordinal);

    /// <summary>The bits a role sets, or 0 for <c>none</c>.</summary>
    public static int Bits(string role) => role switch
    {
        "read" => ReadBits,
        "write" => WriteBits,
        "admin" => AdminBits,
        "none" => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "not a neutral role"),
    };

    /// <summary>
    /// The role the managed allow bits make: <c>read</c>, <c>write</c>, <c>admin</c>, <c>none</c>, or the bits in hex
    /// when they are no role, so they never pass for one.
    /// </summary>
    public static string Role(int allow) => (allow & Mask) switch
    {
        0 => "none",
        ReadBits => "read",
        WriteBits => "write",
        AdminBits => "admin",
        int bits => $"custom (0x{bits:x})",
    };

    /// <summary>The repo token's entries that grant a role bit, as live objects named for people.</summary>
    public static IEnumerable<LiveObject> Read(string token, JsonObject acl, Func<string, string> identityName)
    {
        foreach (AccessControlEntry ace in ForcePushAcl.Entries(acl).Where(a => (a.Allow & Mask) != 0))
        {
            string name = identityName(ace.Descriptor);
            yield return new LiveObject(
                $"ado:acl/{token}/{ace.Descriptor}",
                MatchKey(ace.Descriptor),
                ace.ToNative(token),
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { [Access] = new JsonObject { [name] = Role(ace.Allow) } },
                $"permissions of {name}");
        }
    }

    /// <summary>The project token's entries that grant a role bit, for the principals the policy names, keyed by the name as written.</summary>
    public static IEnumerable<UnmanagedSetting> Inherited(JsonObject? acl, IReadOnlyDictionary<string, Principal> named, string project)
    {
        if (acl is null)
        {
            yield break;
        }

        foreach (AccessControlEntry ace in ForcePushAcl.Entries(acl).Where(a => (a.Allow & Mask) != 0))
        {
            // Keyed by the name as the policy writes it, so the grant is compared with the role the policy gives.
            foreach ((string written, _) in named.Where(p => p.Value.Descriptor.Equals(ace.Descriptor, StringComparison.OrdinalIgnoreCase)))
            {
                yield return new UnmanagedSetting(
                    Access,
                    $"permissions inherited from project {project}",
                    new JsonObject { [written] = Role(ace.Allow) });
            }
        }
    }

    /// <summary>
    /// One object per principal the policy names: the live entry with the role's bits set and the other managed bits
    /// cleared. <c>none</c> clears them, which updates the entry rather than deleting it.
    /// </summary>
    /// <param name="declared">The access map, as the policy writes it.</param>
    /// <param name="principals">Each declared name resolved, by the name as the policy writes it.</param>
    /// <param name="live">The live snapshot.</param>
    /// <param name="token">The repo's token.</param>
    public static IEnumerable<DesiredObject> Map(JsonObject declared, IReadOnlyDictionary<string, Principal> principals, LiveSnapshot live, string token)
    {
        foreach ((string written, JsonNode? roleNode) in declared)
        {
            string role = roleNode?.GetValue<string>() ?? "none";
            Principal principal = principals.GetValueOrDefault(written)
                ?? throw new PolicyMappingException(DiagnosticCodes.PrincipalNotFound, $"access: '{written}' wasn't looked up.");
            string matchKey = MatchKey(principal.Descriptor);
            LiveObject? current = live.Objects.FirstOrDefault(o => o.MatchKey == matchKey);
            int allow = 0;
            int deny = 0;
            if (current?.Native is JsonObject native)
            {
                allow = PolicyMapper.Number(native["allow"]) ?? 0;
                deny = PolicyMapper.Number(native["deny"]) ?? 0;
            }

            int bits = Bits(role);
            var entry = new AccessControlEntry(principal.Descriptor, (allow & ~Mask) | bits, deny);
            var carried = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                [Access] = bits == 0 ? null : new JsonObject { [principal.Name] = Role(entry.Allow) },
            };

            if (bits == 0 && current is null)
            {
                // Nothing granted, and nothing to take away.
                yield return new DesiredObject(matchKey, $"ado:new/access/{principal.Name}", carried, null);
                continue;
            }

            yield return new DesiredObject(matchKey, $"ado:new/access/{principal.Name}", carried, ForcePushAcl.Payload(token, entry), $"permissions of {principal.Name} (new)");
        }
    }
}

/// <summary>An identity a name in the <c>access</c> rule resolved to.</summary>
/// <param name="Name">Its name for people, such as <c>[Payments]\Contributors</c>; live entries are named the same way.</param>
/// <param name="Descriptor">Its identity descriptor, which security entries are keyed by.</param>
internal sealed record Principal(string Name, string Descriptor);
