using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamLimit.Gate;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class KeyedThrottleTests
{
    private static readonly Guid User = Guid.Parse("38a5a5bb-90ab-4590-837d-16bcb0b1c1f0");

    private readonly TestClock _clock = new();

    [Fact]
    public void SameKey_AcceptedOncePerInterval()
    {
        var throttle = new KeyedThrottle(BlockActivityLogger.ThrottleInterval, _clock);
        var key = KeyedThrottle.UserDeviceKey(User, "tv");

        Assert.True(throttle.TryAcquire(key));
        _clock.Advance(BlockActivityLogger.ThrottleInterval - TimeSpan.FromSeconds(1));
        Assert.False(throttle.TryAcquire(key));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryAcquire(key));
    }

    [Fact]
    public void DifferentDevicesAndUsers_AreIndependent()
    {
        var throttle = new KeyedThrottle(TimeSpan.FromMinutes(5), _clock);

        Assert.True(throttle.TryAcquire(KeyedThrottle.UserDeviceKey(User, "tv")));
        Assert.True(throttle.TryAcquire(KeyedThrottle.UserDeviceKey(User, "phone")));
        Assert.True(throttle.TryAcquire(KeyedThrottle.UserDeviceKey(Guid.NewGuid(), "tv")));
        Assert.False(throttle.TryAcquire(KeyedThrottle.UserDeviceKey(User, "TV")));
    }

    [Fact]
    public void ConcurrentBurst_AcceptsExactlyOne()
    {
        // A blocked attempt fires PlaybackInfo, playlists and segments at once.
        var throttle = new KeyedThrottle(TimeSpan.FromMinutes(5), _clock);
        var key = KeyedThrottle.UserDeviceKey(User, "tv");

        var accepted = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(_ => throttle.TryAcquire(key))
            .Count(ok => ok);

        Assert.Equal(1, accepted);
    }

    [Fact]
    public async Task ConcurrentBurst_FromTasks_AcceptsExactlyOne()
    {
        var throttle = new KeyedThrottle(TimeSpan.FromSeconds(30), _clock);
        var key = KeyedThrottle.UserDeviceKey(User, "web");

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => throttle.TryAcquire(key))));

        Assert.Single(results, ok => ok);
    }
}
