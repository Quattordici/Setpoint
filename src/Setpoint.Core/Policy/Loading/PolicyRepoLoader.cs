using System.Text;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Policy.Loading;

/// <summary>One file read from a policy repo: its path relative to the root, with forward slashes, and its text.</summary>
public sealed record PolicySourceFile(string Path, string Text);

/// <summary>The outcome of loading a policy repo.</summary>
/// <param name="Repo">The loaded files, or null when any file has an error.</param>
/// <param name="Diagnostics">Everything found, in file order.</param>
public sealed record PolicyLoadResult(PolicyRepo? Repo, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The files that were read, ordered by path. The plan's policy hash is taken over them.</summary>
    public IReadOnlyList<PolicySourceFile> Sources { get; init; } = [];

    /// <summary>True when any diagnostic is an error.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Loads a policy repo: <c>targets.yml</c>, <c>baseline.yml</c>, <c>groups/&lt;name&gt;.yml</c> and
/// <c>repos/&lt;target&gt;/&lt;project-or-owner&gt;/&lt;repo&gt;.yml</c>. Every file is parsed strictly and checked
/// against where it lives. Merging the layers is a later step.
/// </summary>
/// <param name="providers">The provider blocks the adapters define, such as <c>github:</c>.</param>
public sealed class PolicyRepoLoader(IReadOnlyList<IProviderBlockDefinition> providers)
{
    public const string TargetsFileName = "targets.yml";
    public const string BaselineFileName = "baseline.yml";
    public const string GroupsDirectory = "groups";
    public const string ReposDirectory = "repos";
    public const string Extension = ".yml";

    /// <summary>Reads the policy repo at <paramref name="rootDirectory"/> from disk and loads it.</summary>
    public PolicyLoadResult LoadDirectory(string rootDirectory)
    {
        var root = new DirectoryInfo(rootDirectory);
        if (!root.Exists)
        {
            return new PolicyLoadResult(null, [Diagnostic.Error(DiagnosticCodes.FileLayout, $"The policy repo directory '{rootDirectory}' does not exist.")]);
        }

        var files = new List<PolicySourceFile>();
        foreach (string name in new[] { "targets.yml", "targets.yaml", "baseline.yml", "baseline.yaml" })
        {
            AddIfExists(root, name, files);
        }

        foreach (string directory in new[] { GroupsDirectory, ReposDirectory })
        {
            var sub = new DirectoryInfo(Path.Combine(root.FullName, directory));
            if (!sub.Exists)
            {
                continue;
            }

            foreach (FileInfo file in sub.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Extension is ".yml" or ".yaml")
                {
                    files.Add(new PolicySourceFile(RelativePath(root, file), File.ReadAllText(file.FullName, Encoding.UTF8)));
                }
            }
        }

        return Load(files);
    }

    /// <summary>Loads a policy repo from files already in memory.</summary>
    public PolicyLoadResult Load(IEnumerable<PolicySourceFile> files)
    {
        IReadOnlyList<PolicySourceFile> sources = [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
        var diagnostics = new List<Diagnostic>();
        PolicyFile<TargetsFile>? targets = null;
        PolicyFile<PolicyLayer>? baseline = null;
        var groups = new List<PolicyFile<PolicyLayer>>();
        var repoOverrides = new List<PolicyFile<PolicyLayer>>();

        foreach (PolicySourceFile file in sources)
        {
            string[] segments = file.Path.Split('/');

            if (file.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.FileLayout,
                    $"Policy files use the {Extension} extension. Rename this file to '{file.Path[..^5]}{Extension}'.",
                    SourceLocation.StartOf(file.Path)));
                continue;
            }

            switch (segments)
            {
                case [TargetsFileName]:
                    targets = PolicyFileParser.ParseTargets(file.Path, file.Text, diagnostics);
                    break;

                case [BaselineFileName]:
                    baseline = ParseLayer(file, LayerKind.Baseline, diagnostics);
                    break;

                case [GroupsDirectory, _]:
                    AddIfParsed(groups, ParseLayer(file, LayerKind.Group, diagnostics));
                    break;

                case [ReposDirectory, _, _, _]:
                    AddIfParsed(repoOverrides, ParseLayer(file, LayerKind.Repo, diagnostics));
                    break;

                case [GroupsDirectory, ..]:
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.FileLayout,
                        "Group files sit directly in groups/, as groups/<name>.yml.",
                        SourceLocation.StartOf(file.Path)));
                    break;

                case [ReposDirectory, ..]:
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.FileLayout,
                        "Repo overrides live at repos/<target>/<project-or-owner>/<repo>.yml.",
                        SourceLocation.StartOf(file.Path)));
                    break;

                default:
                    // Other files, such as a pipeline definition at the root, are not policy files.
                    break;
            }
        }

        if (!files.Any(f => f.Path == TargetsFileName))
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.FileLayout, $"The policy repo has no {TargetsFileName}."));
        }

        if (!files.Any(f => f.Path == BaselineFileName))
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.FileLayout, $"The policy repo has no {BaselineFileName}."));
        }

        bool hasErrors = diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        PolicyRepo? repo = hasErrors || targets is null || baseline is null
            ? null
            : new PolicyRepo { Targets = targets, Baseline = baseline, Groups = groups, RepoOverrides = repoOverrides };
        return new PolicyLoadResult(repo, diagnostics) { Sources = sources };
    }

    private PolicyFile<PolicyLayer>? ParseLayer(PolicySourceFile file, LayerKind expected, List<Diagnostic> diagnostics)
    {
        PolicyFile<PolicyLayer>? parsed = PolicyFileParser.ParseLayer(file.Path, file.Text, providers, diagnostics);
        if (parsed is null)
        {
            return null;
        }

        int errorsBefore = diagnostics.Count;
        LayerRules.Check(parsed, expected, diagnostics);
        return diagnostics.Count > errorsBefore ? null : parsed;
    }

    private static void AddIfParsed(List<PolicyFile<PolicyLayer>> list, PolicyFile<PolicyLayer>? file)
    {
        if (file is not null)
        {
            list.Add(file);
        }
    }

    private static void AddIfExists(DirectoryInfo root, string name, List<PolicySourceFile> files)
    {
        string path = Path.Combine(root.FullName, name);
        if (File.Exists(path))
        {
            files.Add(new PolicySourceFile(name, File.ReadAllText(path, Encoding.UTF8)));
        }
    }

    private static string RelativePath(DirectoryInfo root, FileInfo file) =>
        Path.GetRelativePath(root.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
}
