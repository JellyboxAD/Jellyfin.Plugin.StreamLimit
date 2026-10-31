using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.StreamLimit.Api;
using Jellyfin.Plugin.StreamLimit.Configuration;
using Jellyfin.Plugin.StreamLimit.Gate;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class HousekeepingTests
{
    private static readonly Guid Alice = Guid.Parse("38a5a5bb-90ab-4590-837d-16bcb0b1c1f0");
    private static readonly Guid Bob = Guid.Parse("6c3f7c1e-2d4b-4a8f-9e01-abcdefabcdef");

    private readonly TestClock _clock = new();

    [Fact]
    public void PlaySessionRegistry_EntriesExpire_AndArePruned()
    {
        var registry = new PlaySessionRegistry(_clock);
        registry.Remember("never-stopped", "play-1");
        _clock.Advance(PlaySessionRegistry.Lifetime - TimeSpan.FromMinutes(1));
        registry.Remember("fresh", "play-2");
        Assert.Equal("play-1", registry.Get("never-stopped"));

        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(registry.Get("never-stopped"));
        Assert.Equal(1, registry.Prune());
        Assert.Equal(1, registry.Count);
        Assert.Equal("play-2", registry.Get("fresh"));
    }

    [Fact]
    public void EnforcedDeviceRegistry_PruneDropsExpired_AndClearUserOnlyThatUser()
    {
        var registry = new EnforcedDeviceRegistry(_clock);
        registry.Record(Alice, "old");
        _clock.Advance(EnforcedDeviceRegistry.Lifetime + TimeSpan.FromSeconds(1));
        registry.Record(Alice, "tv");
        registry.Record(Alice, "phone");
        registry.Record(Bob, "tv");

        // Record pruned "old" already; Prune keeps the fresh ones.
        registry.Prune();
        Assert.Equal(3, registry.Count);

        Assert.Equal(2, registry.ClearUser(Alice));
        Assert.False(registry.IsEnforced(Alice, "tv"));
        Assert.True(registry.IsEnforced(Bob, "tv"));

        _clock.Advance(EnforcedDeviceRegistry.Lifetime + TimeSpan.FromSeconds(1));
        registry.Prune();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void KeyedThrottle_PruneDropsOldKeysWithoutANewEvent()
    {
        var throttle = new KeyedThrottle(TimeSpan.FromSeconds(30), _clock);
        Assert.True(throttle.TryAcquire("a"));
        Assert.True(throttle.TryAcquire("b"));

        _clock.Advance(TimeSpan.FromMinutes(11));
        throttle.Prune();

        Assert.Equal(0, throttle.Count);
        Assert.True(throttle.TryAcquire("a"));
    }

    [Fact]
    public void LiveView_ListsOnlyWhatIsCounted_WithReasons()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var tv = Session(Alice, "tv", "Movie", now.AddSeconds(-4));
        var slots = new[]
        {
            // Playing per Jellyfin, with an idle open response too.
            new StreamSlotTracker.SlotSnapshot(Alice, "tv", new DateTimeOffset(now.AddSeconds(-30)), 2, 0, true),
            // External player streaming (bytes within the window).
            new StreamSlotTracker.SlotSnapshot(Alice, "vlc", new DateTimeOffset(now.AddSeconds(-2)), 1, 1, true),
            // HLS client between segments.
            new StreamSlotTracker.SlotSnapshot(Alice, "hls", new DateTimeOffset(now.AddSeconds(-10)), 0, 0, true),
            // Ghost: open responses only, no bytes for minutes.
            new StreamSlotTracker.SlotSnapshot(Alice, "ghost", new DateTimeOffset(now.AddMinutes(-20)), 258, 0, false),
            // Bob only has a ghost: not listed at all.
            new StreamSlotTracker.SlotSnapshot(Bob, "ghost", new DateTimeOffset(now.AddMinutes(-20)), 29, 0, false),
        };

        var result = ActiveStreamsBuilder.Build([tv], slots, now, _ => 3, _ => false, _ => "bob", (_, _) => false);

        var alice = Assert.Single(result);
        Assert.Equal(3, alice.Streams.Count);
        Assert.DoesNotContain(alice.Streams, s => s.DeviceId == "ghost");
        Assert.All(alice.Streams, s => Assert.True(s.Counted));

        var playing = alice.Streams.Single(s => s.DeviceId == "tv");
        Assert.Equal("playing", playing.Reason);
        Assert.Equal(4, playing.IdleSeconds);
        Assert.Equal(2, playing.OpenResponses);

        var streaming = alice.Streams.Single(s => s.DeviceId == "vlc");
        Assert.Equal("streaming", streaming.Reason);
        Assert.Equal(2, streaming.IdleSeconds);
        Assert.Equal(1, streaming.OpenResponses);

        var recent = alice.Streams.Single(s => s.DeviceId == "hls");
        Assert.Equal("recent", recent.Reason);
        Assert.Equal(10, recent.IdleSeconds);
        Assert.Equal(0, recent.OpenResponses);
    }

    [Fact]
    public void LiveView_EnforcedDevice_IsStopped_UnlessItsTrafficStillCounts()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var zombie = Session(Alice, "afinity", "Show", now);
        var stillStreaming = Session(Alice, "infuse", "Film", now);
        var slots = new[]
        {
            new StreamSlotTracker.SlotSnapshot(Alice, "afinity", new DateTimeOffset(now.AddMinutes(-5)), 3, 0, false),
            new StreamSlotTracker.SlotSnapshot(Alice, "infuse", new DateTimeOffset(now), 1, 1, true),
        };

        var result = ActiveStreamsBuilder.Build([zombie, stillStreaming], slots, now, _ => 1, _ => false, _ => null, (_, _) => true);

        var alice = Assert.Single(result);
        var stopped = alice.Streams.Single(s => s.DeviceId == "afinity");
        Assert.True(stopped.Enforced);
        Assert.False(stopped.Counted);
        Assert.Equal("stopped", stopped.Reason);
        Assert.Equal(3, stopped.OpenResponses);

        var streaming = alice.Streams.Single(s => s.DeviceId == "infuse");
        Assert.True(streaming.Enforced);
        Assert.True(streaming.Counted);
        Assert.Equal("streaming", streaming.Reason);
    }

    [Fact]
    public void OldConfigXml_GetsNotifyNativeAppsOn()
    {
        // A 1.1.0.2 config has no NotifyNativeApps element: the constructor default applies.
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <StreamLimiterConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <EnableWebMessage>true</EnableWebMessage>
              <ShowLimitPopup>false</ShowLimitPopup>
              <ConfigVersion>1</ConfigVersion>
              <UserStreamLimits>{"38a5a5bb90ab4590837d16bcb0b1c1f0":2}</UserStreamLimits>
              <MessageTitle>Stream Limit</MessageTitle>
              <MessageText>Active streams exceeded</MessageText>
              <DefaultMaxStreams>1</DefaultMaxStreams>
            </StreamLimiterConfiguration>
            """;

        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(new StringReader(xml))!;

        Assert.True(config.NotifyNativeApps);
        Assert.False(config.ShowLimitPopup);
        Assert.Equal(1, config.DefaultMaxStreams);
        Assert.False(ConfigurationMigration.Migrate(config));
        Assert.True(config.NotifyNativeApps);
    }

    [Fact]
    public void ConfigXml_KeepsNotifyNativeAppsOff()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { NotifyNativeApps = false });

        var config = (PluginConfiguration)serializer.Deserialize(new StringReader(writer.ToString()))!;

        Assert.False(config.NotifyNativeApps);
    }

    private static SessionInfo Session(Guid userId, string deviceId, string itemName, DateTime checkIn)
        => new(null!, NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            UserName = "alice",
            DeviceId = deviceId,
            DeviceName = deviceId,
            Client = "Jellyfin Web",
            NowPlayingItem = new BaseItemDto { Name = itemName },
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.DirectPlay },
            LastPlaybackCheckIn = checkIn,
        };
}
