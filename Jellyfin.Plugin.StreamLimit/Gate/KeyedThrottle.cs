using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Thread-safe "at most once per interval per key" throttle, used to collapse the burst of
/// denials a single blocked playback attempt produces (PlaybackInfo, playlists, segments,
/// client retries) into one notification or one activity-log entry.
/// </summary>
public sealed class KeyedThrottle
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyedThrottle"/> class.
    /// </summary>
    /// <param name="interval">Minimum time between two accepted events of the same key.</param>
    /// <param name="clock">The time source.</param>
    public KeyedThrottle(TimeSpan interval, TimeProvider? clock = null)
    {
        _interval = interval;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Builds the throttle key of a user and device.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <returns>The key.</returns>
    public static string UserDeviceKey(Guid userId, string? deviceId)
        => StreamLimitStore.NormalizeUserKey(userId) + "|" + deviceId;

    /// <summary>
    /// Accepts the event when no event of the same key was accepted within the interval.
    /// Exactly one of several concurrent callers wins.
    /// </summary>
    /// <param name="key">The throttle key.</param>
    /// <returns>True when the caller may proceed.</returns>
    public bool TryAcquire(string key)
    {
        var now = _clock.GetUtcNow();
        var last = _last.GetOrAdd(key, DateTimeOffset.MinValue);
        if (now - last < _interval || !_last.TryUpdate(key, now, last))
        {
            return false;
        }

        // Opportunistic pruning keeps the map from growing forever on servers with many
        // rotating browser device ids.
        var keepFor = _interval > TimeSpan.FromMinutes(10) ? _interval : TimeSpan.FromMinutes(10);
        foreach (var (staleKey, seenAt) in _last)
        {
            if (now - seenAt > keepFor)
            {
                _last.TryRemove(staleKey, out _);
            }
        }

        return true;
    }
}
