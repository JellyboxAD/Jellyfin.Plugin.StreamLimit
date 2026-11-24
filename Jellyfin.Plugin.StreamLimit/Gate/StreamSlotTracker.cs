using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Tracks playback "slots" per user and device for the HTTP stream gate.
/// </summary>
/// <remarks>
/// <para>
/// A device holds a slot (counts as a stream) while it shows real activity: one of its media
/// responses wrote bytes within <see cref="ActiveResponseWindow"/>, it made a media request
/// within the slot TTL (HLS segments), or the session manager reports it as playing. Counting
/// distinct devices (not play sessions) means a device switching items or qualities never
/// consumes two slots, and clients that never report playback (external players) are still
/// counted through their HTTP traffic alone.
/// </para>
/// <para>
/// An open response alone does not hold a slot: half-open connections (a phone that slept or
/// lost its network), players paused on a progressive transcode and Jellyfin's HLS segment
/// waits (which ignore the client's disconnect) can keep a response open for hours without a
/// byte flowing. Such a response is kept (if it writes again, its slot is held again) but not
/// counted, and is aborted after <see cref="IdleRequestAbortAfter"/>. A response whose request
/// was aborted or completed is never counted and is dropped by the next prune.
/// </para>
/// </remarks>
public sealed class StreamSlotTracker
{
    /// <summary>
    /// How long a media response counts as live after it last wrote bytes (its start counts
    /// as its first activity, so a response waiting briefly for its first segment counts).
    /// </summary>
    public static readonly TimeSpan ActiveResponseWindow = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long an open media response may stay without writing a byte before it is aborted
    /// and dropped, so dead connections never accumulate.
    /// </summary>
    public static readonly TimeSpan IdleRequestAbortAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long without activity a slot not backed by a session is considered abandoned when
    /// another device of the user is about to be denied (a lost stop report, a closed tab).
    /// </summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _slotTtl;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, SlotState>> _slots = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamSlotTracker"/> class.
    /// </summary>
    /// <param name="clock">The time source.</param>
    /// <param name="slotTtl">How long a slot stays alive after its last observed traffic.</param>
    public StreamSlotTracker(TimeProvider? clock = null, TimeSpan? slotTtl = null)
    {
        _clock = clock ?? TimeProvider.System;
        _slotTtl = slotTtl ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Decides whether a media request from a device may proceed, and if it starts a
    /// new stream, atomically claims the slot for it.
    /// </summary>
    /// <param name="userId">The requesting user.</param>
    /// <param name="deviceId">The requesting device.</param>
    /// <param name="limit">The user's effective limit (must be &gt; 0).</param>
    /// <param name="playingDeviceIds">Device ids the session manager currently reports as playing for this user.</param>
    /// <returns>True when the request may proceed.</returns>
    public bool TryEnter(Guid userId, string deviceId, int limit, IReadOnlyCollection<string> playingDeviceIds)
    {
        var userSlots = GetUserSlots(userId);
        var now = _clock.GetUtcNow();
        var aborts = new List<Action>();
        try
        {
            lock (userSlots)
            {
                PruneUser(userSlots, now, aborts);

                // A device already streaming (held here or visible in sessions) keeps its
                // slot: quality switches and next-episode moves are not new streams. A slot
                // that is no longer held (only idle responses left) is a new stream.
                userSlots.TryGetValue(deviceId, out var existing);
                if ((existing is not null && IsHeld(existing, now))
                    || playingDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
                {
                    Admit(userSlots, deviceId, existing, now);
                    return true;
                }

                var held = CountHeld(userSlots, playingDeviceIds, now, false);
                if (held >= limit)
                {
                    // Before denying, evict slots that look abandoned (no live response, no
                    // session backing them, no traffic for a while): a lost stop report
                    // (closed browser tab) must not lock the user out for the full TTL.
                    EvictAbandoned(userSlots, playingDeviceIds, now);
                    held = CountHeld(userSlots, playingDeviceIds, now, false);
                }

                if (held >= limit)
                {
                    return false;
                }

                Admit(userSlots, deviceId, existing, now);
                return true;
            }
        }
        finally
        {
            RunAborts(aborts);
        }
    }

    /// <summary>
    /// Checks whether the user could start a stream on this device without claiming
    /// anything (used for PlaybackInfo-style pre-flight checks). Applies the same
    /// abandoned-slot rule as <see cref="TryEnter"/>, without evicting anything.
    /// </summary>
    /// <param name="userId">The requesting user.</param>
    /// <param name="deviceId">The requesting device.</param>
    /// <param name="limit">The user's effective limit (must be &gt; 0).</param>
    /// <param name="playingDeviceIds">Device ids currently playing for this user.</param>
    /// <returns>True when a stream from this device would be allowed.</returns>
    public bool WouldAllow(Guid userId, string deviceId, int limit, IReadOnlyCollection<string> playingDeviceIds)
    {
        var userSlots = GetUserSlots(userId);
        var now = _clock.GetUtcNow();
        var aborts = new List<Action>();
        try
        {
            lock (userSlots)
            {
                PruneUser(userSlots, now, aborts);

                if ((userSlots.TryGetValue(deviceId, out var existing) && IsHeld(existing, now))
                    || playingDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }

                return CountHeld(userSlots, playingDeviceIds, now, false) < limit
                       || CountHeld(userSlots, playingDeviceIds, now, true) < limit;
            }
        }
        finally
        {
            RunAborts(aborts);
        }
    }

    /// <summary>
    /// Marks the start of a media response for a device. A response holds its slot while it
    /// writes bytes (see <see cref="Touch"/>), so a long direct-play download keeps counting
    /// the whole time even though it is a single request.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="abort">Aborts the response (the gate passes <c>HttpContext.Abort</c>); used by an admin kick and the idle abort.</param>
    /// <param name="requestAborted">The request's <c>RequestAborted</c> token: once cancelled, the response no longer counts.</param>
    /// <returns>The registration of this response, to pass to <see cref="Touch"/> and <see cref="EndRequest"/>.</returns>
    public MediaRequest BeginRequest(Guid userId, string deviceId, Action? abort = null, CancellationToken requestAborted = default)
    {
        var now = _clock.GetUtcNow();
        var registration = new MediaRequest(abort, requestAborted, now);
        var userSlots = GetUserSlots(userId);
        lock (userSlots)
        {
            // Recreate the slot when missing: a playback-stop report racing the gap
            // between TryEnter and BeginRequest may have released it, and a response
            // about to stream must always be counted.
            userSlots.TryGetValue(deviceId, out var slot);
            slot = Admit(userSlots, deviceId, slot, now);
            slot.Requests.Add(registration);
        }

        return registration;
    }

    /// <summary>
    /// Creates the registration of a media response that holds no slot (the device limit is
    /// off for this user, only the per-playback rules watch it). End it with
    /// <see cref="EndDetachedRequest"/>.
    /// </summary>
    /// <param name="abort">Aborts the response.</param>
    /// <param name="requestAborted">The request's <c>RequestAborted</c> token.</param>
    /// <returns>The registration.</returns>
    public MediaRequest CreateDetachedRequest(Action? abort = null, CancellationToken requestAborted = default)
        => new(abort, requestAborted, _clock.GetUtcNow());

    /// <summary>
    /// Ends a registration made by <see cref="CreateDetachedRequest"/>. Idempotent.
    /// </summary>
    /// <param name="registration">The registration.</param>
    public static void EndDetachedRequest(MediaRequest registration) => registration.MarkCompleted();

    /// <summary>
    /// Records that a media response just wrote bytes. Lock-free; callers throttle it.
    /// </summary>
    /// <param name="registration">The registration returned by <see cref="BeginRequest"/>.</param>
    public void Touch(MediaRequest registration)
        => registration.Touch(_clock.GetUtcNow());

    /// <summary>
    /// Marks the end of a media response. Idempotent: the gate calls it from its
    /// <c>finally</c> and from <c>Response.OnCompleted</c>.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="registration">
    /// The registration returned by <see cref="BeginRequest"/>. Only that response is ended,
    /// so a response aborted by a kick cannot end another response of a newer slot. When
    /// omitted, one open response of the device is ended.
    /// </param>
    public void EndRequest(Guid userId, string deviceId, MediaRequest? registration = null)
    {
        if (registration is not null && !registration.MarkCompleted())
        {
            return;
        }

        if (!_slots.TryGetValue(userId, out var userSlots))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        lock (userSlots)
        {
            if (!userSlots.TryGetValue(deviceId, out var slot))
            {
                return;
            }

            if (registration is null)
            {
                registration = slot.Requests.FirstOrDefault(r => !r.IsCompleted) ?? slot.Requests.FirstOrDefault();
                if (registration is null)
                {
                    return;
                }

                registration.MarkCompleted();
            }

            if (!slot.Requests.Remove(registration))
            {
                // Belongs to a slot that was kicked and since recreated, or was dropped as
                // idle: not ours to touch.
                return;
            }

            // The end of a response is traffic, unless the slot was released meanwhile (stop
            // report): then only the response's own last write counts, so the tail of a
            // stopped stream does not hold the slot for another TTL.
            var seen = slot.ReleasedAtUtc == DateTimeOffset.MinValue ? now : registration.LastActivityUtc;
            if (seen > slot.LastSeenUtc)
            {
                slot.LastSeenUtc = seen;
            }
        }
    }

    /// <summary>
    /// Kicks a device right away (admin action): aborts its open media responses and
    /// removes its slot, even while responses are streaming.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <returns>
    /// The number of live responses aborted, the number of stale registrations cleared
    /// (requests already completed or aborted: nothing to abort), and whether a slot was removed.
    /// </returns>
    public (int Aborted, int Stale, bool Released) AbortDevice(Guid userId, string deviceId)
    {
        if (!_slots.TryGetValue(userId, out var userSlots))
        {
            return (0, 0, false);
        }

        List<MediaRequest> requests;
        lock (userSlots)
        {
            if (!userSlots.TryRemove(deviceId, out var slot))
            {
                return (0, 0, false);
            }

            requests = slot.Requests.ToList();
            slot.Requests.Clear();
        }

        var (aborted, stale) = AbortAll(requests);
        return (aborted, stale, true);
    }

    /// <summary>
    /// Releases every slot of a user (admin "reset" action): aborts their open media
    /// responses and forgets the slots.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The number of slots released, live responses aborted and stale registrations cleared.</returns>
    public (int Released, int Aborted, int Stale) ResetUser(Guid userId)
    {
        if (!_slots.TryGetValue(userId, out var userSlots))
        {
            return (0, 0, 0);
        }

        var requests = new List<MediaRequest>();
        int released;
        lock (userSlots)
        {
            released = userSlots.Count;
            foreach (var slot in userSlots.Values)
            {
                requests.AddRange(slot.Requests);
                slot.Requests.Clear();
            }

            userSlots.Clear();
        }

        var (aborted, stale) = AbortAll(requests);
        return (released, aborted, stale);
    }

    /// <summary>
    /// Releases the slot of a device, typically when its session reports playback
    /// stopped, so the slot frees up immediately instead of waiting for the TTL.
    /// </summary>
    /// <remarks>
    /// A slot with a live response (bytes written recently) is kept: a stop report can race
    /// the tail of a download. Idle open responses do not keep it: the slot stops counting at
    /// once (the slot object stays while they are open, so a response that writes again is
    /// counted again).
    /// </remarks>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <returns>True when a counted slot was freed.</returns>
    public bool ReleaseDevice(Guid userId, string deviceId)
    {
        if (!_slots.TryGetValue(userId, out var userSlots))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        var aborts = new List<Action>();
        try
        {
            lock (userSlots)
            {
                PruneUser(userSlots, now, aborts);
                if (!userSlots.TryGetValue(deviceId, out var slot) || HasLiveRequest(slot, now))
                {
                    return false;
                }

                if (slot.Requests.Count == 0)
                {
                    return userSlots.TryRemove(deviceId, out _);
                }

                var wasHeld = IsHeld(slot, now);
                Release(slot, now);
                return wasHeld;
            }
        }
        finally
        {
            RunAborts(aborts);
        }
    }

    /// <summary>
    /// Gets the devices currently holding one of the user's streams: held slots plus the
    /// devices the session manager reports as playing (what the limit is checked against).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="playingDeviceIds">Device ids currently playing for this user.</param>
    /// <returns>The distinct device ids (case-insensitive).</returns>
    public IReadOnlyCollection<string> GetHeldDevices(Guid userId, IReadOnlyCollection<string> playingDeviceIds)
    {
        var devices = new HashSet<string>(playingDeviceIds, StringComparer.OrdinalIgnoreCase);
        if (_slots.TryGetValue(userId, out var userSlots))
        {
            var now = _clock.GetUtcNow();
            var aborts = new List<Action>();
            lock (userSlots)
            {
                PruneUser(userSlots, now, aborts);
                foreach (var (deviceId, slot) in userSlots)
                {
                    if (IsHeld(slot, now))
                    {
                        devices.Add(deviceId);
                    }
                }
            }

            RunAborts(aborts);
        }

        return devices;
    }

    /// <summary>
    /// Gets a snapshot of every slot, for the admin live view. Includes slots that are no
    /// longer held but still have open (idle) responses; see <see cref="SlotSnapshot.Held"/>.
    /// </summary>
    /// <returns>The slots of all users.</returns>
    public IReadOnlyList<SlotSnapshot> GetSnapshot()
    {
        var now = _clock.GetUtcNow();
        var result = new List<SlotSnapshot>();
        var aborts = new List<Action>();
        foreach (var (userId, userSlots) in _slots)
        {
            lock (userSlots)
            {
                PruneUser(userSlots, now, aborts);
                foreach (var (deviceId, slot) in userSlots)
                {
                    result.Add(new SlotSnapshot(
                        userId,
                        deviceId,
                        LastActivity(slot),
                        slot.Requests.Count,
                        slot.Requests.Count(r => IsLive(slot, r, now)),
                        IsRecent(slot, now)));
                }
            }
        }

        RunAborts(aborts);
        return result;
    }

    /// <summary>
    /// Prunes every user's slots: drops completed or aborted registrations, aborts responses
    /// idle for longer than <see cref="IdleRequestAbortAfter"/>, and removes slots that are no
    /// longer held and have no open response. Run periodically, so memory and the live view
    /// stay clean even for users who never come back.
    /// </summary>
    /// <returns>The number of idle responses aborted.</returns>
    public int Sweep()
    {
        var now = _clock.GetUtcNow();
        var aborts = new List<Action>();
        foreach (var userSlots in _slots.Values)
        {
            lock (userSlots)
            {
                PruneUser(userSlots, now, aborts);
            }
        }

        RunAborts(aborts);
        return aborts.Count;
    }

    private ConcurrentDictionary<string, SlotState> GetUserSlots(Guid userId)
        => _slots.GetOrAdd(userId, static _ => new ConcurrentDictionary<string, SlotState>(StringComparer.OrdinalIgnoreCase));

    private static SlotState Admit(ConcurrentDictionary<string, SlotState> userSlots, string deviceId, SlotState? slot, DateTimeOffset now)
    {
        if (slot is null)
        {
            slot = new SlotState();
            userSlots[deviceId] = slot;
        }

        slot.LastSeenUtc = now;
        slot.ReleasedAtUtc = DateTimeOffset.MinValue;
        return slot;
    }

    private static void Release(SlotState slot, DateTimeOffset now)
    {
        // Activity up to now no longer counts; any later write or request makes it count again.
        var last = LastActivity(slot);
        slot.ReleasedAtUtc = last > now ? last : now;
    }

    private static DateTimeOffset LastActivity(SlotState slot)
    {
        var last = slot.LastSeenUtc;
        foreach (var request in slot.Requests)
        {
            var activity = request.LastActivityUtc;
            if (activity > last)
            {
                last = activity;
            }
        }

        return last;
    }

    private static bool IsLive(SlotState slot, MediaRequest request, DateTimeOffset now)
    {
        if (request.IsStale)
        {
            return false;
        }

        var activity = request.LastActivityUtc;
        return activity > slot.ReleasedAtUtc && now - activity <= ActiveResponseWindow;
    }

    private static bool HasLiveRequest(SlotState slot, DateTimeOffset now)
    {
        foreach (var request in slot.Requests)
        {
            if (IsLive(slot, request, now))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsRecent(SlotState slot, DateTimeOffset now)
    {
        var last = LastActivity(slot);
        return last > slot.ReleasedAtUtc && now - last <= _slotTtl;
    }

    private bool IsHeld(SlotState slot, DateTimeOffset now)
        => HasLiveRequest(slot, now) || IsRecent(slot, now);

    private static bool IsAbandoned(string deviceId, SlotState slot, IReadOnlyCollection<string> playingDeviceIds, DateTimeOffset now)
        => !HasLiveRequest(slot, now)
           && now - LastActivity(slot) > AbandonedAfter
           && !playingDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase);

    private int CountHeld(
        ConcurrentDictionary<string, SlotState> userSlots,
        IReadOnlyCollection<string> playingDeviceIds,
        DateTimeOffset now,
        bool excludeAbandoned)
    {
        var devices = new HashSet<string>(playingDeviceIds, StringComparer.OrdinalIgnoreCase);
        foreach (var (deviceId, slot) in userSlots)
        {
            if (IsHeld(slot, now) && !(excludeAbandoned && IsAbandoned(deviceId, slot, playingDeviceIds, now)))
            {
                devices.Add(deviceId);
            }
        }

        return devices.Count;
    }

    private static void EvictAbandoned(ConcurrentDictionary<string, SlotState> userSlots, IReadOnlyCollection<string> playingDeviceIds, DateTimeOffset now)
    {
        foreach (var (deviceId, slot) in userSlots)
        {
            if (!IsAbandoned(deviceId, slot, playingDeviceIds, now))
            {
                continue;
            }

            if (slot.Requests.Count == 0)
            {
                userSlots.TryRemove(deviceId, out _);
            }
            else
            {
                Release(slot, now);
            }
        }
    }

    private void PruneUser(ConcurrentDictionary<string, SlotState> userSlots, DateTimeOffset now, List<Action> aborts)
    {
        foreach (var (deviceId, slot) in userSlots)
        {
            if (slot.Requests.Count > 0)
            {
                slot.Requests.RemoveWhere(request =>
                {
                    if (request.IsStale)
                    {
                        // Completed (EndRequest missed) or aborted by the client: never counted
                        // again. Its last write still counts as the slot's traffic.
                        if (slot.ReleasedAtUtc == DateTimeOffset.MinValue && request.LastActivityUtc > slot.LastSeenUtc)
                        {
                            slot.LastSeenUtc = request.LastActivityUtc;
                        }

                        return true;
                    }

                    if (now - request.LastActivityUtc > IdleRequestAbortAfter)
                    {
                        // A dead connection (or a wait that will never end): cut it so it
                        // cannot accumulate. Aborted outside the lock by the caller.
                        if (request.Abort is not null)
                        {
                            aborts.Add(request.Abort);
                        }

                        request.MarkCompleted();
                        return true;
                    }

                    return false;
                });
            }

            if (slot.Requests.Count == 0 && !IsRecent(slot, now))
            {
                userSlots.TryRemove(deviceId, out _);
            }
        }
    }

    private static (int Aborted, int Stale) AbortAll(IEnumerable<MediaRequest> requests)
    {
        var aborts = new List<Action>();
        var stale = 0;
        foreach (var request in requests)
        {
            // A completed request's HttpContext may already serve another request on the same
            // connection: never abort it.
            if (request.IsStale || request.Abort is null)
            {
                stale += request.IsStale ? 1 : 0;
                continue;
            }

            aborts.Add(request.Abort);
        }

        return (RunAborts(aborts), stale);
    }

    private static int RunAborts(List<Action> aborts)
    {
        // Outside the lock: aborting runs connection code. It is safe from any thread, and
        // a failure must never reach the caller (or the aborted request's pipeline).
        var aborted = 0;
        foreach (var abort in aborts)
        {
            try
            {
                abort();
                aborted++;
            }
            catch (Exception)
            {
                // The response may already be completing; nothing left to abort.
            }
        }

        return aborted;
    }

    /// <summary>
    /// A point-in-time copy of one slot.
    /// </summary>
    /// <param name="UserId">The user.</param>
    /// <param name="DeviceId">The device.</param>
    /// <param name="LastSeenUtc">Last observed activity (request start or end, bytes written).</param>
    /// <param name="InFlight">Media responses currently open, idle ones included.</param>
    /// <param name="LiveRequests">Open responses that wrote bytes within <see cref="ActiveResponseWindow"/>.</param>
    /// <param name="Recent">Whether the device made a media request within the slot TTL.</param>
    public sealed record SlotSnapshot(Guid UserId, string DeviceId, DateTimeOffset LastSeenUtc, int InFlight, int LiveRequests = 0, bool Recent = true)
    {
        /// <summary>
        /// Gets a value indicating whether the slot is counted from its own traffic (a live
        /// response or a recent request); a playing session counts the device too.
        /// </summary>
        public bool Held => LiveRequests > 0 || Recent;
    }

    /// <summary>
    /// One media response registered with the tracker.
    /// </summary>
    public sealed class MediaRequest
    {
        private long _lastActivityTicks;
        private int _completed;

        internal MediaRequest(Action? abort, CancellationToken requestAborted, DateTimeOffset now)
        {
            Abort = abort;
            RequestAborted = requestAborted;
            _lastActivityTicks = now.UtcTicks;
        }

        /// <summary>
        /// Gets the time the response last wrote bytes (its start when it has not yet).
        /// </summary>
        public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

        /// <summary>
        /// Gets a value indicating whether the response ended.
        /// </summary>
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;

        internal Action? Abort { get; }

        internal CancellationToken RequestAborted { get; }

        // Completed, or the client went away: never counted, never aborted.
        internal bool IsStale => IsCompleted || RequestAborted.IsCancellationRequested;

        internal void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastActivityTicks, now.UtcTicks);

        internal bool MarkCompleted() => Interlocked.Exchange(ref _completed, 1) == 0;
    }

    private sealed class SlotState
    {
        // Last admission, request start or request end.
        public DateTimeOffset LastSeenUtc { get; set; }

        // Set by a stop report or the abandoned-slot rule while idle responses are still open:
        // activity up to this instant no longer counts. MinValue when not released.
        public DateTimeOffset ReleasedAtUtc { get; set; } = DateTimeOffset.MinValue;

        // Open media responses (reference equality).
        public HashSet<MediaRequest> Requests { get; } = new();
    }
}
