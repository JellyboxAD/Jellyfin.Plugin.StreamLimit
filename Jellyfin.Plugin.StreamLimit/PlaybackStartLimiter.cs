using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamLimit.Gate;
using Jellyfin.Plugin.StreamLimit.Notifications;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// Enforces per-user concurrent stream limits when playback starts.
/// </summary>
/// <remarks>
/// Enforcement is layered so it also works for clients that ignore remote
/// stop commands (Infuse, external players, some TV clients):
/// 1. a Stop playstate command (well-behaved clients), plus a server-side playback
///    stop for sessions that cannot receive commands,
/// 2. killing the server-side transcode job (clients that ignore commands),
/// 3. optionally revoking the device token (cuts direct-play HTTP streams too).
/// The stopped device is then recorded in the <see cref="EnforcedDeviceRegistry"/>, so a
/// client that keeps its session alive afterwards does not keep holding a stream.
/// </remarks>
public sealed class PlaybackStartLimiter : IEventConsumer<PlaybackStartEventArgs>
{
    private static int _taskCounter;

    private readonly ISessionManager _sessionManager;
    private readonly StreamLimitManager _limitManager;
    private readonly EnforcedDeviceRegistry _enforcedDevices;
    private readonly StreamSlotTracker _tracker;
    private readonly PlaySessionRegistry _playSessions;
    private readonly StreamEnforcer _enforcer;
    private readonly BlockActivityLogger _activityLogger;
    private readonly WebhookNotifier _webhookNotifier;
    private readonly ILogger<PlaybackStartLimiter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStartLimiter"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="limitManager">The stream limit manager.</param>
    /// <param name="enforcedDevices">The registry of devices the limit was enforced on.</param>
    /// <param name="tracker">The gate's slot tracker (for the other devices of the message).</param>
    /// <param name="playSessions">The registry of the sessions' PlaySessionIds.</param>
    /// <param name="enforcer">The shared stop actions.</param>
    /// <param name="activityLogger">The activity log writer for blocked streams.</param>
    /// <param name="webhookNotifier">The webhook notifier.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackStartLimiter(
        ISessionManager sessionManager,
        StreamLimitManager limitManager,
        EnforcedDeviceRegistry enforcedDevices,
        StreamSlotTracker tracker,
        PlaySessionRegistry playSessions,
        StreamEnforcer enforcer,
        BlockActivityLogger activityLogger,
        WebhookNotifier webhookNotifier,
        ILogger<PlaybackStartLimiter> logger)
    {
        _sessionManager = sessionManager;
        _limitManager = limitManager;
        _enforcedDevices = enforcedDevices;
        _tracker = tracker;
        _playSessions = playSessions;
        _enforcer = enforcer;
        _activityLogger = activityLogger;
        _webhookNotifier = webhookNotifier;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The event publisher awaits consumers inline from the HTTP request that
    /// reported playback, so this method only takes a cheap decision and offloads
    /// the actual (slow, delay-containing) enforcement to a detached task. All
    /// captured services are singletons, safe to use after the DI scope ends.
    /// </remarks>
    public Task OnEvent(PlaybackStartEventArgs eventArgs)
    {
        var session = eventArgs.Session;
        if (session is null || session.UserId.Equals(Guid.Empty))
        {
            return Task.CompletedTask;
        }

        // Kept for every playback: the admin live view needs it to kill only this
        // playback's transcode job (SessionInfo does not expose it).
        _playSessions.Remember(session.Id, eventArgs.PlaySessionId);

        var userId = session.UserId;
        var maxStreamsAllowed = _limitManager.GetEffectiveLimit(userId);
        if (maxStreamsAllowed <= 0)
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(() => CheckAndEnforceAsync(eventArgs, session, userId, maxStreamsAllowed));
        return Task.CompletedTask;
    }

    private async Task CheckAndEnforceAsync(PlaybackStartEventArgs eventArgs, SessionInfo session, Guid userId, int maxStreamsAllowed)
    {
        var taskNumber = Interlocked.Increment(ref _taskCounter);

        // Serialize the count-then-stop decision per user so two near-simultaneous
        // starts cannot both slip under the limit.
        var userLock = _limitManager.GetUserEnforcementLock(userId);
        await userLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Same definition as the HTTP gate: distinct devices with a recent playback
            // check-in, so a crashed client's ghost session cannot get the user's new,
            // legitimate stream killed. The starting session always counts. Trade-off: a
            // client that stops reporting progress for 3+ minutes is no longer counted here;
            // the HTTP gate (traffic-based) still covers it when the hard block is on.
            // Devices we already stopped don't count either, whatever their client keeps
            // reporting (see EnforcedDeviceRegistry).
            var startingKey = PlayingSessions.StreamKey(session);
            var playingStreams = PlayingSessions.GetPlayingStreamKeys(
                _sessionManager.Sessions,
                userId,
                DateTime.UtcNow,
                s => _enforcedDevices.IsEnforced(userId, PlayingSessions.StreamKey(s)));
            playingStreams.Add(startingKey);
            var activeStreamsForUser = playingStreams.Count;

            _logger.LogInformation(
                "[{TaskNumber}] Playback started. User: {UserId}, active streams: {ActiveStreams}, limit: {MaxStreams}",
                taskNumber,
                userId,
                activeStreamsForUser,
                maxStreamsAllowed);

            if (activeStreamsForUser <= maxStreamsAllowed)
            {
                // A legitimate start: if this device was stopped earlier, it counts again.
                _enforcedDevices.Clear(userId, startingKey);
                return;
            }

            // The other devices of the message: the counted sessions plus the gate's slots.
            var otherDevices = playingStreams
                .Concat(_tracker.GetHeldDevices(userId, Array.Empty<string>()))
                .Where(key => !string.Equals(key, startingKey, StringComparison.OrdinalIgnoreCase));
            var userSessions = _sessionManager.Sessions.Where(s => s.UserId.Equals(userId)).ToList();

            var blocked = new BlockedStream(
                userId,
                session.UserName,
                startingKey,
                session.DeviceName,
                session.Client,
                session.RemoteEndPoint,
                maxStreamsAllowed,
                "playback start limiter",
                activeStreamsForUser - 1,
                MessageTemplate.DescribeDevices(otherDevices, userSessions));

            await LimitPlayback(eventArgs, session, blocked, taskNumber).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TaskNumber}] Stream limit enforcement failed", taskNumber);
        }
        finally
        {
            userLock.Release();
        }
    }

    private async Task LimitPlayback(PlaybackStartEventArgs eventArgs, SessionInfo session, BlockedStream blocked, int taskNumber)
    {
        var context = taskNumber.ToString(CultureInfo.InvariantCulture);
        _logger.LogInformation(
            "[{TaskNumber}] Limit exceeded. Stopping playback on session {SessionId} (client: {Client}, device: {Device})",
            taskNumber,
            session.Id,
            session.Client,
            session.DeviceName);

        _enforcedDevices.Record(session.UserId, blocked.DeviceId);
        _activityLogger.LogBlocked(blocked);
        _webhookNotifier.NotifyBlocked(blocked);

        await _enforcer.StopAsync(session, eventArgs.PlaySessionId, eventArgs.MediaSourceId, context).ConfigureAwait(false);

        // Recorded again: our own server-side stop publishes a playback-stopped event
        // whose releaser clears the entry, while the client may well keep its session
        // alive afterwards (the very zombie the registry exists for).
        _enforcedDevices.Record(session.UserId, blocked.DeviceId);

        if (Plugin.Instance?.Configuration.KillTranscodeJobs == true)
        {
            await _enforcer.KillTranscodeJobsAsync(session.DeviceId, eventArgs.PlaySessionId, context).ConfigureAwait(false);
        }

        // Give the client a moment to process the stop before showing the message,
        // otherwise some clients drop the dialog during the screen transition.
        await Task.Delay(500).ConfigureAwait(false);

        // Off by default: the stop command already makes the client surface its own
        // state; a custom popup is redundant. See PluginConfiguration.ShowLimitPopup.
        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.ShowLimitPopup == true)
        {
            await _enforcer.SendMessageAsync(
                session,
                MessageTemplate.Render(configuration.ResolvedMessageTitle, blocked.MessageValues),
                MessageTemplate.Render(configuration.ResolvedMessageText, blocked.MessageValues),
                context).ConfigureAwait(false);
        }

        if (configuration?.ForceLogoutOnLimit == true)
        {
            await _enforcer.LogoutDeviceAsync(session.UserId, session.DeviceId, context).ConfigureAwait(false);
        }

        _logger.LogInformation("[{TaskNumber}] Stream limit enforced for session {SessionId}", taskNumber, session.Id);
    }
}
