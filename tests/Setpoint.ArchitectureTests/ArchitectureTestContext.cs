using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ReflectionAssembly = System.Reflection.Assembly;

namespace Setpoint.ArchitectureTests;

/// <summary>
/// The Setpoint assemblies loaded once for every architecture test, plus one provider per layer.
/// The core owns the neutral model and all the thinking; adapters only translate; the CLI hosts both.
/// </summary>
internal static class ArchitectureTestContext
{
    public const string CoreAssemblyName = "Setpoint.Core";
    public const string GitHubAdapterAssemblyName = "Setpoint.Adapters.GitHub";
    public const string AzureDevOpsAdapterAssemblyName = "Setpoint.Adapters.AzureDevOps";
    public const string CliAssemblyName = "setpoint";

    private static readonly ReflectionAssembly s_coreAssembly = ReflectionAssembly.Load(CoreAssemblyName);
    private static readonly ReflectionAssembly s_gitHubAdapterAssembly = ReflectionAssembly.Load(GitHubAdapterAssemblyName);
    private static readonly ReflectionAssembly s_azureDevOpsAdapterAssembly = ReflectionAssembly.Load(AzureDevOpsAdapterAssemblyName);
    private static readonly ReflectionAssembly s_cliAssembly = ReflectionAssembly.Load(CliAssemblyName);

    public static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(s_coreAssembly, s_gitHubAdapterAssembly, s_azureDevOpsAdapterAssembly, s_cliAssembly)
        .Build();

    public static readonly IObjectProvider<IType> CoreLayer =
        Types().That().ResideInAssembly(s_coreAssembly).As("Core");

    public static readonly IObjectProvider<IType> GitHubAdapter =
        Types().That().ResideInAssembly(s_gitHubAdapterAssembly).As("GitHub adapter");

    public static readonly IObjectProvider<IType> AzureDevOpsAdapter =
        Types().That().ResideInAssembly(s_azureDevOpsAdapterAssembly).As("Azure DevOps adapter");

    public static readonly IObjectProvider<IType> CliLayer =
        Types().That().ResideInAssembly(s_cliAssembly).As("CLI");

    public static readonly IObjectProvider<IType> Adapters =
        Types().That().Are(GitHubAdapter).Or().Are(AzureDevOpsAdapter).As("Adapters");

    public static readonly IObjectProvider<IType> CoreAndAdapters =
        Types().That().Are(CoreLayer).Or().Are(Adapters).As("Core and adapters");

    /// <summary>
    /// Types from the terminal and command-line libraries. These assemblies are not loaded into
    /// <see cref="Architecture"/>, so the provider includes referenced types too.
    /// </summary>
    public static readonly IObjectProvider<IType> ConsoleAndCommandLineTypes =
        Types(true).That().ResideInNamespaceMatching("Spectre.Console*")
            .Or().ResideInNamespaceMatching("System.CommandLine*")
            .As("Spectre.Console and System.CommandLine");
}
