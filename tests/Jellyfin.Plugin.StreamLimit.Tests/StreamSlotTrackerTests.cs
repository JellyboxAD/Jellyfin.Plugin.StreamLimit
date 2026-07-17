using System;
using System.Collections.Generic;
using Jellyfin.Plugin.StreamLimit.Gate;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class StreamSlotTrackerTests
{
    private static readonly Guid User = Guid.Parse("38a5a5bb-90ab-4590-837d-16bcb0b1c1f0");
    private static readonly string[] NoSessions = Array.Empty<string>();

    private readonly FakeClock _clock = new();

    private StreamSlotTracker CreateTracker(int ttlSeconds = 60)
        => new(_clock, TimeSpan.FromSeconds(ttlSeconds));

    [Fact]
    public void TryEnter_UnderLimit_Grants()
    {
        var tracker = CreateTracker();

        Assert.True(tracker.TryEnter(User, "device-a", 2, NoSessions));
        Assert.True(tracker.TryEnter(User, "device-b", 2, NoSessions));
    }

    [Fact]
    public void TryEnter_AtLimit_DeniesNewDevice()
    {
        var tracker = CreateTracker();
        tracker.TryEnter(User, "device-a", 1, NoSessions);

        Assert.False(tracker.TryEnter(User, "device-b", 1, NoSessions));
    }

    [Fact]
    public void TryEnter_SameDevice_KeepsItsSlot()
    {
        var tracker = CreateTracker();
        tracker.TryEnter(User, "device-a", 1, NoSessions);

        // Quality switch / next episode on the same device must not be a new stream.
        Assert.True(tracker.TryEnter(User, "device-a", 1, NoSessions));
    }

    [Fact]
    public void TryEnter_DeviceAlreadyPlayingPerSessions_AllowedEvenAtLimit()
    {
        var tracker = CreateTracker();
        var playing = new[] { "device-a" };

        Assert.True(tracker.TryEnter(User, "device-a", 1, playing));
    }

    [Fact]
    public void TryEnter_SessionDeviceAndSlotDeviceAreNotDoubleCounted()
    {
        var tracker = CreateTracker();
        tracker.TryEnter(User, "device-a", 2, NoSessions);

        // device-a now also visible as a playing session: still one held slot.
        Assert.True(tracker.TryEnter(User, "device-b", 2, new[] { "device-a" }));
    }

    [Fact]
    public void TryEnter_CountsSessionDevicesUnknownToTracker()
    {
        var tracker = CreateTracker();

        Assert.False(tracker.TryEnter(User, "device-b", 1, new[] { "device-a" }));
    }

    [Fact]
    public void SlotExpires_AfterTtlWithoutTraffic()
    {
        var tracker = CreateTracker(ttlSeconds: 60);
        tracker.TryEnter(User, "device-a", 1, NoSessions);

        _clock.Advance(TimeSpan.FromSeconds(61));

        Assert.True(tracker.TryEnter(User, "device-b", 1, NoSessions));
    }

    [Fact]
    public void InFlightResponse_HoldsSlotPastTtl()
    {
        var tracker = CreateTracker(ttlSeconds: 60);
        tracker.TryEnter(User, "device-a", 1, NoSessions);
        tracker.BeginRequest(User, "device-a");

        // A direct-play download issues one request lasting the whole movie.
        _clock.Advance(TimeSpan.FromHours(2));

        Assert.False(tracker.TryEnter(User, "device-b", 1, NoSessions));

        tracker.EndRequest(User, "device-a");
        _clock.Advance(TimeSpan.FromSeconds(61));

        Assert.True(tracker.TryEnter(User, "device-b", 1, NoSessions));
    }

    [Fact]
    public void ReleaseDevice_FreesSlotImmediately()
    {
        var tracker = CreateTracker();
        tracker.TryEnter(User, "device-a", 1, NoSessions);

        tracker.ReleaseDevice(User, "device-a");

        Assert.True(tracker.TryEnter(User, "device-b", 1, NoSessions));
    }

    [Fact]
    public void ReleaseDevice_KeepsSlotWhileResponseInFlight()
    {
        var tracker = CreateTracker();
        tracker.TryEnter(User, "device-a", 1, NoSessions);
        tracker.BeginRequest(User, "device-a");

        tracker.ReleaseDevice(User, "device-a");

        Assert.False(tracker.TryEnter(User, "device-b", 1, NoSessions));
    }

    [Fact]
    public void WouldAllow_DoesNotClaimASlot()
    {
        var tracker = CreateTracker();

        Assert.True(tracker.WouldAllow(User, "device-a", 1, NoSessions));

        // Nothing was claimed: another device can still take the only slot.
        Assert.True(tracker.TryEnter(User, "device-b", 1, NoSessions));
        Assert.False(tracker.TryEnter(User, "device-a", 1, NoSessions));
        Assert.False(tracker.WouldAllow(User, "device-a", 1, NoSessions));
    }

    [Fact]
    public void BeginRequest_RecreatesSlotReleasedInTheGap()
    {
        // Regression: a playback-stop report racing between TryEnter and
        // BeginRequest must not orphan the in-flight hold.
        var tracker = CreateTracker(ttlSeconds: 60);
        Assert.True(tracker.TryEnter(User, "device-a", 1, NoSessions));
        tracker.ReleaseDevice(User, "device-a");

        tracker.BeginRequest(User, "device-a");
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.False(tracker.TryEnter(User, "device-b", 1, NoSessions));

        tracker.EndRequest(User, "device-a");
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(tracker.TryEnter(User, "device-b", 1, NoSessions));
    }

    [Fact]
    public void AbandonedSlot_IsEvictedWhenAnotherDeviceIsAboutToBeDenied()
    {
        // A lost stop report (closed tab) must not lock the user out for the full
        // TTL: idle slots with no session backing them are evicted on demand.
        var tracker = CreateTracker(ttlSeconds: 60);
        tracker.TryEnter(User, "device-a", 1, NoSessions);

        _clock.Advance(TimeSpan.FromSeconds(20));

        Assert.True(tracker.TryEnter(User, "device-b", 1, NoSessions));
    }

    [Fact]
    public void RecentSlot_IsNotEvictedByTheAbandonedSweep()
    {
        var tracker = CreateTracker(ttlSeconds: 60);
        tracker.TryEnter(User, "device-a", 1, NoSessions);

        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.False(tracker.TryEnter(User, "device-b", 1, NoSessions));
    }

    [Fact]
    public void SessionBackedSlot_IsNotEvictedByTheAbandonedSweep()
    {
        var tracker = CreateTracker(ttlSeconds: 60);
        tracker.TryEnter(User, "device-a", 1, NoSessions);

        _clock.Advance(TimeSpan.FromSeconds(20));

        Assert.False(tracker.TryEnter(User, "device-b", 1, new[] { "device-a" }));
    }

    [Fact]
    public void InFlightSlot_IsNotEvictedByTheAbandonedSweep()
    {
        var tracker = CreateTracker(ttlSeconds: 60);
        tracker.TryEnter(User, "device-a", 1, NoSessions);
        tracker.BeginRequest(User, "device-a");

        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.False(tracker.TryEnter(User, "device-b", 1, NoSessions));
    }

    [Fact]
    public void UsersAreIsolated()
    {
        var tracker = CreateTracker();
        var otherUser = Guid.Parse("6c3f7c1e-2d4b-4a8f-9e01-abcdefabcdef");
        tracker.TryEnter(User, "device-a", 1, NoSessions);

        Assert.True(tracker.TryEnter(otherUser, "device-b", 1, NoSessions));
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
