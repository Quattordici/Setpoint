namespace Setpoint.Adapters.AzureDevOps;

/// <summary>
/// The branch policy types Setpoint manages, by their fixed ids. The preflight checks that the server has each one,
/// because the Status id comes from Microsoft's Terraform provider rather than the REST reference.
/// </summary>
internal static class AzureDevOpsPolicyTypes
{
    public const string MinimumReviewers = "fa4e907d-c16b-4a4c-9dfa-4906e5d171dd";

    public const string Build = "0609b952-1397-4640-95ec-e00a01b2c241";

    public const string Status = "cbdc66da-9728-4af8-aada-9a5a32e4a226";

    public const string CommentRequirements = "c6a1889d-b943-4856-b76f-9e46bb6b0df2";

    public const string MergeStrategy = "fa4e907d-c16b-4a4c-9dfa-4916e5d171ab";

    /// <summary>Every type Setpoint needs, with the name the preflight reports when one is missing.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Required =
    [
        (MinimumReviewers, "Minimum number of reviewers"),
        (Build, "Build"),
        (Status, "Status"),
        (CommentRequirements, "Comment requirements"),
        (MergeStrategy, "Require a merge strategy"),
    ];
}
