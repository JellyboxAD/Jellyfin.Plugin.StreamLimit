using System;
using Jellyfin.Plugin.StreamLimit.Gate;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class SlotSnapshotTests
{
    private static readonly Guid User = Guid.Parse("38a5a5bb-90ab-4590-837d-16bcb0b1c1f0");
    private static readonly Guid OtherUser = Guid.Parse("6c3f7c1e-2d4b-4a8f-9e01-abcdefabcdef");

    private readonly TestClock _clock = new();

    [Fact]
    public void GetHeldDevices_UnionsSlotsAndPlayingDevices()
    {
        var tracker = new StreamSlotTracker(_clock, TimeSpan.FromSeconds(60));
        tracker.TryEnter(User, "tv", 3, []);
        tracker.TryEnter(OtherUser, "laptop", 3, []);

        var held = tracker.GetHeldDevices(User, ["phone", "TV"]);

        Assert.Equal(2, held.Count);
        Assert.Contains("phone", held);
        Assert.Contains("tv", held);
    }

    [Fact]
    public void GetHeldDevices_DropsExpiredSlots()
    {
        var tracker = new StreamSlotTracker(_clock, TimeSpan.FromSeconds(60));
        tracker.TryEnter(User, "tv", 3, []);
        _clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Empty(tracker.GetHeldDevices(User, []));
    }

    [Fact]
    public void GetSnapshot_ListsEveryUsersSlots()
    {
        var tracker = new StreamSlotTracker(_clock, TimeSpan.FromSeconds(60));
        tracker.TryEnter(User, "tv", 3, []);
        tracker.BeginRequest(User, "tv");
        tracker.TryEnter(OtherUser, "laptop", 3, []);

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, s => s.UserId == User && s.DeviceId == "tv" && s.InFlight == 1);
        Assert.Contains(snapshot, s => s.UserId == OtherUser && s.DeviceId == "laptop" && s.InFlight == 0);
    }

    [Fact]
    public void ReleaseDevice_ReportsWhetherASlotWasFreed()
    {
        var tracker = new StreamSlotTracker(_clock, TimeSpan.FromSeconds(60));
        tracker.TryEnter(User, "tv", 3, []);
        tracker.TryEnter(User, "download", 3, []);
        tracker.BeginRequest(User, "download");

        Assert.True(tracker.ReleaseDevice(User, "tv"));
        Assert.False(tracker.ReleaseDevice(User, "tv"));
        Assert.False(tracker.ReleaseDevice(User, "download"));
        Assert.False(tracker.ReleaseDevice(OtherUser, "tv"));
    }

    [Fact]
    public void PlaySessionRegistry_RemembersAndForgets()
    {
        var registry = new PlaySessionRegistry();
        registry.Remember("session-1", "play-1");
        registry.Remember("session-1", "play-2");
        registry.Remember("session-2", "play-3");
        registry.Remember(null, "ignored");

        Assert.Equal("play-2", registry.Get("session-1"));

        registry.Forget("session-1");
        registry.Remember("session-2", null);

        Assert.Null(registry.Get("session-1"));
        Assert.Null(registry.Get("session-2"));
        Assert.Null(registry.Get(null));
    }
}
