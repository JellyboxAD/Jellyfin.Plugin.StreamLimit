using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Tracks playback "slots" per user and device for the HTTP stream gate.
/// </summary>
/// <remarks>
/// A device holds a slot while it has an HTTP media response in flight, while it
/// requested media bytes recently (HLS segments), or while the session manager
/// reports it as playing. Counting distinct devices (not play sessions) means a
/// device switching items or qualities never consumes two slots, and clients that
/// never report playback (external players) are still counted through their HTTP
/// traffic alone.
/// </remarks>
public sealed class StreamSlotTracker
{
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
        var userSlots = _slots.GetOrAdd(userId, static _ => new ConcurrentDictionary<string, SlotState>(StringComparer.OrdinalIgnoreCase));
        var now = _clock.GetUtcNow();

        lock (userSlots)
        {
            PruneUser(userSlots, now);

            // A device already streaming (tracked here or visible in sessions) keeps
            // its slot: quality switches and next-episode moves are not new streams.
            if (userSlots.TryGetValue(deviceId, out var existing))
            {
                existing.LastSeenUtc = now;
                return true;
            }

            if (playingDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
            {
                userSlots[deviceId] = new SlotState { LastSeenUtc = now };
                return true;
            }

            var held = CountHeldSlots(userSlots, playingDeviceIds);
            if (held >= limit)
            {
                // Before denying, evict slots that look abandoned (no response in
                // flight, no session backing them, no traffic for a while): a lost
                // stop report (closed browser tab) must not lock the user out for
                // the full TTL.
                EvictAbandoned(userSlots, playingDeviceIds, now);
                held = CountHeldSlots(userSlots, playingDeviceIds);
            }

            if (held >= limit)
            {
                return false;
            }

            userSlots[deviceId] = new SlotState { LastSeenUtc = now };
            return true;
        }
    }

    /// <summary>
    /// Checks whether the user could start a stream on this device without claiming
    /// anything (used for PlaybackInfo-style pre-flight checks).
    /// </summary>
    /// <param name="userId">The requesting user.</param>
    /// <param name="deviceId">The requesting device.</param>
    /// <param name="limit">The user's effective limit (must be &gt; 0).</param>
    /// <param name="playingDeviceIds">Device ids currently playing for this user.</param>
    /// <returns>True when a stream from this device would be allowed.</returns>
    public bool WouldAllow(Guid userId, string deviceId, int limit, IReadOnlyCollection<string> playingDeviceIds)
    {
        var userSlots = _slots.GetOrAdd(userId, static _ => new ConcurrentDictionary<string, SlotState>(StringComparer.OrdinalIgnoreCase));
        var now = _clock.GetUtcNow();

        lock (userSlots)
        {
            PruneUser(userSlots, now);

            if (userSlots.ContainsKey(deviceId)
                || playingDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            return CountHeldSlots(userSlots, playingDeviceIds) < limit;
        }
    }

    /// <summary>
    /// Marks the start of an in-flight media response for a device. Long-running
    /// direct-play downloads hold their slot for the whole response lifetime even
    /// though they issue a single request.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    public void BeginRequest(Guid userId, string deviceId)
    {
        var userSlots = _slots.GetOrAdd(userId, static _ => new ConcurrentDictionary<string, SlotState>(StringComparer.OrdinalIgnoreCase));
        lock (userSlots)
        {
            // Recreate the slot when missing: a playback-stop report racing the gap
            // between TryEnter and BeginRequest may have released it, and a response
            // about to stream must always be counted.
            if (!userSlots.TryGetValue(deviceId, out var slot))
            {
                slot = new SlotState();
                userSlots[deviceId] = slot;
            }

            slot.InFlight++;
            slot.LastSeenUtc = _clock.GetUtcNow();
        }
    }

    /// <summary>
    /// Marks the end of an in-flight media response.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    public void EndRequest(Guid userId, string deviceId)
    {
        if (!_slots.TryGetValue(userId, out var userSlots))
        {
            return;
        }

        lock (userSlots)
        {
            if (userSlots.TryGetValue(deviceId, out var slot))
            {
                slot.InFlight = Math.Max(0, slot.InFlight - 1);
                slot.LastSeenUtc = _clock.GetUtcNow();
            }
        }
    }

    /// <summary>
    /// Releases the slot of a device, typically when its session reports playback
    /// stopped, so the slot frees up immediately instead of waiting for the TTL.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    public void ReleaseDevice(Guid userId, string deviceId)
    {
        if (!_slots.TryGetValue(userId, out var userSlots))
        {
            return;
        }

        lock (userSlots)
        {
            // Keep slots with responses still streaming: a stop report can race the
            // tail of a download.
            if (userSlots.TryGetValue(deviceId, out var slot) && slot.InFlight == 0)
            {
                userSlots.TryRemove(deviceId, out _);
            }
        }
    }

    private static int CountHeldSlots(ConcurrentDictionary<string, SlotState> userSlots, IReadOnlyCollection<string> playingDeviceIds)
    {
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deviceId in userSlots.Keys)
        {
            devices.Add(deviceId);
        }

        foreach (var deviceId in playingDeviceIds)
        {
            devices.Add(deviceId);
        }

        return devices.Count;
    }

    private static void EvictAbandoned(ConcurrentDictionary<string, SlotState> userSlots, IReadOnlyCollection<string> playingDeviceIds, DateTimeOffset now)
    {
        var abandonedAfter = TimeSpan.FromSeconds(15);
        foreach (var (deviceId, slot) in userSlots)
        {
            if (slot.InFlight <= 0
                && now - slot.LastSeenUtc > abandonedAfter
                && !playingDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
            {
                userSlots.TryRemove(deviceId, out _);
            }
        }
    }

    private void PruneUser(ConcurrentDictionary<string, SlotState> userSlots, DateTimeOffset now)
    {
        foreach (var (deviceId, slot) in userSlots)
        {
            if (slot.InFlight <= 0 && now - slot.LastSeenUtc > _slotTtl)
            {
                userSlots.TryRemove(deviceId, out _);
            }
        }
    }

    private sealed class SlotState
    {
        public DateTimeOffset LastSeenUtc { get; set; }

        public int InFlight { get; set; }
    }
}
