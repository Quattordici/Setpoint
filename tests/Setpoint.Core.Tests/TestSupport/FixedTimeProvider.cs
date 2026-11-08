namespace Setpoint.Testing;

/// <summary>A clock that always says the same time, so plan files are reproducible in tests.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
