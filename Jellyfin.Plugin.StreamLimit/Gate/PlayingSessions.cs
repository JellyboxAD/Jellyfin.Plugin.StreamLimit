using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Single definition of "a device currently streaming" shared by the HTTP gate and
/// the reactive PlaybackStart limiter, so both enforcement layers count the same way.
/// </summary>
public static class PlayingSessions
{
    /// <summary>
    /// How long a playing session may go without a playback check-in before it stops counting.
    /// A crashed client's session keeps NowPlayingItem set (and IsActive stays true for clients
    /// without a remote-control channel) until Jellyfin's 5-10 minute reaper runs; it must not
    /// lock the user out that long.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Gets the distinct device ids of the user's sessions that are actively playing.
    /// </summary>
    /// <param name="sessions">All server sessions.</param>
    /// <param name="userId">The user.</param>
    /// <param name="utcNow">Current UTC time.</param>
    /// <returns>Distinct device ids (case-insensitive).</returns>
    public static IReadOnlyCollection<string> GetPlayingDeviceIds(IEnumerable<SessionInfo> sessions, Guid userId, DateTime utcNow)
        => Playing(sessions, userId, utcNow)
            .Where(s => !string.IsNullOrEmpty(s.DeviceId))
            .Select(s => s.DeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Gets one key per stream the user is actively playing: the device id, or the session id
    /// for a session without a device id (so it is never silently left out of the count).
    /// </summary>
    /// <param name="sessions">All server sessions.</param>
    /// <param name="userId">The user.</param>
    /// <param name="utcNow">Current UTC time.</param>
    /// <returns>Distinct stream keys (case-insensitive).</returns>
    public static HashSet<string> GetPlayingStreamKeys(IEnumerable<SessionInfo> sessions, Guid userId, DateTime utcNow)
        => Playing(sessions, userId, utcNow)
            .Select(StreamKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the key a session counts under: its device id, or its session id when it has none.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>The stream key.</returns>
    public static string StreamKey(SessionInfo session)
        => string.IsNullOrEmpty(session.DeviceId) ? session.Id : session.DeviceId;

    private static IEnumerable<SessionInfo> Playing(IEnumerable<SessionInfo> sessions, Guid userId, DateTime utcNow)
    {
        var staleBefore = utcNow - StaleAfter;
        return sessions.Where(s => s.UserId.Equals(userId)
                                   && s.NowPlayingItem is not null
                                   && s.IsActive
                                   && s.LastPlaybackCheckIn >= staleBefore);
    }
}
