namespace AIUsage.Core.Tests;

/// <summary>
/// A minimal controllable <see cref="TimeProvider"/> for freshness/cadence tests — a hand-rolled
/// fake clock so the test project keeps ZERO third-party packages beyond xUnit + the test SDK
/// (no <c>Microsoft.Extensions.TimeProvider.Testing</c> dependency).
/// </summary>
/// <remarks>
/// It exposes BOTH a controllable wall clock (<see cref="GetUtcNow"/>) and a controllable MONOTONIC clock
/// (<see cref="GetTimestamp"/>). <see cref="Advance"/> moves both in lockstep — the normal "time passes"
/// case — while <see cref="JumpWallClock"/> moves ONLY the wall clock (forward or backward), simulating a
/// system clock adjustment so tests can prove the monotonic cadence floor is immune to it. The timestamp
/// frequency is <see cref="TimeSpan.TicksPerSecond"/>, so a monotonic delta equals the wall delta exactly.
/// </remarks>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    private long _timestamp; // monotonic, in TimestampFrequency (100 ns) units

    public FakeTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
        _timestamp = 0;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    /// <summary>Advance BOTH the wall clock and the monotonic clock — ordinary elapsed time.</summary>
    public void Advance(TimeSpan by)
    {
        _utcNow += by;
        _timestamp += by.Ticks; // by.Ticks are 100 ns units == TimestampFrequency units
    }

    /// <summary>
    /// Move ONLY the wall clock (the monotonic clock is untouched) — a simulated system-clock adjustment.
    /// A negative <paramref name="by"/> rewinds the wall clock; the monotonic floor must ignore it.
    /// </summary>
    public void JumpWallClock(TimeSpan by) => _utcNow += by;
}
