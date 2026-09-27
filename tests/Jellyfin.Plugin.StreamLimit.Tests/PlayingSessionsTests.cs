using System;
using Jellyfin.Plugin.StreamLimit.Gate;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class PlayingSessionsTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static SessionInfo Playing(string deviceId, DateTime lastCheckIn, Guid? userId = null)
        => new(null!, NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId ?? User,
            DeviceId = deviceId,
            NowPlayingItem = new BaseItemDto(),
            LastPlaybackCheckIn = lastCheckIn,
        };

    [Fact]
    public void StaleSession_IsNotCounted()
    {
        // A crashed client still has NowPlayingItem set but stopped checking in.
        var ghost = Playing("tv", Now - PlayingSessions.StaleAfter - TimeSpan.FromSeconds(1));
        var live = Playing("phone", Now.AddSeconds(-5));

        var devices = PlayingSessions.GetPlayingDeviceIds([ghost, live], User, Now);

        Assert.Equal(["phone"], devices);
    }

    [Fact]
    public void SessionsOnSameDevice_CountOnce()
    {
        var tab1 = Playing("Browser-1", Now);
        var tab2 = Playing("browser-1", Now);

        Assert.Single(PlayingSessions.GetPlayingDeviceIds([tab1, tab2], User, Now));
    }

    [Fact]
    public void IdleOtherUserAndDeviceless_AreIgnored()
    {
        var idle = Playing("tv", Now);
        idle.NowPlayingItem = null;
        var otherUser = Playing("laptop", Now, Guid.NewGuid());
        var noDevice = Playing(string.Empty, Now);

        Assert.Empty(PlayingSessions.GetPlayingDeviceIds([idle, otherUser, noDevice], User, Now));
    }

    [Fact]
    public void StreamKeys_CountDevicelessSessionsBySessionId()
    {
        var tv = Playing("tv", Now);
        var noDevice1 = Playing(string.Empty, Now);
        var noDevice2 = Playing(string.Empty, Now);
        var ghost = Playing(string.Empty, Now - PlayingSessions.StaleAfter - TimeSpan.FromSeconds(1));

        var keys = PlayingSessions.GetPlayingStreamKeys([tv, noDevice1, noDevice2, ghost], User, Now);

        Assert.Equal(3, keys.Count);
        Assert.Contains(noDevice1.Id, keys);
    }
}
