using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Policy.Schema;

namespace Setpoint.Core.Planning;

/// <summary>
/// A plan: the plan file <c>plan --out</c> writes and <c>apply</c> reads, and what <c>plan --json</c> prints.
/// It is keyed on native objects. <see cref="PlanRepo.Changes"/> is the neutral view for people and reports;
/// apply never reads it. A plan never holds a secret.
/// </summary>
[Description("A Setpoint plan: the native objects apply will write, with fingerprints for the stale check, and the neutral changes for people.")]
public sealed record PlanDocument
{
    /// <summary>The format version this Setpoint writes and reads.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The published schema for this format.</summary>
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaUri;

    /// <summary>Where the plan file schema is published.</summary>
    public const string SchemaUri = PolicySchemaGenerator.BaseUri + PolicySchemaGenerator.PlanSchemaFileName;

    /// <summary>The plan file format version.</summary>
    [Description("The plan file format version. Always 1.")]
    public required int FormatVersion { get; init; }

    /// <summary>The Setpoint version that made the plan. apply refuses a plan from another version.</summary>
    [Description("The Setpoint version that made the plan. apply refuses a plan made by another version.")]
    public required string SetpointVersion { get; init; }

    /// <summary>When the plan was made.</summary>
    [Description("When the plan was made, in UTC.")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The policy the plan was made from.</summary>
    public required PlanPolicy Policy { get; init; }

    /// <summary>The options that shaped the plan.</summary>
    public required PlanOptionsRecord Options { get; init; }

    /// <summary>The targets that were reached, with what the preflight found.</summary>
    public required IReadOnlyList<PlanTarget> Targets { get; init; }

    /// <summary>Every repo in scope, including in-sync and skipped ones.</summary>
    public required IReadOnlyList<PlanRepo> Repos { get; init; }

    /// <summary>Diagnostics that belong to the run rather than one repo, such as an unreachable target.</summary>
    public required IReadOnlyList<PlanDiagnostic> Diagnostics { get; init; }

    /// <summary>Totals over the repos.</summary>
    public required PlanSummary Summary { get; init; }

    /// <summary>True when any repo has a change.</summary>
    [JsonIgnore]
    public bool HasChanges => Repos.Any(r => r.Objects.Count > 0);
}

/// <summary>The policy a plan was made from.</summary>
/// <param name="Hash">SHA-256 over every policy file's path and text. apply refuses the plan if it changed.</param>
/// <param name="Files">How many policy files there are.</param>
/// <param name="Commit">The policy repo's commit, when it is a git checkout.</param>
public sealed record PlanPolicy(
    [property: Description("SHA-256 over every policy file's path and text, with line endings normalised.")] string Hash,
    [property: Description("How many policy files were read.")] int Files,
    [property: Description("The policy repo commit, when known.")] string? Commit);

/// <summary>The options that shaped a plan.</summary>
/// <param name="Strict">Whether warnings were errors.</param>
/// <param name="SkipUnreachable">Whether unreachable targets were left out.</param>
/// <param name="AllowUnsupportedServer">Whether servers older than the minimum were accepted.</param>
/// <param name="MaxChanges">The most repos apply may change; 0 means no limit.</param>
public sealed record PlanOptionsRecord(bool Strict, bool SkipUnreachable, bool AllowUnsupportedServer, int MaxChanges);

/// <summary>A target the plan reached.</summary>
/// <param name="Name">The target name.</param>
/// <param name="Platform">The platform.</param>
/// <param name="Detected">What the preflight found.</param>
/// <param name="ApiVersion">The API version used.</param>
/// <param name="Auth">The sign-in method. Never the secret.</param>
/// <param name="Identity">Who Setpoint signed in as, when known.</param>
/// <param name="RepoCount">How many repos are in scope.</param>
public sealed record PlanTarget(string Name, Platform Platform, string Detected, string ApiVersion, AuthMethod Auth, string? Identity, int RepoCount);

/// <summary>One repo in a plan.</summary>
public sealed record PlanRepo
{
    /// <summary>The target name.</summary>
    public required string Target { get; init; }

    /// <summary><c>project-or-owner/repo</c>.</summary>
    public required string Path { get; init; }

    /// <summary>What plan concluded.</summary>
    public required PlanRepoStatus Status { get; init; }

    /// <summary>The layers that apply: <c>baseline</c>, group names, <c>repo</c>.</summary>
    public required IReadOnlyList<string> Layers { get; init; }

    /// <summary>The native objects apply writes, with what it does to each.</summary>
    public required IReadOnlyList<PlanObject> Objects { get; init; }

    /// <summary>The neutral view of the same changes. apply never reads it.</summary>
    public required IReadOnlyList<PlanChange> Changes { get; init; }

    /// <summary>The live settings Setpoint leaves alone.</summary>
    public required IReadOnlyList<PlanUnmanaged> Unmanaged { get; init; }

    /// <summary>How many live settings Setpoint leaves alone.</summary>
    public int UnmanagedCount => Unmanaged.Count;

    /// <summary>Warnings and notes about this repo.</summary>
    public required IReadOnlyList<PlanDiagnostic> Diagnostics { get; init; }
}

/// <summary>What plan concluded about a repo.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlanRepoStatus>))]
public enum PlanRepoStatus
{
    /// <summary>Apply would change it.</summary>
    [JsonStringEnumMemberName("changes")]
    Changes,

    /// <summary>Live state matches the policy.</summary>
    [JsonStringEnumMemberName("insync")]
    InSync,

    /// <summary>Not managed: archived, empty, a fork, or unsupported on the platform.</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,

    /// <summary>Reading it failed.</summary>
    [JsonStringEnumMemberName("error")]
    Error,
}

/// <summary>One native object apply writes.</summary>
/// <param name="Key">The object's key, such as <c>ado:policy/17</c>, or <c>ado:new/...</c> for one to create.</param>
/// <param name="Operation">What apply does to it.</param>
/// <param name="Fingerprint">SHA-256 over the normalised live object at plan time; for a create, over "nothing matches" its match key.</param>
/// <param name="MatchKey">What the object is matched on, so apply can check that nothing appeared before a create.</param>
/// <param name="Payload">The full payload apply sends as-is; absent for a delete.</param>
/// <param name="Title">What the object is, for people, such as <c>Minimum number of reviewers #17</c>.</param>
public sealed record PlanObject(
    string Key,
    ObjectOperation Operation,
    string Fingerprint,
    string MatchKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Payload = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null);

/// <summary>What apply does to a native object.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ObjectOperation>))]
public enum ObjectOperation
{
    /// <summary>Create it.</summary>
    [JsonStringEnumMemberName("create")]
    Create,

    /// <summary>Replace it with the payload.</summary>
    [JsonStringEnumMemberName("update")]
    Update,

    /// <summary>Delete it.</summary>
    [JsonStringEnumMemberName("delete")]
    Delete,
}

/// <summary>One neutral change, pointing at the object that carries it.</summary>
/// <param name="Rule">The rule or field, such as <c>minimumReviewers.count</c>.</param>
/// <param name="Action">Create, update or remove.</param>
/// <param name="From">The live value for people, or <c>(none)</c>.</param>
/// <param name="To">The new value for people, or <c>(removed)</c>.</param>
/// <param name="Layer">The layer the new value comes from.</param>
/// <param name="Locked">Whether the baseline locks the rule.</param>
/// <param name="Object">The key of the object in <see cref="PlanRepo.Objects"/>.</param>
public sealed record PlanChange(string Rule, ChangeAction Action, string From, string To, string Layer, bool Locked, string Object);

/// <summary>What a change does to a rule.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangeAction>))]
public enum ChangeAction
{
    /// <summary>The setting is added.</summary>
    [JsonStringEnumMemberName("create")]
    Create,

    /// <summary>The setting's value changes.</summary>
    [JsonStringEnumMemberName("update")]
    Update,

    /// <summary>The setting is removed.</summary>
    [JsonStringEnumMemberName("remove")]
    Remove,
}

/// <summary>A live setting Setpoint leaves alone.</summary>
/// <param name="Rule">The rule it bears on.</param>
/// <param name="Source">Where it lives.</param>
/// <param name="Value">Its value, for people.</param>
public sealed record PlanUnmanaged(string Rule, string Source, string Value);

/// <summary>A diagnostic in a plan.</summary>
/// <param name="Code">The SPxxxx code.</param>
/// <param name="Severity">Its severity after <c>--strict</c>.</param>
/// <param name="Message">The message.</param>
public sealed record PlanDiagnostic(string Code, DiagnosticSeverity Severity, string Message)
{
    /// <summary>Converts a diagnostic, keeping its location in the message.</summary>
    public static PlanDiagnostic From(Diagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Severity,
        diagnostic.Location is null ? diagnostic.Message : $"{diagnostic.Location}: {diagnostic.Message}");
}

/// <summary>Totals over a plan's repos.</summary>
/// <param name="ReposChanged">Repos apply would change.</param>
/// <param name="Create">Settings added.</param>
/// <param name="Update">Settings changed.</param>
/// <param name="Remove">Settings removed.</param>
/// <param name="InSync">Repos already in line.</param>
/// <param name="Skipped">Repos skipped.</param>
/// <param name="Errors">Repos whose read failed.</param>
/// <param name="Unmanaged">Live settings left alone, over every repo.</param>
/// <param name="Warnings">Warnings, over the run and every repo.</param>
public sealed record PlanSummary(int ReposChanged, int Create, int Update, int Remove, int InSync, int Skipped, int Errors, int Unmanaged, int Warnings);
