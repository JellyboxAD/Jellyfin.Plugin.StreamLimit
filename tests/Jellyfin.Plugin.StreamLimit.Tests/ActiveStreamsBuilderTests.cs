using System;
using System.Linq;
using Jellyfin.Plugin.StreamLimit.Api;
using Jellyfin.Plugin.StreamLimit.Gate;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class ActiveStreamsBuilderTests
{
    private static readonly Guid Alice = Guid.Parse("38a5a5bb-90ab-4590-837d-16bcb0b1c1f0");
    private static readonly Guid Bob = Guid.Parse("6c3f7c1e-2d4b-4a8f-9e01-abcdefabcdef");
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void Build_ListsEachPlaybackOfOneDevice(bool countPlaybacks, int expectedCounted)
    {
        var browser = Session(Alice, "browser", "Chrome", "Movie A", PlayMethod.Transcode);
        var slots = new[] { new StreamSlotTracker.SlotSnapshot(Alice, "browser", new DateTimeOffset(Now), 2, 2) };
        var playbacks = new[]
        {
            new PlaybackSnapshot(Alice, "BROWSER", "tab-a", "item-a", true, new DateTimeOffset(Now.AddMinutes(-5)), new DateTimeOffset(Now), null),
            new PlaybackSnapshot(Alice, "browser", "tab-b", "item-b", true, new DateTimeOffset(Now.AddSeconds(-30)), new DateTimeOffset(Now), null),
            new PlaybackSnapshot(Alice, "browser", "tab-c", "item-c", true, new DateTimeOffset(Now.AddSeconds(-10)), new DateTimeOffset(Now), PlaybackDenialReason.TranscodeLimit),
            new PlaybackSnapshot(Bob, "browser", "other-user", "item-x", true, new DateTimeOffset(Now), new DateTimeOffset(Now), null),
        };

        var result = ActiveStreamsBuilder.Build(
            [browser],
            slots,
            Now,
            _ => 2,
            _ => false,
            _ => "unused",
            (_, _) => false,
            playbacks,
            id => "Name of " + id,
            p => p.PlaySessionId == "tab-a",
            countPlaybacks);

        var stream = Assert.Single(Assert.Single(result).Streams);
        Assert.Equal(new[] { "tab-a", "tab-b", "tab-c" }, stream.Playbacks.Select(p => p.PlaySessionId).ToArray());
        Assert.Equal("Name of item-b", stream.Playbacks[1].ItemName);
        Assert.True(stream.Playbacks[0].Transcoding);
        Assert.False(stream.Playbacks[1].Transcoding);
        Assert.Equal("transcode-limit", stream.Playbacks[2].Refused);
        Assert.Equal(expectedCounted, stream.CountedStreams);
    }

    [Fact]
    public void Build_MergesSessionsAndSlotsPerDevice()
    {
        var tv = Session(Alice, "tv", "Living room TV", "Movie", PlayMethod.Transcode);
        var phoneIdle = Session(Alice, "ext", "Phone", null, null);
        var slots = new[]
        {
            new StreamSlotTracker.SlotSnapshot(Alice, "TV", new DateTimeOffset(Now.AddSeconds(5)), 1),
            new StreamSlotTracker.SlotSnapshot(Alice, "ext", new DateTimeOffset(Now.AddSeconds(-20)), 0),
        };

        var result = ActiveStreamsBuilder.Build([tv, phoneIdle], slots, Now, _ => 2, _ => false, _ => "unused", (_, _) => false);

        var alice = Assert.Single(result);
        Assert.Equal("38a5a5bb90ab4590837d16bcb0b1c1f0", alice.UserId);
        Assert.Equal("alice", alice.UserName);
        Assert.Equal(2, alice.Limit);
        Assert.Equal(2, alice.Streams.Count);

        var tvStream = alice.Streams.Single(s => s.DeviceId.Equals("tv", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("both", tvStream.Source);
        Assert.Equal("Movie", tvStream.ItemName);
        Assert.Equal("Transcode", tvStream.PlayMethod);
        Assert.Equal(tv.Id, tvStream.SessionId);
        Assert.Equal(Now.AddSeconds(5), tvStream.LastActivity);

        // HTTP-only device (external player): named from its idle session.
        var ext = alice.Streams.Single(s => s.DeviceId == "ext");
        Assert.Equal("slot", ext.Source);
        Assert.Equal("Phone", ext.DeviceName);
        Assert.Null(ext.ItemName);
        Assert.Equal("unknown", ext.PlayMethod);
    }

    [Fact]
    public void Build_SkipsUsersWithNothingCounted_AndFlagsExemptAndEnforced()
    {
        var idle = Session(Bob, "laptop", "Laptop", null, null);
        var stale = Session(Bob, "tv", "TV", "Old", PlayMethod.DirectPlay);
        stale.LastPlaybackCheckIn = Now - PlayingSessions.StaleAfter - TimeSpan.FromSeconds(1);
        var zombie = Session(Alice, "afinity", "Pixel", "Show", PlayMethod.DirectStream);

        var result = ActiveStreamsBuilder.Build(
            [idle, stale, zombie],
            [],
            Now,
            _ => 0,
            id => id == Alice,
            _ => null,
            (user, device) => user == Alice && device == "afinity");

        var alice = Assert.Single(result);
        Assert.True(alice.Exempt);
        var stream = Assert.Single(alice.Streams);
        Assert.True(stream.Enforced);
        Assert.Equal("session", stream.Source);
        Assert.Equal("DirectStream", stream.PlayMethod);
    }

    [Fact]
    public void Build_SlotOnlyUser_UsesUserNameLookup()
    {
        var slots = new[] { new StreamSlotTracker.SlotSnapshot(Bob, "vlc", new DateTimeOffset(Now), 1) };

        var result = ActiveStreamsBuilder.Build([], slots, Now, _ => 1, _ => false, id => id == Bob ? "bob" : null, (_, _) => false);

        var bob = Assert.Single(result);
        Assert.Equal("bob", bob.UserName);
        var stream = Assert.Single(bob.Streams);
        Assert.Equal("vlc", stream.DeviceId);
        Assert.Null(stream.DeviceName);
        Assert.Null(stream.SessionId);
        Assert.Equal(DateTimeKind.Utc, stream.LastActivity!.Value.Kind);
    }

    private static SessionInfo Session(Guid userId, string deviceId, string deviceName, string? itemName, PlayMethod? playMethod)
        => new(null!, NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            UserName = userId == Alice ? "alice" : "bob",
            DeviceId = deviceId,
            DeviceName = deviceName,
            Client = "Jellyfin Web",
            RemoteEndPoint = "192.168.1.20",
            NowPlayingItem = itemName is null ? null : new BaseItemDto { Name = itemName },
            PlayState = new PlayerStateInfo { PlayMethod = playMethod },
            LastPlaybackCheckIn = Now,
        };
}
