using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Session;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Frees a device's gate slot as soon as its session reports playback stopped,
/// so users who properly stop a stream can start another one immediately instead
/// of waiting for the slot TTL. Also ends the device's enforced state: a stopped
/// device is no zombie.
/// </summary>
public sealed class PlaybackStopSlotReleaser : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly StreamSlotTracker _tracker;
    private readonly EnforcedDeviceRegistry _enforcedDevices;
    private readonly PlaySessionRegistry _playSessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStopSlotReleaser"/> class.
    /// </summary>
    /// <param name="tracker">The slot tracker.</param>
    /// <param name="enforcedDevices">The registry of devices the limit was enforced on.</param>
    /// <param name="playSessions">The registry of the sessions' PlaySessionIds.</param>
    public PlaybackStopSlotReleaser(StreamSlotTracker tracker, EnforcedDeviceRegistry enforcedDevices, PlaySessionRegistry playSessions)
    {
        _tracker = tracker;
        _enforcedDevices = enforcedDevices;
        _playSessions = playSessions;
    }

    /// <inheritdoc />
    public Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        var session = eventArgs.Session;
        var deviceId = eventArgs.DeviceId ?? session?.DeviceId;
        var userId = session?.UserId ?? Guid.Empty;

        if (!userId.Equals(Guid.Empty) && !string.IsNullOrEmpty(deviceId))
        {
            _tracker.ReleaseDevice(userId, deviceId);
        }

        if (session is not null && !userId.Equals(Guid.Empty))
        {
            _enforcedDevices.Clear(userId, string.IsNullOrEmpty(deviceId) ? session.Id : deviceId);
        }

        _playSessions.Forget(session?.Id);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Frees a device's gate slot when its session ends (websocket lost, logout),
/// covering clients that die without ever reporting a playback stop.
/// </summary>
public sealed class SessionEndedSlotReleaser : IEventConsumer<SessionEndedEventArgs>
{
    private readonly StreamSlotTracker _tracker;
    private readonly EnforcedDeviceRegistry _enforcedDevices;
    private readonly PlaySessionRegistry _playSessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionEndedSlotReleaser"/> class.
    /// </summary>
    /// <param name="tracker">The slot tracker.</param>
    /// <param name="enforcedDevices">The registry of devices the limit was enforced on.</param>
    /// <param name="playSessions">The registry of the sessions' PlaySessionIds.</param>
    public SessionEndedSlotReleaser(StreamSlotTracker tracker, EnforcedDeviceRegistry enforcedDevices, PlaySessionRegistry playSessions)
    {
        _tracker = tracker;
        _enforcedDevices = enforcedDevices;
        _playSessions = playSessions;
    }

    /// <inheritdoc />
    public Task OnEvent(SessionEndedEventArgs eventArgs)
    {
        var session = eventArgs.Argument;
        _playSessions.Forget(session?.Id);
        if (session is null || session.UserId.Equals(Guid.Empty))
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrEmpty(session.DeviceId))
        {
            _tracker.ReleaseDevice(session.UserId, session.DeviceId);
        }

        _enforcedDevices.Clear(session.UserId, PlayingSessions.StreamKey(session));
        return Task.CompletedTask;
    }
}
