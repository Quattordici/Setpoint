namespace Setpoint.Core.Tests;

/// <summary>
/// Tests for <see cref="ExitCode"/>. The numeric values are a public contract that pipelines
/// branch on, so these tests pin each one.
/// </summary>
public sealed class ExitCodeTests
{
    /// <summary>
    /// Given each documented exit code,
    /// when it is converted to the process exit value,
    /// then it has the number the spec assigns to it.
    /// </summary>
    [Theory(DisplayName = "👍 Exit codes keep their documented numbers")]
    [InlineData(ExitCode.Success, 0)]
    [InlineData(ExitCode.RuntimeFailure, 1)]
    [InlineData(ExitCode.InvalidPolicyOrUsage, 2)]
    [InlineData(ExitCode.StalePlan, 3)]
    [InlineData(ExitCode.GuardTripped, 4)]
    [InlineData(ExitCode.ChangesFound, 5)]
    public void ExitCode_DocumentedValue_IsStable(ExitCode code, int expected)
    {
        // Arrange
        // The code and its expected number come from the theory data.

        // Act
        int actual = (int)code;

        // Assert
        actual.ShouldBe(expected);
    }

    /// <summary>
    /// Given the exit code enum,
    /// when its members are counted,
    /// then there are exactly six, so a new code can't be added without updating the docs and these tests.
    /// </summary>
    [Fact(DisplayName = "🖐️ Exit code set has exactly six members")]
    public void ExitCode_Members_AreExactlySix()
    {
        // Arrange
        ExitCode[] members = Enum.GetValues<ExitCode>();

        // Act
        int count = members.Length;

        // Assert
        count.ShouldBe(6);
    }
}
