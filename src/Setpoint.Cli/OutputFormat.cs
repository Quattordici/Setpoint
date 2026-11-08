namespace Setpoint.Cli;

/// <summary>
/// How command output is laid out. When <c>--output</c> is not given, Setpoint uses
/// <see cref="Tree"/> on a terminal and <see cref="Plain"/> otherwise.
/// </summary>
internal enum OutputFormat
{
    /// <summary>The tree layout with colour, for people at a terminal.</summary>
    Tree,

    /// <summary>One line per change with no colour, for logs and pipelines.</summary>
    Plain,
}
