using Spectre.Console;

namespace Setpoint.Cli.Tests;

/// <summary>
/// Tests for <see cref="ConsoleFactory"/>, which makes sure output still renders
/// when it goes to a file or a pipeline log instead of a terminal.
/// </summary>
public sealed class ConsoleFactoryTests
{
    /// <summary>
    /// Given output redirected to a writer that is not a terminal,
    /// when a console is created and a line is written,
    /// then the console uses the fallback width and the line reaches the writer.
    /// </summary>
    [Fact(DisplayName = "🖐️ Renders to a redirected writer at the fallback width")]
    public void Create_RedirectedWriter_UsesFallbackWidthAndRenders()
    {
        // Arrange
        using var writer = new StringWriter();
        string line = new Bogus.Faker().Lorem.Sentence();

        // Act
        var console = ConsoleFactory.Create(writer);
        console.WriteLine(line);

        // Assert
        console.Profile.Width.ShouldBe(ConsoleFactory.RedirectedWidth);
        writer.ToString().ShouldContain(line);
    }
}
