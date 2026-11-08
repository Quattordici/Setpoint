using Setpoint.Core.Planning;
using Setpoint.Core.Policy.Loading;
using Setpoint.Core.Policy.Model;
using Setpoint.Core.Tests.TestData;

namespace Setpoint.Core.Tests.TestSupport;

/// <summary>Loads a <see cref="PolicyRepoBuilder"/> repo for tests that start after loading.</summary>
internal static class PolicyLoading
{
    public static PolicyRepo Load(this PolicyRepoBuilder builder)
    {
        PolicyLoadResult result = new PolicyRepoLoader([FakeProviderBlockDefinition.Instance]).Load(builder.Build());
        result.Diagnostics.ShouldBeEmpty("the test policy repo should load cleanly");
        return result.Repo!;
    }

    /// <summary>Loads the repo as plan input, with its sources for the policy hash.</summary>
    public static PlanInput LoadInput(this PolicyRepoBuilder builder, string? commit = null)
    {
        PolicyLoadResult result = new PolicyRepoLoader([FakeProviderBlockDefinition.Instance]).Load(builder.Build());
        result.Diagnostics.ShouldBeEmpty("the test policy repo should load cleanly");
        return new PlanInput(result.Repo!, result.Sources, commit);
    }
}
