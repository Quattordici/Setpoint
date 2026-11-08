using System.Security.Cryptography;
using System.Text;
using Setpoint.Core.Policy.Loading;

namespace Setpoint.Core.Planning;

/// <summary>The hash of a policy repo that a plan records. apply refuses the plan if the policy changed since.</summary>
public static class PolicyHash
{
    /// <summary>
    /// SHA-256 over each file's path and text, in path order. Line endings are normalised, so a Windows and a Linux
    /// checkout of the same commit hash the same.
    /// </summary>
    public static string Compute(IEnumerable<PolicySourceFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (PolicySourceFile file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Path));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(file.Text.ReplaceLineEndings("\n")));
            hash.AppendData([0]);
        }

        return Fingerprint.Prefix + Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
