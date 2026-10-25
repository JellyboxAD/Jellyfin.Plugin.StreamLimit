using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Remembers the devices the reactive limiter has just stopped, so their sessions stop
/// counting as playing even though the client keeps them alive.
/// </summary>
/// <remarks>
/// Several clients leave a "zombie" session behind after enforcement: AFinity keeps
/// reporting progress every few seconds after our Stop command (it only stops its
/// player), Swiftfin sends no stopped report when playback fails, and the jellyfin-android
/// native player cannot receive the Stop command at all. Those sessions keep a fresh
/// LastPlaybackCheckIn, so without this registry the blocked device would keep holding a
/// stream and lock the user out. An entry is cleared when the device reports a stop, when
/// its session ends, when it is later admitted again (gate or playback start), or after
/// <see cref="Lifetime"/>.
/// </remarks>
public sealed class EnforcedDeviceRegistry
{
    /// <summary>
    /// How long an enforced device stays excluded at most.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long after enforcement a successful gate entry does NOT clear the entry: media
    /// requests already in flight when the Stop command was sent (the next HLS segment)
    /// would otherwise re-arm the zombie session immediately.
    /// </summary>
    public static readonly TimeSpan ReentryGrace = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _enforced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="EnforcedDeviceRegistry"/> class.
    /// </summary>
    /// <param name="clock">The time source.</param>
    public EnforcedDeviceRegistry(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Records that the stream limit was just enforced on a device.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="streamKey">The device id (or the session id of a session without one).</param>
    public void Record(Guid userId, string? streamKey)
    {
        if (userId.Equals(Guid.Empty) || string.IsNullOrEmpty(streamKey))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        _enforced[Key(userId, streamKey)] = now;

        // Opportunistic pruning keeps the map bounded on servers with rotating device ids.
        foreach (var (key, enforcedAt) in _enforced)
        {
            if (now - enforcedAt > Lifetime)
            {
                _enforced.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Whether the device is currently excluded from the playing count.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="streamKey">The device id (or the session id of a session without one).</param>
    /// <returns>True while the device's enforcement is recent and not cleared.</returns>
    public bool IsEnforced(Guid userId, string? streamKey)
    {
        if (_enforced.IsEmpty || string.IsNullOrEmpty(streamKey))
        {
            return false;
        }

        var key = Key(userId, streamKey);
        if (!_enforced.TryGetValue(key, out var enforcedAt))
        {
            return false;
        }

        if (_clock.GetUtcNow() - enforcedAt > Lifetime)
        {
            _enforced.TryRemove(key, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Clears a device's entry: it stopped (stop report, session ended) or was legitimately
    /// admitted again.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="streamKey">The device id (or the session id of a session without one).</param>
    public void Clear(Guid userId, string? streamKey)
    {
        if (_enforced.IsEmpty || string.IsNullOrEmpty(streamKey))
        {
            return;
        }

        _enforced.TryRemove(Key(userId, streamKey), out _);
    }

    /// <summary>
    /// Clears a device's entry after the gate admitted one of its media requests, unless
    /// the enforcement is younger than <see cref="ReentryGrace"/>.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="streamKey">The device id.</param>
    public void ClearOnReentry(Guid userId, string? streamKey)
    {
        if (_enforced.IsEmpty || string.IsNullOrEmpty(streamKey))
        {
            return;
        }

        var key = Key(userId, streamKey);
        if (_enforced.TryGetValue(key, out var enforcedAt) && _clock.GetUtcNow() - enforcedAt >= ReentryGrace)
        {
            // Remove only the entry we inspected: a fresh Record racing this call wins.
            _enforced.TryRemove(KeyValuePair.Create(key, enforcedAt));
        }
    }

    private static string Key(Guid userId, string streamKey)
        => StreamLimitStore.NormalizeUserKey(userId) + "|" + streamKey;
}
