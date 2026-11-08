using Setpoint.Cli.Composition;
using Setpoint.Core.Policy.Schema;

namespace Setpoint.Cli.Tests.Policy;

/// <summary>
/// Keeps the committed JSON Schemas under <c>schema/v1/</c> in step with the model. Editors fetch the
/// committed files, so a model change without regenerating them would mislead people editing policy files.
/// Set <c>SETPOINT_UPDATE_SCHEMA=1</c> and run the tests to rewrite them.
/// </summary>
public sealed class PublishedSchemaTests
{
    public static TheoryData<string> SchemaFiles =>
        [PolicySchemaGenerator.TargetsSchemaFileName, PolicySchemaGenerator.LayerSchemaFileName, PolicySchemaGenerator.PlanSchemaFileName];

    /// <summary>
    /// Given the committed schema file,
    /// when the schema is generated from the current model and adapters,
    /// then the two are identical.
    /// </summary>
    [Theory(DisplayName = "👍 The committed schema matches the model")]
    [MemberData(nameof(SchemaFiles))]
    public void Schema_Committed_MatchesGenerated(string fileName)
    {
        // Arrange
        string committedPath = Path.Combine(RepoRoot(), "schema", "v1", fileName);
        string generated = PolicySchemaGenerator.Serialize(fileName switch
        {
            PolicySchemaGenerator.TargetsSchemaFileName => PolicySchemaGenerator.TargetsSchema(),
            PolicySchemaGenerator.PlanSchemaFileName => PolicySchemaGenerator.PlanSchema(),
            _ => PolicySchemaGenerator.LayerSchema(ProviderBlocks.All),
        });

        if (Environment.GetEnvironmentVariable("SETPOINT_UPDATE_SCHEMA") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(committedPath)!);
            File.WriteAllText(committedPath, generated);
        }

        // Act
        string committed = File.Exists(committedPath) ? File.ReadAllText(committedPath).ReplaceLineEndings("\n") : string.Empty;

        // Assert
        committed.ShouldBe(generated, $"schema/v1/{fileName} is out of date. Run the tests with SETPOINT_UPDATE_SCHEMA=1 to regenerate it.");
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

        throw new InvalidOperationException("Could not find the repo root (Setpoint.slnx) above the test output directory.");
    }
}
