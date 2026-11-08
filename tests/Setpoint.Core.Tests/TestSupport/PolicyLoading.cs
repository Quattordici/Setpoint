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
}
