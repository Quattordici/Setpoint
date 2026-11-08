using Setpoint.Core.Policy.Model;
using Setpoint.Core.Providers;

namespace Setpoint.Core.Tests.Providers;

/// <summary>Tests for <see cref="TargetCredentials"/>: which environment variables hold a target's secrets.</summary>
public sealed class TargetCredentialsTests
{
    /// <summary>
    /// Given a target name,
    /// when its token variable is named,
    /// then it is SETPOINT_ with the name upper-cased and non-alphanumerics turned into underscores, then _TOKEN.
    /// </summary>
    [Theory(DisplayName = "👍 Names the token variable after the target")]
    [InlineData("github", "SETPOINT_GITHUB_TOKEN")]
    [InlineData("ado-onprem", "SETPOINT_ADO_ONPREM_TOKEN")]
    [InlineData("gh.eu2", "SETPOINT_GH_EU2_TOKEN")]
    public void TokenVariable_TargetName_FollowsConvention(string name, string expected)
    {
        // Arrange
        var target = new Target(name, new TargetDefinition { Platform = Platform.GitHub });

        // Act
        string variable = TargetCredentials.TokenVariable(target);

        // Assert
        variable.ShouldBe(expected);
    }

    /// <summary>
    /// Given a target with tokenEnv,
    /// when its token variable is named,
    /// then tokenEnv wins.
    /// </summary>
    [Fact(DisplayName = "🖐️ tokenEnv overrides the conventional name")]
    public void TokenVariable_TokenEnv_Wins()
    {
        // Arrange
        string variable = "GH_" + new Bogus.Randomizer().AlphaNumeric(8).ToUpperInvariant();
        var target = new Target("github", new TargetDefinition { Platform = Platform.GitHub, TokenEnv = variable });

        // Act
        string named = TargetCredentials.TokenVariable(target);

        // Assert
        named.ShouldBe(variable);
    }

    /// <summary>
    /// Given a target name,
    /// when its App key variable is named,
    /// then it is SETPOINT_&lt;TARGET&gt;_APP_KEY.
    /// </summary>
    [Fact(DisplayName = "👍 Names the App key variable after the target")]
    public void AppKeyVariable_TargetName_FollowsConvention()
    {
        // Arrange
        var target = new Target("github-apps", new TargetDefinition { Platform = Platform.GitHub, Auth = AuthMethod.App });

        // Act
        string variable = TargetCredentials.AppKeyVariable(target);

        // Assert
        variable.ShouldBe("SETPOINT_GITHUB_APPS_APP_KEY");
    }
}
