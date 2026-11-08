namespace Setpoint.Core.Policy;

/// <summary>
/// One repo, named the way selectors and repo override paths name it: <c>&lt;target&gt;/&lt;project-or-owner&gt;/&lt;repo&gt;</c>.
/// Comparison ignores case, as both GitHub and Azure DevOps do.
/// </summary>
/// <param name="Target">The target name from targets.yml.</param>
/// <param name="Owner">The Azure DevOps project or the GitHub owner.</param>
/// <param name="Name">The repo name.</param>
public sealed record RepoRef(string Target, string Owner, string Name)
{
    /// <summary>Parses <c>target/owner/repo</c>, or returns null when it doesn't have three non-empty parts.</summary>
    public static RepoRef? TryParse(string value)
    {
        string[] parts = value.Split('/');
        return parts is [{ Length: > 0 } target, { Length: > 0 } owner, { Length: > 0 } name]
            ? new RepoRef(target, owner, name)
            : null;
    }

    /// <inheritdoc />
    public bool Equals(RepoRef? other) =>
        other is not null
        && string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Target),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Owner),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

    /// <inheritdoc />
    public override string ToString() => $"{Target}/{Owner}/{Name}";
}
