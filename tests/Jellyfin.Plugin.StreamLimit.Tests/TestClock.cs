using System;

namespace Jellyfin.Plugin.StreamLimit.Tests;

/// <summary>
/// Manually advanced time source for the time-dependent helpers.
/// </summary>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
