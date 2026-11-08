using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Setpoint.Core.Policy.Model;

/// <summary><c>targets.yml</c>: the named platform endpoints the policy applies to.</summary>
public sealed record TargetsFile
{
    /// <summary>The policy format version. Always 1.</summary>
    [Description("The policy format version. Must be 1.")]
    public required int Version { get; init; }

    /// <summary>The targets, keyed by target name. Selectors and repo override paths start with the target name.</summary>
    [Description("The targets, keyed by name. Selectors and repo override paths start with the target name.")]
    public required IReadOnlyDictionary<string, TargetDefinition> Targets { get; init; }
}

/// <summary>One named target: github.com, or one Azure DevOps organization or collection.</summary>
public sealed record TargetDefinition
{
    /// <summary>Which platform the target is.</summary>
    [Description("Which platform the target is.")]
    public required Platform Platform { get; init; }

    /// <summary>The endpoint URL. Required on Azure DevOps; defaults to github.com on GitHub.</summary>
    [Description("The endpoint URL: an Azure DevOps organization or collection URL. Defaults to github.com on GitHub.")]
    public Uri? Url { get; init; }

    /// <summary>How Setpoint signs in. Defaults to pat.</summary>
    [Description("How Setpoint signs in. Defaults to pat.")]
    public AuthMethod? Auth { get; init; }

    /// <summary>Globs over project-or-owner/repo for the repos this target manages.</summary>
    [Description("Globs over <project-or-owner>/<repo> for the repos this target manages. Required on GitHub; defaults to the whole collection on Azure DevOps.")]
    public IReadOnlyList<string>? Scope { get; init; }

    /// <summary>Globs over project-or-owner/repo that are left out of scope.</summary>
    [Description("Globs over <project-or-owner>/<repo> that are left out of scope.")]
    public IReadOnlyList<string>? Exclude { get; init; }

    /// <summary>The environment variable holding the token, instead of SETPOINT_&lt;TARGET&gt;_TOKEN.</summary>
    [Description("The environment variable that holds the token, instead of SETPOINT_<TARGET>_TOKEN.")]
    public string? TokenEnv { get; init; }

    /// <summary>The GitHub App id, for auth: app.</summary>
    [Description("The GitHub App id, for auth: app. The private key comes from SETPOINT_<TARGET>_APP_KEY.")]
    public long? AppId { get; init; }

    /// <summary>Whether GitHub forks in scope are managed. They are skipped by default.</summary>
    [Description("Whether forks in scope are managed. They are skipped by default.")]
    public bool IncludeForks { get; init; }
}

/// <summary>A platform Setpoint has an adapter for.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<Platform>))]
public enum Platform
{
    /// <summary>GitHub.</summary>
    [JsonStringEnumMemberName("github")]
    GitHub,

    /// <summary>Azure DevOps Services or Azure DevOps Server.</summary>
    [JsonStringEnumMemberName("azuredevops")]
    AzureDevOps,
}

/// <summary>How Setpoint signs in to a target.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<AuthMethod>))]
public enum AuthMethod
{
    /// <summary>A personal access token from SETPOINT_&lt;TARGET&gt;_TOKEN. The default.</summary>
    Pat,

    /// <summary>The Azure Pipelines job token, from SYSTEM_ACCESSTOKEN.</summary>
    AccessToken,

    /// <summary>Windows authentication against Azure DevOps Server.</summary>
    Integrated,

    /// <summary>A GitHub App installation token.</summary>
    App,

    /// <summary>Microsoft Entra ID through Azure.Identity, on Azure DevOps Services.</summary>
    Entra,
}
