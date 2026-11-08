using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Core.Policy.Loading;

/// <summary>
/// Checks that a layer file matches where it lives: the right <c>layer:</c> for its folder, only the keys
/// that kind of layer allows, and a group name or repo that matches the file path.
/// </summary>
internal static class LayerRules
{
    public static void Check(PolicyFile<PolicyLayer> file, LayerKind expected, List<Diagnostic> diagnostics)
    {
        PolicyLayer layer = file.Content;
        SourceMap source = file.Source;

        if (layer.Layer != expected)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.FileLayout,
                $"This file must say 'layer: {Name(expected)}' because of where it lives, but it says 'layer: {Name(layer.Layer)}'.",
                source.Find("$.layer")));
            return;
        }

        NotAllowed(layer.Locked is not null && expected != LayerKind.Baseline, "locked", "the baseline", source, diagnostics);
        NotAllowed(layer.Name is not null && expected != LayerKind.Group, "name", "groups", source, diagnostics);
        NotAllowed(layer.Selectors is not null && expected != LayerKind.Group, "selectors", "groups", source, diagnostics);
        NotAllowed(layer.Repo is not null && expected != LayerKind.Repo, "repo", "repo overrides", source, diagnostics);

        switch (expected)
        {
            case LayerKind.Group:
                string fileName = file.Path["groups/".Length..^PolicyRepoLoader.Extension.Length];
                if (layer.Name is null)
                {
                    diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidValue, $"A group needs 'name: {fileName}'.", SourceLocation.StartOf(file.Path)));
                }
                else if (layer.Name != fileName)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.FileLayout,
                        $"The group is named '{layer.Name}' but its file is groups/{fileName}{PolicyRepoLoader.Extension}. The two must match.",
                        source.Find("$.name")));
                }

                if (layer.Selectors is null)
                {
                    diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidValue, "A group needs 'selectors:', the repos or globs it applies to.", SourceLocation.StartOf(file.Path)));
                }

                break;

            case LayerKind.Repo:
                string repoPath = file.Path["repos/".Length..^PolicyRepoLoader.Extension.Length];
                if (layer.Repo is null)
                {
                    diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidValue, $"A repo override needs 'repo: {repoPath}'.", SourceLocation.StartOf(file.Path)));
                }
                else if (layer.Repo != repoPath)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.FileLayout,
                        $"The override is for '{layer.Repo}' but its file is repos/{repoPath}{PolicyRepoLoader.Extension}. The path must mirror the repo.",
                        source.Find("$.repo")));
                }

                break;
        }
    }

    private static void NotAllowed(bool present, string key, string allowedIn, SourceMap source, List<Diagnostic> diagnostics)
    {
        if (present)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.KeyNotAllowedInLayer,
                $"'{key}' is only allowed in {allowedIn}.",
                source.Find($"$.{key}")));
        }
    }

    private static string Name(LayerKind kind) => kind.ToString().ToLowerInvariant();
}
