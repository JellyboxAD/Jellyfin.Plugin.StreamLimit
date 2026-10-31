using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Remembers the PlaySessionId each session reported with its last playback start.
/// </summary>
/// <remarks>
/// Jellyfin's <c>SessionInfo</c> does not expose the current PlaySessionId, but killing a
/// transcode job without one would kill every job of the device. An admin "Stop" from the
/// live view needs it to kill only the offending transcode. Entries are forgotten on stop or
/// session end, and expire after <see cref="Lifetime"/> (a session that never reported a stop
/// and never ended must not stay forever).
/// </remarks>
public sealed class PlaySessionRegistry
{
    /// <summary>
    /// How long an entry is kept at most after its playback start.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (string PlaySessionId, DateTimeOffset At)> _playSessions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaySessionRegistry"/> class.
    /// </summary>
    /// <param name="clock">The time source.</param>
    public PlaySessionRegistry(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the number of entries currently remembered.
    /// </summary>
    public int Count => _playSessions.Count;

    /// <summary>
    /// Records the PlaySessionId of a session's playback start.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="playSessionId">The PlaySessionId; null or empty forgets the session.</param>
    public void Remember(string? sessionId, string? playSessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        if (string.IsNullOrEmpty(playSessionId))
        {
            _playSessions.TryRemove(sessionId, out _);
            return;
        }

        _playSessions[sessionId] = (playSessionId, _clock.GetUtcNow());
    }

    /// <summary>
    /// Gets the last known PlaySessionId of a session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The PlaySessionId, or null when unknown or expired.</returns>
    public string? Get(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_playSessions.TryGetValue(sessionId, out var entry))
        {
            return null;
        }

        return _clock.GetUtcNow() - entry.At > Lifetime ? null : entry.PlaySessionId;
    }

    /// <summary>
    /// Forgets a session (playback stopped or session ended).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    public void Forget(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            _playSessions.TryRemove(sessionId, out _);
        }
    }

    /// <summary>
    /// Drops the expired entries (periodic housekeeping).
    /// </summary>
    /// <returns>The number of entries dropped.</returns>
    public int Prune()
    {
        var now = _clock.GetUtcNow();
        var dropped = 0;
        foreach (var entry in _playSessions)
        {
            if (now - entry.Value.At > Lifetime && _playSessions.TryRemove(KeyValuePair.Create(entry.Key, entry.Value)))
            {
                dropped++;
            }
        }

        return dropped;
    }
}
