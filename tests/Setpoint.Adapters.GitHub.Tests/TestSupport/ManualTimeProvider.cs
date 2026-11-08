namespace Setpoint.Adapters.GitHub.Tests.TestSupport;

/// <summary>A clock the test moves by hand. Timers stay real, so only "now" is faked.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
