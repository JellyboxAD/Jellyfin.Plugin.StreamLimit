using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Why a playback was refused by the <see cref="PlaybackTracker"/>.
/// </summary>
public enum PlaybackDenialReason
{
    /// <summary>Over the user's stream limit, counting each playback (not each device).</summary>
    PlaybackLimit = 0,

    /// <summary>Over the per-user limit of simultaneous video transcodes.</summary>
    TranscodeLimit = 1,
}

/// <summary>
/// Tracks individual playbacks (one per PlaySessionId) on top of the device slots of the
/// <see cref="StreamSlotTracker"/>, for the two opt-in rules that need them: counting each
/// playback instead of each device, and limiting simultaneous video transcodes per user.
/// </summary>
/// <remarks>
/// <para>
/// Several browser tabs share one device id, and Jellyfin even merges them into one session,
/// but each playback still has its own PlaySessionId in its media URLs (and its own ffmpeg
/// job). A request without one is keyed by device and item instead.
/// </para>
/// <para>
/// A playback counts while it is live: a media request within <see cref="RecentRequestWindow"/>
/// (HLS segments), or an open response that wrote bytes within
/// <see cref="StreamSlotTracker.ActiveResponseWindow"/> (a direct-play download).
/// </para>
/// <para>
/// A quality switch or a next episode briefly shows two live playbacks on one device: the old
/// one stops fetching the moment the new one starts. A playback over a limit is therefore
/// refused only when the overlap is confirmed: it is at least <see cref="OverlapGrace"/> old and
/// an older playback of the same rule still made a media request after it started (or is still
/// sending a long download <see cref="DownloadConfirmAfter"/> after it started). A replaced
/// playback never does, so nothing is refused. The newest playbacks are the ones refused.
/// </para>
/// </remarks>
public sealed class PlaybackTracker
{
    /// <summary>
    /// How long a playback stays live after its last media request (longer than the HLS
    /// segment interval of a playing client).
    /// </summary>
    public static readonly TimeSpan RecentRequestWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The minimum age of an over-limit playback before it is refused (its first requests
    /// may race the last ones of the playback it replaces).
    /// </summary>
    public static readonly TimeSpan OverlapGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after a playback started an older one must still be making requests for the
    /// overlap to count (a request racing the switch is not an overlap).
    /// </summary>
    public static readonly TimeSpan RequestConfirmAfter = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long after a playback started an older long download (direct play) must still be
    /// sending bytes for the overlap to count (the tail of a replaced download is not one).
    /// </summary>
    public static readonly TimeSpan DownloadConfirmAfter = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long a playback without traffic is remembered (a refused one keeps being refused
    /// while its client retries, and is forgotten this long after it gives up).
    /// </summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a refusal holds before the playback is checked again: if the user closed
    /// another stream meanwhile, a client that keeps retrying recovers on its own.
    /// </summary>
    public static readonly TimeSpan DenialHolds = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, Dictionary<string, Entry>> _users = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackTracker"/> class.
    /// </summary>
    /// <param name="clock">The time source.</param>
    public PlaybackTracker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Records a media request of a playback.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="playSessionId">The PlaySessionId from the request, if any.</param>
    /// <param name="itemId">The requested item, used as the key when there is no PlaySessionId.</param>
    /// <param name="isVideo">Whether the request is on a video endpoint that can run ffmpeg.</param>
    /// <param name="request">The gate's registration of the response, so a live download counts and can be aborted.</param>
    /// <returns>What is known about the playback, or null when the request cannot be keyed.</returns>
    public PlaybackObservation? Observe(
        Guid userId,
        string deviceId,
        string? playSessionId,
        string? itemId,
        bool isVideo,
        StreamSlotTracker.MediaRequest? request = null)
    {
        if (Key(deviceId, playSessionId, itemId) is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        var entries = _users.GetOrAdd(userId, static _ => new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
        lock (entries)
        {
            var key = Resolve(entries, deviceId, playSessionId, itemId, now)!;
            var isNew = !entries.TryGetValue(key, out var entry);
            if (entry is null)
            {
                entry = new Entry(deviceId, string.IsNullOrEmpty(playSessionId) ? null : playSessionId, itemId, now);
                entries[key] = entry;
            }

            ExpireDenial(entry, now);
            entry.LastSeenUtc = now;
            entry.IsVideo |= isVideo;
            if (request is not null)
            {
                entry.Requests.Add(request);
            }

            return new PlaybackObservation(isNew, entry.DeniedReason);
        }
    }

    /// <summary>
    /// Attaches an admitted media response to its playback, so a live download keeps it
    /// counting and a refusal can abort it.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="playSessionId">The PlaySessionId from the request, if any.</param>
    /// <param name="itemId">The requested item.</param>
    /// <param name="request">The gate's registration of the response.</param>
    public void Attach(Guid userId, string deviceId, string? playSessionId, string? itemId, StreamSlotTracker.MediaRequest request)
    {
        if (!_users.TryGetValue(userId, out var entries))
        {
            return;
        }

        lock (entries)
        {
            var key = Resolve(entries, deviceId, playSessionId, itemId, _clock.GetUtcNow());
            if (key is not null && entries.TryGetValue(key, out var entry))
            {
                entry.Requests.Add(request);
            }
        }
    }

    /// <summary>
    /// Whether a playback was refused.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="playSessionId">The PlaySessionId from the request, if any.</param>
    /// <param name="itemId">The requested item.</param>
    /// <returns>Why it was refused, or null when it is allowed (or unknown).</returns>
    public PlaybackDenialReason? GetDenial(Guid userId, string deviceId, string? playSessionId, string? itemId)
    {
        if (!_users.TryGetValue(userId, out var entries))
        {
            return null;
        }

        lock (entries)
        {
            var key = Resolve(entries, deviceId, playSessionId, itemId, _clock.GetUtcNow());
            return key is not null && entries.TryGetValue(key, out var entry) ? entry.DeniedReason : null;
        }
    }

    /// <summary>
    /// Whether a device has a live, allowed playback other than the given one (so refusing
    /// that one must not free the device slot).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="playSessionId">The refused playback PlaySessionId, if any.</param>
    /// <param name="itemId">The refused playback item.</param>
    /// <returns>True when another playback of the device is live.</returns>
    public bool HasOtherLivePlayback(Guid userId, string deviceId, string? playSessionId, string? itemId)
    {
        if (!_users.TryGetValue(userId, out var entries))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        lock (entries)
        {
            var key = Resolve(entries, deviceId, playSessionId, itemId, now);
            return entries.Any(kv => !string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(kv.Value.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                                     && kv.Value.DeniedReason is null
                                     && IsLive(kv.Value, now));
        }
    }

    /// <summary>
    /// Applies both rules to the user's live playbacks and marks the ones to refuse.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="heldDevices">The devices holding one of the user's streams (gate slots and playing sessions).</param>
    /// <param name="playbackLimit">The stream limit when playbacks are counted; 0 or less disables the rule.</param>
    /// <param name="transcodeLimit">The simultaneous video transcode limit; 0 or less disables the rule.</param>
    /// <param name="isTranscoding">Whether a playback is currently a video transcode (called outside the lock).</param>
    /// <returns>The playbacks refused by this call, with the actions aborting their open responses.</returns>
    public IReadOnlyList<PlaybackDenial> Enforce(
        Guid userId,
        IReadOnlyCollection<string> heldDevices,
        int playbackLimit,
        int transcodeLimit,
        Func<PlaybackRef, bool> isTranscoding)
    {
        if ((playbackLimit <= 0 && transcodeLimit <= 0) || !_users.TryGetValue(userId, out var entries))
        {
            return Array.Empty<PlaybackDenial>();
        }

        var now = _clock.GetUtcNow();
        List<(Entry Entry, PlaybackRef Ref)> live;
        Dictionary<Entry, Activity> activity;
        lock (entries)
        {
            Prune(entries, now);
            foreach (var entry in entries.Values)
            {
                ExpireDenial(entry, now);
            }

            live = entries.Values
                .Where(e => e.DeniedReason is null && IsLive(e, now))
                .Select(e => (e, new PlaybackRef(e.DeviceId, e.PlaySessionId, e.IsVideo)))
                .ToList();
            activity = live.ToDictionary(p => p.Entry, p => ActivityOf(p.Entry));
        }

        if (live.Count == 0)
        {
            return Array.Empty<PlaybackDenial>();
        }

        var toDeny = new List<(Entry Entry, PlaybackDenialReason Reason, int Active, int Limit)>();

        if (transcodeLimit > 0)
        {
            // Looked up outside the lock: it asks Jellyfin's transcode manager.
            var transcoding = live.Where(p => p.Ref.IsVideo && SafeIsTranscoding(isTranscoding, p.Ref)).Select(p => p.Entry).ToList();
            foreach (var entry in Newest(transcoding, transcoding.Count - transcodeLimit, now, activity))
            {
                toDeny.Add((entry, PlaybackDenialReason.TranscodeLimit, transcoding.Count - 1, transcodeLimit));
            }
        }

        if (playbackLimit > 0)
        {
            var remaining = live.Select(p => p.Entry).Where(e => toDeny.All(d => d.Entry != e)).ToList();
            var devices = new HashSet<string>(heldDevices, StringComparer.OrdinalIgnoreCase);
            devices.UnionWith(remaining.Select(e => e.DeviceId));

            // Each device counts once (its oldest live playback); every further live playback
            // of the same device is an extra stream.
            var extras = remaining
                .GroupBy(e => e.DeviceId, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.OrderBy(e => e.FirstSeenUtc).Skip(1))
                .ToList();
            var total = devices.Count + extras.Count;
            foreach (var entry in Newest(extras, total - playbackLimit, now, activity, remaining))
            {
                toDeny.Add((entry, PlaybackDenialReason.PlaybackLimit, total - 1, playbackLimit));
            }
        }

        if (toDeny.Count == 0)
        {
            return Array.Empty<PlaybackDenial>();
        }

        var denials = new List<PlaybackDenial>();
        lock (entries)
        {
            foreach (var (entry, reason, active, limit) in toDeny)
            {
                if (entry.DeniedReason is not null)
                {
                    continue;
                }

                entry.DeniedReason = reason;
                entry.DeniedAtUtc = now;
                var aborts = entry.Requests
                    .Where(r => !r.IsStale && r.Abort is not null)
                    .Select(r => r.Abort!)
                    .ToList();
                denials.Add(new PlaybackDenial(userId, entry.DeviceId, entry.PlaySessionId, reason, active, limit, aborts));
            }
        }

        return denials;
    }

    /// <summary>
    /// Forgets a playback that reported its stop.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="playSessionId">The PlaySessionId of the stopped playback; when empty, every playback of the device without one is forgotten.</param>
    public void End(Guid userId, string? deviceId, string? playSessionId)
    {
        if (string.IsNullOrEmpty(deviceId) || !_users.TryGetValue(userId, out var entries))
        {
            return;
        }

        lock (entries)
        {
            if (!string.IsNullOrEmpty(playSessionId))
            {
                entries.Remove(Key(deviceId, playSessionId, null)!);
                return;
            }

            RemoveWhere(entries, e => e.PlaySessionId is null && string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Forgets every playback of a device (its session ended, or an admin stopped it).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    public void EndDevice(Guid userId, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !_users.TryGetValue(userId, out var entries))
        {
            return;
        }

        lock (entries)
        {
            RemoveWhere(entries, e => string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Forgets every playback of a user (admin reset).
    /// </summary>
    /// <param name="userId">The user.</param>
    public void ResetUser(Guid userId)
    {
        if (_users.TryGetValue(userId, out var entries))
        {
            lock (entries)
            {
                entries.Clear();
            }
        }
    }

    /// <summary>
    /// Gets the number of live playbacks of a user, refused ones excluded.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The live playback count.</returns>
    public int CountLive(Guid userId)
    {
        if (!_users.TryGetValue(userId, out var entries))
        {
            return 0;
        }

        var now = _clock.GetUtcNow();
        lock (entries)
        {
            return entries.Values.Count(e => e.DeniedReason is null && IsLive(e, now));
        }
    }

    /// <summary>
    /// Gets the playbacks to show in the admin live view: the live ones, and the refused ones
    /// whose client is still trying (so the admin sees what was refused and why).
    /// </summary>
    /// <returns>A point-in-time copy, oldest first per user.</returns>
    public IReadOnlyList<PlaybackSnapshot> GetSnapshot()
    {
        var now = _clock.GetUtcNow();
        var result = new List<PlaybackSnapshot>();
        foreach (var (userId, entries) in _users)
        {
            lock (entries)
            {
                foreach (var entry in entries.Values.OrderBy(e => e.FirstSeenUtc))
                {
                    var live = IsLive(entry, now);
                    var refused = entry.DeniedReason is not null && now - entry.LastSeenUtc <= RecentRequestWindow;
                    if ((!live || entry.DeniedReason is not null) && !refused)
                    {
                        continue;
                    }

                    var activity = ActivityOf(entry);
                    var last = activity.LastBytesUtc > activity.LastRequestUtc ? activity.LastBytesUtc : activity.LastRequestUtc;
                    result.Add(new PlaybackSnapshot(
                        userId,
                        entry.DeviceId,
                        entry.PlaySessionId,
                        entry.ItemId,
                        entry.IsVideo,
                        entry.FirstSeenUtc,
                        last,
                        entry.DeniedReason));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Prunes every user's playbacks and returns the users that have more than one live
    /// playback (the only ones either rule can apply to), so they can be re-checked.
    /// </summary>
    /// <returns>The users to re-check.</returns>
    public IReadOnlyList<Guid> Sweep()
    {
        var now = _clock.GetUtcNow();
        var recheck = new List<Guid>();
        foreach (var (userId, entries) in _users)
        {
            lock (entries)
            {
                Prune(entries, now);
                if (entries.Values.Count(e => e.DeniedReason is null && IsLive(e, now)) > 1)
                {
                    recheck.Add(userId);
                }
            }
        }

        return recheck;
    }

    // A request without a PlaySessionId belongs to the device's live playback of the same item
    // when there is one (some clients send it on the playlist but not on every request), so
    // one playback is never split into two.
    private static string? Resolve(Dictionary<string, Entry> entries, string deviceId, string? playSessionId, string? itemId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(playSessionId) && !string.IsNullOrEmpty(itemId))
        {
            foreach (var (key, entry) in entries)
            {
                if (entry.PlaySessionId is not null
                    && string.Equals(entry.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase)
                    && IsLive(entry, now))
                {
                    return key;
                }
            }
        }

        return Key(deviceId, playSessionId, itemId);
    }

    private static void ExpireDenial(Entry entry, DateTimeOffset now)
    {
        if (entry.DeniedReason is not null && now - entry.DeniedAtUtc >= DenialHolds)
        {
            // Checked again by the next Enforce: refused at once if still over the limit
            // (it is past the overlap grace), allowed if the user freed a stream meanwhile.
            entry.DeniedReason = null;
        }
    }

    private static string? Key(string deviceId, string? playSessionId, string? itemId)
    {
        if (!string.IsNullOrEmpty(playSessionId))
        {
            return "ps|" + deviceId + "|" + playSessionId;
        }

        return string.IsNullOrEmpty(itemId) ? null : "item|" + deviceId + "|" + itemId;
    }

    // The newest `count` playbacks, of which only those with a confirmed overlap are refused
    // now; the others are checked again later (they may be replacing an older one, which then
    // stops counting). The pool is the rule's set of playbacks (defaults to the candidates).
    private static IEnumerable<Entry> Newest(
        List<Entry> candidates,
        int count,
        DateTimeOffset now,
        IReadOnlyDictionary<Entry, Activity> activity,
        List<Entry>? pool = null)
    {
        if (count <= 0)
        {
            return Enumerable.Empty<Entry>();
        }

        var others = pool ?? candidates;
        return candidates
            .OrderByDescending(e => e.FirstSeenUtc)
            .Take(count)
            .Where(e => now - e.FirstSeenUtc >= OverlapGrace
                        && others.Any(o => o != e
                                           && o.FirstSeenUtc <= e.FirstSeenUtc
                                           && activity.TryGetValue(o, out var a)
                                           && (a.LastRequestUtc >= e.FirstSeenUtc + RequestConfirmAfter
                                               || a.LastBytesUtc >= e.FirstSeenUtc + DownloadConfirmAfter)));
    }

    // Snapshot of a playback's activity, taken under the lock: the last media request it
    // made and the last bytes one of its open responses sent.
    private readonly record struct Activity(DateTimeOffset LastRequestUtc, DateTimeOffset LastBytesUtc);

    private static Activity ActivityOf(Entry entry)
    {
        var bytes = DateTimeOffset.MinValue;
        foreach (var request in entry.Requests)
        {
            if (!request.IsStale && request.LastActivityUtc > bytes)
            {
                bytes = request.LastActivityUtc;
            }
        }

        return new Activity(entry.LastSeenUtc, bytes);
    }

    private static bool SafeIsTranscoding(Func<PlaybackRef, bool> isTranscoding, PlaybackRef playback)
    {
        try
        {
            return isTranscoding(playback);
        }
        catch (Exception)
        {
            // Unknown: never refuse a stream on a failed lookup.
            return false;
        }
    }

    private static bool IsLive(Entry entry, DateTimeOffset now)
    {
        if (now - entry.LastSeenUtc <= RecentRequestWindow)
        {
            return true;
        }

        foreach (var request in entry.Requests)
        {
            if (!request.IsStale && now - request.LastActivityUtc <= StreamSlotTracker.ActiveResponseWindow)
            {
                return true;
            }
        }

        return false;
    }

    private static void Prune(Dictionary<string, Entry> entries, DateTimeOffset now)
    {
        foreach (var entry in entries.Values)
        {
            entry.Requests.RemoveWhere(r => r.IsStale);
        }

        RemoveWhere(entries, e => e.Requests.Count == 0 && now - e.LastSeenUtc > ForgetAfter);
    }

    private static void RemoveWhere(Dictionary<string, Entry> entries, Func<Entry, bool> predicate)
    {
        foreach (var key in entries.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList())
        {
            entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public Entry(string deviceId, string? playSessionId, string? itemId, DateTimeOffset now)
        {
            DeviceId = deviceId;
            PlaySessionId = playSessionId;
            ItemId = itemId;
            FirstSeenUtc = now;
            LastSeenUtc = now;
        }

        public string DeviceId { get; }

        public string? PlaySessionId { get; }

        public string? ItemId { get; }

        public DateTimeOffset FirstSeenUtc { get; }

        public DateTimeOffset LastSeenUtc { get; set; }

        public bool IsVideo { get; set; }

        public PlaybackDenialReason? DeniedReason { get; set; }

        public DateTimeOffset DeniedAtUtc { get; set; }

        // Open media responses of this playback (reference equality).
        public HashSet<StreamSlotTracker.MediaRequest> Requests { get; } = new();
    }
}

/// <summary>
/// What the tracker knows about a playback after one of its requests.
/// </summary>
/// <param name="IsNew">Whether this request started a playback not seen before.</param>
/// <param name="DeniedReason">Why the playback was refused, or null when it is allowed.</param>
public sealed record PlaybackObservation(bool IsNew, PlaybackDenialReason? DeniedReason);

/// <summary>
/// A point-in-time copy of one playback, for the admin live view.
/// </summary>
/// <param name="UserId">The user.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="PlaySessionId">The PlaySessionId, or null for a playback keyed by item.</param>
/// <param name="ItemId">The item, when known.</param>
/// <param name="IsVideo">Whether the playback uses a video endpoint that can run ffmpeg.</param>
/// <param name="StartedUtc">Its first media request.</param>
/// <param name="LastActivityUtc">Its last media request or last bytes sent.</param>
/// <param name="DeniedReason">Why it was refused, or null when it is allowed.</param>
public sealed record PlaybackSnapshot(
    Guid UserId,
    string DeviceId,
    string? PlaySessionId,
    string? ItemId,
    bool IsVideo,
    DateTimeOffset StartedUtc,
    DateTimeOffset LastActivityUtc,
    PlaybackDenialReason? DeniedReason);

/// <summary>
/// A playback as passed to the transcode lookup.
/// </summary>
/// <param name="DeviceId">The device.</param>
/// <param name="PlaySessionId">The PlaySessionId, or null for a playback keyed by item.</param>
/// <param name="IsVideo">Whether the playback uses a video endpoint that can run ffmpeg.</param>
public sealed record PlaybackRef(string DeviceId, string? PlaySessionId, bool IsVideo);

/// <summary>
/// A playback refused by <see cref="PlaybackTracker.Enforce"/>.
/// </summary>
/// <param name="UserId">The user.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="PlaySessionId">The PlaySessionId, or null for a playback keyed by item.</param>
/// <param name="Reason">Which rule refused it.</param>
/// <param name="Active">The user's other streams (or transcodes) counted by that rule.</param>
/// <param name="Limit">The limit of that rule.</param>
/// <param name="Aborts">Aborts the playback's open responses; run outside any lock.</param>
public sealed record PlaybackDenial(
    Guid UserId,
    string DeviceId,
    string? PlaySessionId,
    PlaybackDenialReason Reason,
    int Active,
    int Limit,
    IReadOnlyList<Action> Aborts);
