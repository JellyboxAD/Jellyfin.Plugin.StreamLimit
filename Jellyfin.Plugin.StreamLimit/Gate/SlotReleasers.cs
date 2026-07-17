using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Session;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Frees a device's gate slot as soon as its session reports playback stopped,
/// so users who properly stop a stream can start another one immediately instead
/// of waiting for the slot TTL.
/// </summary>
public sealed class PlaybackStopSlotReleaser : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly StreamSlotTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStopSlotReleaser"/> class.
    /// </summary>
    /// <param name="tracker">The slot tracker.</param>
    public PlaybackStopSlotReleaser(StreamSlotTracker tracker)
    {
        _tracker = tracker;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionEndedSlotReleaser"/> class.
    /// </summary>
    /// <param name="tracker">The slot tracker.</param>
    public SessionEndedSlotReleaser(StreamSlotTracker tracker)
    {
        _tracker = tracker;
    }

    /// <inheritdoc />
    public Task OnEvent(SessionEndedEventArgs eventArgs)
    {
        var session = eventArgs.Argument;
        if (session is not null && !session.UserId.Equals(Guid.Empty) && !string.IsNullOrEmpty(session.DeviceId))
        {
            _tracker.ReleaseDevice(session.UserId, session.DeviceId);
        }

        return Task.CompletedTask;
    }
}
