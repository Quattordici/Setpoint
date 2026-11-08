namespace Setpoint.Core.Diagnostics;

/// <summary>A place in a policy file, as a path relative to the policy repo root and a 1-based line and column.</summary>
public sealed record SourceLocation(string File, int Line, int Column)
{
    /// <summary>The start of a file, for problems that belong to the file as a whole.</summary>
    public static SourceLocation StartOf(string file) => new(file, 1, 1);

    /// <inheritdoc />
    public override string ToString() => $"{File}:{Line}:{Column}";
}
