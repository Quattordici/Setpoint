using System.CommandLine;

namespace Setpoint.Cli;

/// <summary>
/// Options shared by more than one command. Each command adds the ones that apply to it,
/// so help only lists flags that do something for that command.
/// </summary>
internal static class CommonOptions
{
    public static Option<DirectoryInfo> Policy() => new("--policy", "-p")
    {
        Description = "The policy repo directory, holding targets.yml and baseline.yml.",
        HelpName = "dir",
        DefaultValueFactory = _ => new DirectoryInfo("."),
    };

    public static Option<bool> Strict() => new("--strict")
    {
        Description = "Treat every warning as an error.",
    };

    public static Option<bool> Verbose() => new("--verbose", "-v")
    {
        Description = "Show in-sync repos and list unmanaged settings.",
    };

    public static Option<OutputFormat?> Output() => new("--output", "-o")
    {
        Description = "Output layout. Defaults to tree on a terminal and plain otherwise.",
        HelpName = "tree|plain",
    };

    public static Option<bool> SkipUnreachable() => new("--skip-unreachable")
    {
        Description = "Leave out targets that can't be reached, with a warning, instead of failing.",
    };

    public static Option<bool> AllowUnsupportedServer() => new("--allow-unsupported-server")
    {
        Description = "Run against Azure DevOps Server versions older than 2022.",
    };

    public static Option<int> MaxChanges() => new("--max-changes")
    {
        Description = "Refuse to apply when more than N repos would change. 0 removes the limit.",
        HelpName = "N",
        DefaultValueFactory = _ => 25,
    };

    public static Option<FileInfo?> Summary() => new("--summary")
    {
        Description = "Append a Markdown summary to this file, such as $GITHUB_STEP_SUMMARY, for the pipeline's run page.",
        HelpName = "file",
    };

    public static Option<bool> FailFast() => new("--fail-fast")
    {
        Description = "Stop the whole run at the first failed write, instead of only that repo.",
    };

    /// <summary>
    /// Dev-only and hidden from help: save every HTTP exchange, redacted, as WireMock.Net mappings for tests.
    /// </summary>
    public static Option<DirectoryInfo?> Record() => new("--record")
    {
        Description = "Dev only: save redacted HTTP request/response pairs to this directory as WireMock mappings.",
        HelpName = "dir",
        Hidden = true,
    };
}
