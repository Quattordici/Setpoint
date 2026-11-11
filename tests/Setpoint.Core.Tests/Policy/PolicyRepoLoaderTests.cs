using Setpoint.Core.Diagnostics;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Tests.TestData;
using Setpoint.Core.Tests.TestSupport;

namespace Setpoint.Core.Tests.Policy;

/// <summary>
/// Tests for <see cref="PolicyRepoLoader"/>: loading the four kinds of policy file, the strict YAML rules
/// (version, unknown keys, tags, interpolation, anchors), value types, and file layout checks.
/// </summary>
public sealed class PolicyRepoLoaderTests
{
    private readonly PolicyRepoLoader _sut = new([FakeProviderBlockDefinition.Instance]);

    /// <summary>
    /// Given the smallest valid policy repo,
    /// when it is loaded,
    /// then it loads with no diagnostics and the baseline rule is set.
    /// </summary>
    [Fact(DisplayName = "👍 Loads a minimal policy repo")]
    public void Load_MinimalRepo_LoadsCleanly()
    {
        // Arrange
        var builder = new PolicyRepoBuilder();

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.ShouldBeEmpty();
        result.Repo.ShouldNotBeNull();
        result.Repo.Targets.Content.Targets[builder.TargetName].Platform.ShouldBe(Platform.GitHub);
        result.Repo.Baseline.Content.Rules.RequireCommentResolution.Value.Enabled.ShouldBeTrue();
    }

    /// <summary>
    /// Given a baseline that defines an anchor and reuses it with an alias and a merge key,
    /// when it is loaded,
    /// then the aliased and merged values are the anchored ones, and explicit keys win over merged ones.
    /// </summary>
    [Fact(DisplayName = "👍 Resolves anchors, aliases and merge keys")]
    public void Load_AnchorsAliasesAndMergeKeys_Resolve()
    {
        // Arrange
        var builder = new PolicyRepoBuilder()
            .WithTargets("""
                version: 1
                targets:
                  gh: &gh
                    platform: github
                    scope: [acme/*]
                  gh-mirror:
                    <<: *gh
                    scope: [acme-mirror/*]
                  gh-copy: *gh
                """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.ShouldBeEmpty();
        IReadOnlyDictionary<string, TargetDefinition> targets = result.Repo!.Targets.Content.Targets;
        targets["gh-copy"].Scope.ShouldBe(["acme/*"]);
        targets["gh-mirror"].Platform.ShouldBe(Platform.GitHub);
        targets["gh-mirror"].Scope.ShouldBe(["acme-mirror/*"]);
    }

    /// <summary>
    /// Given a layer that sets one rule to ~ and leaves another out,
    /// when it is loaded,
    /// then the first is unmanaged and the second is not declared.
    /// </summary>
    [Fact(DisplayName = "🖐️ Tells unmanaged (~) apart from not declared")]
    public void Load_TildeAndAbsentRules_AreDistinct()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            rules:
              requiredChecks: ~
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        NeutralRules rules = result.Repo!.Baseline.Content.Rules;
        rules.RequiredChecks.State.ShouldBe(DeclaredState.Unmanaged);
        rules.MinimumReviewers.State.ShouldBe(DeclaredState.NotDeclared);
    }

    /// <summary>
    /// Given a layer with no rules key and a minimumReviewers rule with no enabled key,
    /// when they are loaded,
    /// then the defaults apply: an empty rule set, and the rule enabled.
    /// </summary>
    [Fact(DisplayName = "🖐️ Applies defaults for keys that are left out")]
    public void Load_OmittedKeys_UseDefaults()
    {
        // Arrange
        var builder = new PolicyRepoBuilder()
            .WithBaseline("""
                version: 1
                layer: baseline
                rules:
                  minimumReviewers:
                    count: 1
                """)
            .WithGroup("empty", "version: 1\nlayer: group\nname: empty\nselectors: []");

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Repo!.Baseline.Content.Rules.MinimumReviewers.Value.Enabled.ShouldBeTrue();
        result.Repo.Groups.ShouldHaveSingleItem().Content.Rules.ShouldBe(new NeutralRules());
    }

    /// <summary>
    /// Given a boolean rule written as false and another written as { enabled: false },
    /// when they are loaded,
    /// then both mean disabled, and only the second is marked as written in object form.
    /// </summary>
    [Fact(DisplayName = "🖐️ Reads a boolean rule as a plain boolean or as { enabled }")]
    public void Load_BooleanRuleForms_BothDisable()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            rules:
              requireCommentResolution: false
              protectFromForcePushAndDelete:
                enabled: false
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        NeutralRules rules = result.Repo!.Baseline.Content.Rules;
        rules.RequireCommentResolution.Value.ShouldBe(new BooleanRule(false));
        rules.ProtectFromForcePushAndDelete.Value.ShouldBe(new BooleanRule(false) { WrittenAsObject = true });
    }

    /// <summary>
    /// Given a baseline with a provider block,
    /// when it is loaded,
    /// then the block is read with the provider's own model.
    /// </summary>
    [Fact(DisplayName = "👍 Loads a provider block with the provider's model")]
    public void Load_ProviderBlock_UsesProviderModel()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            fake:
              minimumReviewers:
                flag: true
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.ShouldBeEmpty();
        var block = result.Repo!.Baseline.Content.Providers["fake"].ShouldBeOfType<FakeProviderBlock>();
        block.MinimumReviewers.Value.Flag.ShouldBeTrue();
    }

    /// <summary>
    /// Given a full set of layer files in their right places,
    /// when they are loaded,
    /// then the group and the repo override are both returned.
    /// </summary>
    [Fact(DisplayName = "👍 Loads groups and repo overrides")]
    public void Load_GroupAndRepoOverride_AreReturned()
    {
        // Arrange
        var builder = new PolicyRepoBuilder();
        string repo = $"{builder.TargetName}/{builder.Owner}/api";
        builder
            .WithGroup("payments", $"""
                version: 1
                layer: group
                name: payments
                selectors: [{builder.TargetName}/{builder.Owner}/*]
                rules:
                  allowedMergeTypes: [squash]
                """)
            .WithRepoOverride(repo, $"""
                version: 1
                layer: repo
                repo: {repo}
                rules:
                  requiredChecks:
                    - name: ci
                """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.ShouldBeEmpty();
        result.Repo!.Groups.ShouldHaveSingleItem().Content.Rules.AllowedMergeTypes.Value.ShouldBe([MergeType.Squash]);
        result.Repo.RepoOverrides.ShouldHaveSingleItem().Content.Rules.RequiredChecks.Value.ShouldHaveSingleItem().Name.ShouldBe("ci");
    }

    /// <summary>
    /// Given a policy file with no version key,
    /// when it is loaded,
    /// then it fails with SP0103 naming that file.
    /// </summary>
    [Fact(DisplayName = "👎 Requires version: 1 in every file")]
    public void Load_MissingVersion_ReportsUnsupportedVersion()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            layer: baseline
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Repo.ShouldBeNull();
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.UnsupportedVersion);
        diagnostic.Location!.File.ShouldBe("baseline.yml");
    }

    /// <summary>
    /// Given a policy file that declares a version other than 1,
    /// when it is loaded,
    /// then it fails with SP0103 on the version line.
    /// </summary>
    [Theory(DisplayName = "👎 Rejects versions other than 1")]
    [InlineData("2")]
    [InlineData("\"1\"")]
    [InlineData("~")]
    public void Load_OtherVersion_ReportsUnsupportedVersion(string version)
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline($"""
            # comment
            version: {version}
            layer: baseline
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.UnsupportedVersion);
        diagnostic.Location.ShouldBe(new SourceLocation("baseline.yml", 2, 1));
    }

    /// <summary>
    /// Given a rule with a misspelt key,
    /// when it is loaded,
    /// then it fails with SP0102 naming the file, the line and the likely key.
    /// </summary>
    [Fact(DisplayName = "👎 Reports an unknown key with its file, line and a suggestion")]
    public void Load_UnknownKey_ReportsFileLineAndSuggestion()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            rules:
              minimumReviewers:
                cout: 2
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.UnknownKey);
        diagnostic.Location.ShouldBe(new SourceLocation("baseline.yml", 5, 5));
        diagnostic.Message.ShouldContain("Did you mean 'count'?");
    }

    /// <summary>
    /// Given a file with unknown keys at the top level, in rules and in a provider block,
    /// when it is loaded,
    /// then every one of them is reported, not only the first.
    /// </summary>
    [Fact(DisplayName = "👎 Reports every unknown key in a file")]
    public void Load_SeveralUnknownKeys_ReportsEachOne()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            colour: blue
            rules:
              requireSignedCommits: true
            fake:
              minimumReviewers:
                flagg: true
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.Select(d => (d.Code, d.Location!.Line)).ShouldBe(
            [(DiagnosticCodes.UnknownKey, 3), (DiagnosticCodes.UnknownKey, 5), (DiagnosticCodes.UnknownKey, 8)],
            ignoreOrder: true);
    }

    /// <summary>
    /// Given a misspelt provider block key at the top level,
    /// when it is loaded,
    /// then the suggestion names the provider key.
    /// </summary>
    [Fact(DisplayName = "👎 Suggests the provider key for a misspelt provider block")]
    public void Load_MisspeltProviderKey_SuggestsProvider()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            fak: {}
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("Did you mean 'fake'?");
    }

    /// <summary>
    /// Given a value with a custom YAML tag,
    /// when it is loaded,
    /// then it fails with SP0104 on that line.
    /// </summary>
    [Fact(DisplayName = "👎 Rejects custom YAML tags")]
    public void Load_CustomTag_ReportsCustomTag()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            rules: !include rules.yml
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.CustomTag);
        diagnostic.Location!.Line.ShouldBe(3);
    }

    /// <summary>
    /// Given a standard YAML tag such as !!str,
    /// when it is loaded,
    /// then it is accepted.
    /// </summary>
    [Fact(DisplayName = "🖐️ Accepts standard !! tags")]
    public void Load_StandardTag_IsAccepted()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: !!str baseline
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a value written as shell, GitHub Actions or Azure Pipelines variable interpolation,
    /// when it is loaded,
    /// then it fails with SP0105, because Setpoint never expands variables.
    /// </summary>
    [Theory(DisplayName = "👎 Rejects environment and pipeline variable interpolation")]
    [InlineData("${GITHUB_TOKEN}")]
    [InlineData("${{ secrets.TOKEN }}")]
    [InlineData("$(System.AccessToken)")]
    [InlineData("prefix-${ENV}-suffix")]
    public void Load_Interpolation_ReportsInterpolation(string value)
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithTargets($"""
            version: 1
            targets:
              gh:
                platform: github
                tokenEnv: "{value}"
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.Interpolation);
        diagnostic.Location.ShouldBe(new SourceLocation("targets.yml", 5, 15));
    }

    /// <summary>
    /// Given a file that is not valid YAML,
    /// when it is loaded,
    /// then it fails with SP0101 at the broken line.
    /// </summary>
    [Fact(DisplayName = "👎 Reports invalid YAML at its line")]
    public void Load_InvalidYaml_ReportsInvalidYaml()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            rules:
              allowedMergeTypes: [squash
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.InvalidYaml);
        diagnostic.Location!.File.ShouldBe("baseline.yml");
    }

    /// <summary>
    /// Given a map with the same key twice,
    /// when it is loaded,
    /// then it fails with SP0101 instead of silently keeping one value.
    /// </summary>
    [Fact(DisplayName = "👎 Rejects duplicate keys")]
    public void Load_DuplicateKey_ReportsInvalidYaml()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            rules:
              requireCommentResolution: true
              requireCommentResolution: false
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.InvalidYaml);
    }

    /// <summary>
    /// Given a file with two YAML documents,
    /// when it is loaded,
    /// then it fails with SP0101.
    /// </summary>
    [Fact(DisplayName = "👎 Rejects more than one YAML document per file")]
    public void Load_TwoDocuments_ReportsInvalidYaml()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline("""
            version: 1
            layer: baseline
            ---
            version: 1
            layer: baseline
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.InvalidYaml);
    }

    /// <summary>
    /// Given a value of the wrong type,
    /// when it is loaded,
    /// then it fails with SP0106 at that value's line, in words a policy author understands.
    /// </summary>
    [Theory(DisplayName = "👎 Reports a value of the wrong type at its line")]
    [InlineData("minimumReviewers:\n    count: two", "a whole number")]
    [InlineData("allowedMergeTypes: [fastForward]", "one of merge, squash, rebase")]
    [InlineData("allowedMergeTypes: [1]", "one of merge, squash, rebase")]
    [InlineData("access:\n    platform: owner", "one of none, read, write, admin")]
    [InlineData("environments:\n    production: { branches: protected }", "one of default, any")]
    [InlineData("environments:\n    production: { approvers: [platform] }", "Unknown key")]
    [InlineData("requireCommentResolution: yes", "true, false or { enabled: true|false }")]
    [InlineData("requiredChecks:\n    - title: ci", "Unknown key")]
    public void Load_WrongValueType_ReportsInvalidValue(string rule, string expectedText)
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithBaseline($"""
            version: 1
            layer: baseline
            rules:
              {rule}
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Location!.Line.ShouldBeGreaterThanOrEqualTo(4, diagnostic.ToString());
        diagnostic.Message.ShouldContain(expectedText);
    }

    /// <summary>
    /// Given a required key that is missing,
    /// when it is loaded,
    /// then it fails with SP0106 naming the missing key.
    /// </summary>
    [Fact(DisplayName = "👎 Names a missing required key")]
    public void Load_MissingRequiredKey_NamesIt()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithTargets("""
            version: 1
            targets:
              gh:
                scope: [acme/*]
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.InvalidValue);
        diagnostic.Message.ShouldContain("platform");
    }

    /// <summary>
    /// Given a key that exists but belongs to another kind of layer,
    /// when it is loaded,
    /// then it fails with SP0109 at that key.
    /// </summary>
    [Fact(DisplayName = "👎 Rejects locked outside the baseline")]
    public void Load_LockedInGroup_ReportsKeyNotAllowed()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithGroup("payments", """
            version: 1
            layer: group
            name: payments
            selectors: [gh/acme/*]
            locked: [minimumReviewers]
            """);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.KeyNotAllowedInLayer);
        diagnostic.Location.ShouldBe(new SourceLocation("groups/payments.yml", 5, 1));
    }

    /// <summary>
    /// Given layer files whose contents don't match where they live,
    /// when they are loaded,
    /// then each fails with SP0108.
    /// </summary>
    [Theory(DisplayName = "👎 Rejects a layer file that doesn't match its path")]
    [InlineData("groups/payments.yml", "version: 1\nlayer: group\nname: billing\nselectors: [gh/acme/*]")]
    [InlineData("repos/gh/acme/api.yml", "version: 1\nlayer: repo\nrepo: gh/acme/web")]
    [InlineData("groups/payments.yml", "version: 1\nlayer: baseline")]
    [InlineData("groups/team/payments.yml", "version: 1\nlayer: group\nname: payments\nselectors: [gh/acme/*]")]
    [InlineData("repos/gh/api.yml", "version: 1\nlayer: repo\nrepo: gh/api")]
    [InlineData("groups/payments.yaml", "version: 1\nlayer: group\nname: payments\nselectors: [gh/acme/*]")]
    public void Load_LayerFileOutOfPlace_ReportsFileLayout(string path, string yaml)
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithFile(path, yaml);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.FileLayout);
        diagnostic.Location!.File.ShouldBe(path);
    }

    /// <summary>
    /// Given a policy repo without targets.yml or baseline.yml,
    /// when it is loaded,
    /// then it fails with SP0108 for the missing file.
    /// </summary>
    [Theory(DisplayName = "👎 Requires targets.yml and baseline.yml")]
    [InlineData("targets.yml")]
    [InlineData("baseline.yml")]
    public void Load_RequiredFileMissing_ReportsFileLayout(string missing)
    {
        // Arrange
        var builder = new PolicyRepoBuilder().Without(missing);

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCodes.FileLayout);
        diagnostic.Message.ShouldContain(missing);
    }

    /// <summary>
    /// Given other files in the policy repo, such as a pipeline definition at the root,
    /// when it is loaded,
    /// then they are ignored.
    /// </summary>
    [Fact(DisplayName = "🖐️ Ignores files that are not policy files")]
    public void Load_OtherFiles_AreIgnored()
    {
        // Arrange
        var builder = new PolicyRepoBuilder().WithFile("azure-pipelines.yml", "trigger: [main]\nsteps: []");

        // Act
        PolicyLoadResult result = _sut.Load(builder.Build());

        // Assert
        result.Diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// Given a policy repo on disk,
    /// when it is loaded from its directory,
    /// then the paths are relative with forward slashes and the files load.
    /// </summary>
    [Fact(DisplayName = "👍 Loads a policy repo from a directory")]
    public void LoadDirectory_RepoOnDisk_Loads()
    {
        // Arrange
        string root = Directory.CreateTempSubdirectory("setpoint-").FullName;
        try
        {
            foreach (PolicySourceFile file in new PolicyRepoBuilder()
                .WithGroup("web", "version: 1\nlayer: group\nname: web\nselectors: [gh/acme/*]")
                .Build())
            {
                string path = Path.Combine(root, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Text);
            }

            // Act
            PolicyLoadResult result = _sut.LoadDirectory(root);

            // Assert
            result.Diagnostics.ShouldBeEmpty();
            result.Repo!.Groups.ShouldHaveSingleItem().Path.ShouldBe("groups/web.yml");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Given a directory that does not exist,
    /// when it is loaded,
    /// then it fails with SP0108.
    /// </summary>
    [Fact(DisplayName = "👎 Reports a missing policy repo directory")]
    public void LoadDirectory_MissingDirectory_ReportsFileLayout()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        // Act
        PolicyLoadResult result = _sut.LoadDirectory(root);

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.FileLayout);
    }
}
