using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.StreamLimit.Gate;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.StreamLimit.Api;

/// <summary>
/// One user's streams in the admin live view (<c>GET /StreamLimit/ActiveStreams</c>).
/// </summary>
public sealed class ActiveUserStreams
{
    /// <summary>Gets or sets the user id (dashless).</summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user name.</summary>
    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    /// <summary>Gets or sets the user's effective limit (0 = unlimited).</summary>
    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    /// <summary>Gets or sets a value indicating whether the user is exempt (administrator).</summary>
    [JsonPropertyName("exempt")]
    public bool Exempt { get; set; }

    /// <summary>Gets the user's streams, one per device.</summary>
    [JsonPropertyName("streams")]
    public List<ActiveStream> Streams { get; } = new();
}

/// <summary>
/// One device's stream in the admin live view.
/// </summary>
public sealed class ActiveStream
{
    /// <summary>Gets or sets the device id (the session id for a session without one).</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the client application.</summary>
    [JsonPropertyName("client")]
    public string? Client { get; set; }

    /// <summary>Gets or sets the name of the item playing.</summary>
    [JsonPropertyName("itemName")]
    public string? ItemName { get; set; }

    /// <summary>Gets or sets the play method: DirectPlay, DirectStream, Transcode or unknown.</summary>
    [JsonPropertyName("playMethod")]
    public string PlayMethod { get; set; } = "unknown";

    /// <summary>Gets or sets the client's remote address.</summary>
    [JsonPropertyName("remoteEndPoint")]
    public string? RemoteEndPoint { get; set; }

    /// <summary>Gets or sets the session id.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the last playback check-in or media request (UTC).</summary>
    [JsonPropertyName("lastActivity")]
    public DateTime? LastActivity { get; set; }

    /// <summary>Gets or sets where the stream was seen: session, slot or both.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "session";

    /// <summary>Gets or sets a value indicating whether the limiter stopped this device recently (not counted).</summary>
    [JsonPropertyName("enforced")]
    public bool Enforced { get; set; }
}

/// <summary>
/// Builds the admin live view from the sessions and the gate's slots. Pure apart from the
/// lookups passed in, so it is unit-testable.
/// </summary>
public static class ActiveStreamsBuilder
{
    /// <summary>
    /// Builds the live view: every user with at least one playing session or gate slot.
    /// </summary>
    /// <param name="sessions">All server sessions.</param>
    /// <param name="slots">The gate's slots.</param>
    /// <param name="utcNow">Current UTC time.</param>
    /// <param name="getLimit">Effective limit of a user.</param>
    /// <param name="isExempt">Whether a user is exempt.</param>
    /// <param name="getUserName">User name lookup (used when no session carries one).</param>
    /// <param name="isEnforced">Whether a user's device is in the enforced registry.</param>
    /// <returns>The users, by name.</returns>
    public static IReadOnlyList<ActiveUserStreams> Build(
        IReadOnlyCollection<SessionInfo> sessions,
        IReadOnlyCollection<StreamSlotTracker.SlotSnapshot> slots,
        DateTime utcNow,
        Func<Guid, int> getLimit,
        Func<Guid, bool> isExempt,
        Func<Guid, string?> getUserName,
        Func<Guid, string, bool> isEnforced)
    {
        var userIds = sessions.Where(s => !s.UserId.Equals(Guid.Empty)).Select(s => s.UserId)
            .Concat(slots.Select(s => s.UserId))
            .Distinct();

        var result = new List<ActiveUserStreams>();
        foreach (var userId in userIds)
        {
            var userSessions = sessions.Where(s => s.UserId.Equals(userId)).ToList();
            var playing = PlayingSessions.GetPlayingSessions(userSessions, userId, utcNow).ToList();
            var userSlots = slots.Where(s => s.UserId.Equals(userId)).ToList();
            if (playing.Count == 0 && userSlots.Count == 0)
            {
                continue;
            }

            var user = new ActiveUserStreams
            {
                UserId = StreamLimitStore.NormalizeUserKey(userId),
                UserName = userSessions.Select(s => s.UserName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? getUserName(userId),
                Limit = getLimit(userId),
                Exempt = isExempt(userId),
            };

            var keys = playing.Select(PlayingSessions.StreamKey)
                .Concat(userSlots.Select(s => s.DeviceId))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                var session = playing.FirstOrDefault(s => string.Equals(PlayingSessions.StreamKey(s), key, StringComparison.OrdinalIgnoreCase));
                var slot = userSlots.FirstOrDefault(s => string.Equals(s.DeviceId, key, StringComparison.OrdinalIgnoreCase));

                // A slot-only device (external player, HTTP traffic only) may still have an
                // idle session that knows its name.
                var anySession = session ?? userSessions.FirstOrDefault(s => string.Equals(s.DeviceId, key, StringComparison.OrdinalIgnoreCase));

                DateTime? lastActivity = session?.LastPlaybackCheckIn;
                if (slot is not null && (lastActivity is null || slot.LastSeenUtc.UtcDateTime > lastActivity))
                {
                    lastActivity = slot.LastSeenUtc.UtcDateTime;
                }

                user.Streams.Add(new ActiveStream
                {
                    DeviceId = key,
                    DeviceName = anySession?.DeviceName,
                    Client = anySession?.Client,
                    ItemName = session?.NowPlayingItem?.Name,
                    PlayMethod = session?.PlayState?.PlayMethod?.ToString() ?? "unknown",
                    RemoteEndPoint = anySession?.RemoteEndPoint,
                    SessionId = anySession?.Id,
                    LastActivity = lastActivity is null ? null : DateTime.SpecifyKind(lastActivity.Value, DateTimeKind.Utc),
                    Source = session is not null && slot is not null ? "both" : session is not null ? "session" : "slot",
                    Enforced = isEnforced(userId, key),
                });
            }

            user.Streams.Sort((a, b) => string.Compare(a.DeviceName ?? a.DeviceId, b.DeviceName ?? b.DeviceId, StringComparison.OrdinalIgnoreCase));
            result.Add(user);
        }

        result.Sort((a, b) => string.Compare(a.UserName ?? a.UserId, b.UserName ?? b.UserId, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}
