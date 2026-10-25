using System;
using Jellyfin.Plugin.StreamLimit.Gate;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class EnforcedDeviceRegistryTests
{
    private static readonly Guid User = Guid.Parse("38a5a5bb-90ab-4590-837d-16bcb0b1c1f0");
    private static readonly Guid OtherUser = Guid.Parse("6c3f7c1e-2d4b-4a8f-9e01-abcdefabcdef");

    private readonly TestClock _clock = new();

    private EnforcedDeviceRegistry CreateRegistry() => new(_clock);

    [Fact]
    public void Record_MarksDeviceEnforced_PerUserAndCaseInsensitive()
    {
        var registry = CreateRegistry();
        registry.Record(User, "AFinity-1");

        Assert.True(registry.IsEnforced(User, "afinity-1"));
        Assert.False(registry.IsEnforced(OtherUser, "AFinity-1"));
        Assert.False(registry.IsEnforced(User, "phone"));
    }

    [Fact]
    public void Entry_ExpiresAfterLifetime()
    {
        var registry = CreateRegistry();
        registry.Record(User, "tv");

        _clock.Advance(EnforcedDeviceRegistry.Lifetime - TimeSpan.FromSeconds(1));
        Assert.True(registry.IsEnforced(User, "tv"));

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(registry.IsEnforced(User, "tv"));
    }

    [Fact]
    public void Clear_RemovesEntry()
    {
        // Stop report or session end: the device is no zombie anymore.
        var registry = CreateRegistry();
        registry.Record(User, "tv");

        registry.Clear(User, "TV");

        Assert.False(registry.IsEnforced(User, "tv"));
    }

    [Fact]
    public void ClearOnReentry_IgnoredWithinGrace()
    {
        // A segment request already in flight when Stop was sent must not re-arm the zombie.
        var registry = CreateRegistry();
        registry.Record(User, "tv");

        _clock.Advance(EnforcedDeviceRegistry.ReentryGrace - TimeSpan.FromSeconds(1));
        registry.ClearOnReentry(User, "tv");

        Assert.True(registry.IsEnforced(User, "tv"));
    }

    [Fact]
    public void ClearOnReentry_ClearsAfterGrace()
    {
        var registry = CreateRegistry();
        registry.Record(User, "tv");

        _clock.Advance(EnforcedDeviceRegistry.ReentryGrace);
        registry.ClearOnReentry(User, "tv");

        Assert.False(registry.IsEnforced(User, "tv"));
    }

    [Fact]
    public void Record_AgainRestartsLifetime()
    {
        var registry = CreateRegistry();
        registry.Record(User, "tv");
        _clock.Advance(TimeSpan.FromMinutes(8));
        registry.Record(User, "tv");
        _clock.Advance(TimeSpan.FromMinutes(8));

        Assert.True(registry.IsEnforced(User, "tv"));
    }

    [Fact]
    public void EmptyKeysAndUsers_AreIgnored()
    {
        var registry = CreateRegistry();
        registry.Record(Guid.Empty, "tv");
        registry.Record(User, null);
        registry.Record(User, string.Empty);

        Assert.False(registry.IsEnforced(Guid.Empty, "tv"));
        Assert.False(registry.IsEnforced(User, null));
        Assert.False(registry.IsEnforced(User, string.Empty));
    }

    [Fact]
    public void ZombieSession_IsNotCountedAsPlaying()
    {
        // The enforced device keeps a fresh check-in (progress reports after Stop): it must
        // stop counting, so the user can start a stream elsewhere.
        var registry = CreateRegistry();
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var zombie = Session("afinity", now);
        var live = Session("phone", now);
        registry.Record(User, "afinity");

        var devices = PlayingSessions.GetPlayingDeviceIds(
            [zombie, live],
            User,
            now,
            s => registry.IsEnforced(User, PlayingSessions.StreamKey(s)));

        Assert.Equal(["phone"], devices);
    }

    private static SessionInfo Session(string deviceId, DateTime lastCheckIn)
        => new(null!, NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = User,
            DeviceId = deviceId,
            NowPlayingItem = new BaseItemDto(),
            LastPlaybackCheckIn = lastCheckIn,
        };
}
