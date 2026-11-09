using System.Reflection;
using System.Text.RegularExpressions;
using Setpoint.Core.Diagnostics;

namespace Setpoint.Core.Tests.Diagnostics;

/// <summary>
/// Tests for <see cref="DiagnosticCatalogue"/> and <see cref="DiagnosticCodes"/>: every code is catalogued once,
/// sits in a documented range, and appears in docs/diagnostics.md with the same default severity.
/// </summary>
public sealed partial class DiagnosticCatalogueTests
{
    private static readonly string[] s_codes = [.. typeof(DiagnosticCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)];

    /// <summary>
    /// Given every code constant,
    /// when the catalogue is checked,
    /// then each code is in it exactly once, and it has nothing else.
    /// </summary>
    [Fact(DisplayName = "👍 Every code is catalogued exactly once")]
    public void Catalogue_Codes_MatchConstantsOneToOne()
    {
        // Arrange
        string[] catalogued = [.. DiagnosticCatalogue.All.Select(d => d.Code)];

        // Act
        string[] duplicates = [.. catalogued.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key)];

        // Assert
        duplicates.ShouldBeEmpty();
        catalogued.ShouldBe(s_codes, ignoreOrder: true);
    }

    /// <summary>
    /// Given every code,
    /// when its format is checked,
    /// then it is SP plus four digits in one of the documented ranges.
    /// </summary>
    [Fact(DisplayName = "👍 Codes follow SPxxxx in a documented range")]
    public void Codes_Format_FollowRanges()
    {
        // Arrange
        string[] ranges = ["01", "02", "10", "21", "22", "30", "40"];

        // Act
        string[] wrong = [.. s_codes.Where(c => !CodePattern().IsMatch(c) || !ranges.Contains(c[2..4]))];

        // Assert
        wrong.ShouldBeEmpty();
    }

    /// <summary>
    /// Given docs/diagnostics.md,
    /// when it is compared with the catalogue,
    /// then it lists every code with the same default severity.
    /// </summary>
    [Fact(DisplayName = "👍 docs/diagnostics.md lists every code with its severity")]
    public void Docs_Table_MatchesCatalogue()
    {
        // Arrange
        string docs = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "diagnostics.md"));

        // Act
        Dictionary<string, string> documented = DocsRow().Matches(docs).ToDictionary(m => m.Groups["code"].Value, m => m.Groups["severity"].Value);

        // Assert
        documented.Keys.ShouldBe(s_codes, ignoreOrder: true);
        foreach (DiagnosticDescriptor descriptor in DiagnosticCatalogue.All)
        {
            documented[descriptor.Code].ShouldBe(descriptor.DefaultSeverity.ToString().ToLowerInvariant(), customMessage: descriptor.Code);
        }
    }

    /// <summary>
    /// Given docs/diagnostics.md,
    /// when its exit code table is compared with the enum,
    /// then it documents every exit code exactly once, with what to do about it.
    /// </summary>
    [Fact(DisplayName = "👍 docs/diagnostics.md lists every exit code")]
    public void Docs_ExitCodes_MatchEnum()
    {
        // Arrange
        string docs = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "diagnostics.md"));
        int[] expected = [.. Enum.GetValues<ExitCode>().Select(c => (int)c)];

        // Act
        int[] documented = [.. ExitCodeRow().Matches(docs).Select(m => int.Parse(m.Groups["code"].Value, System.Globalization.CultureInfo.InvariantCulture))];

        // Assert
        documented.ShouldBe(expected, ignoreOrder: true);
    }

    /// <summary>
    /// Given a mix of warnings, errors and info lines,
    /// when --strict is applied,
    /// then warnings become errors and info lines stay as they are.
    /// </summary>
    [Fact(DisplayName = "🖐️ --strict turns warnings into errors")]
    public void ApplyStrict_Warnings_BecomeErrors()
    {
        // Arrange
        Diagnostic[] diagnostics =
        [
            Diagnostic.Warning(DiagnosticCodes.SelectorMatchesNothing, "w"),
            Diagnostic.Error(DiagnosticCodes.UnknownKey, "e"),
            new(DiagnosticCodes.PrivateRepoOnGitHubFree, DiagnosticSeverity.Info, "i"),
        ];

        // Act
        IReadOnlyList<Diagnostic> strict = DiagnosticList.ApplyStrict(diagnostics, strict: true);

        // Assert
        strict.Select(d => d.Severity).ShouldBe([DiagnosticSeverity.Error, DiagnosticSeverity.Error, DiagnosticSeverity.Info]);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Setpoint.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repo root above the test output directory.");
    }

    [GeneratedRegex(@"^SP[0-9]{4}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^\| (?<code>SP[0-9]{4}) \| (?<severity>[a-z]+) \|", RegexOptions.Multiline)]
    private static partial Regex DocsRow();

    [GeneratedRegex(@"^\| (?<code>[0-9]) \| [^|]+ \| [^|]+ \|\r?$", RegexOptions.Multiline)]
    private static partial Regex ExitCodeRow();
}
