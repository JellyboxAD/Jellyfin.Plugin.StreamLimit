using System.Collections.Concurrent;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Remembers the PlaySessionId each session reported with its last playback start.
/// </summary>
/// <remarks>
/// Jellyfin's <c>SessionInfo</c> does not expose the current PlaySessionId, but killing a
/// transcode job without one would kill every job of the device. An admin "Stop" from the
/// live view needs it to kill only the offending transcode.
/// </remarks>
public sealed class PlaySessionRegistry
{
    private readonly ConcurrentDictionary<string, string> _playSessions = new();

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

        _playSessions[sessionId] = playSessionId;
    }

    /// <summary>
    /// Gets the last known PlaySessionId of a session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The PlaySessionId, or null when unknown.</returns>
    public string? Get(string? sessionId)
        => !string.IsNullOrEmpty(sessionId) && _playSessions.TryGetValue(sessionId, out var id) ? id : null;

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
}
