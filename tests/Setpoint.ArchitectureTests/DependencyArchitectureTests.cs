using ArchUnitNET.Fluent;
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Setpoint.ArchitectureTests;

/// <summary>
/// Dependency rules for the core/adapter split in the MVP spec: the core never knows about
/// a platform or the CLI, adapters never know about each other or the CLI, and only the CLI
/// touches the terminal and command-line libraries.
/// Rules whose subject is an adapter allow an empty match, because the adapters hold no types
/// until their build milestones (M3 and M4); drop <c>WithoutRequiringPositiveResults</c> then.
/// </summary>
[Trait("Category", "Architecture.Dependency")]
public sealed class DependencyArchitectureTests
{
    /// <summary>
    /// Given the core, when dependency checks run, then it depends on no adapter and not on the CLI.
    /// </summary>
    [Fact(DisplayName = "🏛️ Core does not depend on adapters or the CLI")]
    public void CoreShouldNotDependOnAdaptersOrCli()
    {
        // Arrange
        IArchRule rule = Types().That().Are(ArchitectureTestContext.CoreLayer)
            .Should()
            .NotDependOnAny(ArchitectureTestContext.Adapters)
            .AndShould()
            .NotDependOnAny(ArchitectureTestContext.CliLayer)
            .Because("the core owns the neutral model and must stay platform-neutral; adapters plug into it, not the reverse");

        // Act
        Exception? exception = Record.Exception(() => rule.Check(ArchitectureTestContext.Architecture));

        // Assert
        exception.ShouldBeNull();
    }

    /// <summary>
    /// Given the GitHub adapter, when dependency checks run, then it does not depend on the Azure DevOps adapter.
    /// </summary>
    [Fact(DisplayName = "🏛️ GitHub adapter does not depend on the Azure DevOps adapter")]
    public void GitHubAdapterShouldNotDependOnAzureDevOpsAdapter()
    {
        // Arrange
        IArchRule rule = Types().That().Are(ArchitectureTestContext.GitHubAdapter)
            .Should()
            .NotDependOnAny(ArchitectureTestContext.AzureDevOpsAdapter)
            .Because("adapters only translate between the neutral model and one platform, so shared logic belongs in the core")
            .WithoutRequiringPositiveResults();

        // Act
        Exception? exception = Record.Exception(() => rule.Check(ArchitectureTestContext.Architecture));

        // Assert
        exception.ShouldBeNull();
    }

    /// <summary>
    /// Given the Azure DevOps adapter, when dependency checks run, then it does not depend on the GitHub adapter.
    /// </summary>
    [Fact(DisplayName = "🏛️ Azure DevOps adapter does not depend on the GitHub adapter")]
    public void AzureDevOpsAdapterShouldNotDependOnGitHubAdapter()
    {
        // Arrange
        IArchRule rule = Types().That().Are(ArchitectureTestContext.AzureDevOpsAdapter)
            .Should()
            .NotDependOnAny(ArchitectureTestContext.GitHubAdapter)
            .Because("adapters only translate between the neutral model and one platform, so shared logic belongs in the core")
            .WithoutRequiringPositiveResults();

        // Act
        Exception? exception = Record.Exception(() => rule.Check(ArchitectureTestContext.Architecture));

        // Assert
        exception.ShouldBeNull();
    }

    /// <summary>
    /// Given both adapters, when dependency checks run, then neither depends on the CLI.
    /// </summary>
    [Fact(DisplayName = "🏛️ Adapters do not depend on the CLI")]
    public void AdaptersShouldNotDependOnCli()
    {
        // Arrange
        IArchRule rule = Types().That().Are(ArchitectureTestContext.Adapters)
            .Should()
            .NotDependOnAny(ArchitectureTestContext.CliLayer)
            .Because("the CLI hosts the adapters; adapters must stay usable from tests and any future host")
            .WithoutRequiringPositiveResults();

        // Act
        Exception? exception = Record.Exception(() => rule.Check(ArchitectureTestContext.Architecture));

        // Assert
        exception.ShouldBeNull();
    }

    /// <summary>
    /// Given the core and the adapters, when dependency checks run, then neither uses Spectre.Console or System.CommandLine.
    /// </summary>
    [Fact(DisplayName = "🏛️ Core and adapters do not use the terminal or command-line libraries")]
    public void CoreAndAdaptersShouldNotDependOnConsoleLibraries()
    {
        // Arrange
        IArchRule rule = Types().That().Are(ArchitectureTestContext.CoreAndAdapters)
            .Should()
            .NotDependOnAny(ArchitectureTestContext.ConsoleAndCommandLineTypes)
            .Because("rendering and argument parsing belong to the CLI, so the plan engine stays testable and reusable for the drift report");

        // Act
        Exception? exception = Record.Exception(() => rule.Check(ArchitectureTestContext.Architecture));

        // Assert
        exception.ShouldBeNull();
    }

    /// <summary>
    /// Given the core, when dependency checks run, then it does not depend on the HTTP core.
    /// </summary>
    [Fact(DisplayName = "🏛️ Core does not depend on the HTTP core")]
    public void CoreShouldNotDependOnHttp()
    {
        // Arrange
        IArchRule rule = Types().That().Are(ArchitectureTestContext.CoreLayer)
            .Should()
            .NotDependOnAny(ArchitectureTestContext.HttpLayer)
            .Because("the core plans offline from a neutral model; only adapters talk to platforms");

        // Act
        Exception? exception = Record.Exception(() => rule.Check(ArchitectureTestContext.Architecture));

        // Assert
        exception.ShouldBeNull();
    }

    /// <summary>
    /// Given the HTTP core, when dependency checks run, then it depends on no other Setpoint project.
    /// </summary>
    [Fact(DisplayName = "🏛️ HTTP core depends on no other Setpoint project")]
    public void HttpShouldNotDependOnOtherSetpointProjects()
    {
        // Arrange
        IArchRule rule = Types().That().Are(ArchitectureTestContext.HttpLayer)
            .Should()
            .NotDependOnAny(ArchitectureTestContext.CoreLayer)
            .AndShould()
            .NotDependOnAny(ArchitectureTestContext.Adapters)
            .AndShould()
            .NotDependOnAny(ArchitectureTestContext.CliLayer)
            .Because("the HTTP core is shared plumbing that both adapters build on, with no platform or policy knowledge");

        // Act
        Exception? exception = Record.Exception(() => rule.Check(ArchitectureTestContext.Architecture));

        // Assert
        exception.ShouldBeNull();
    }
}
