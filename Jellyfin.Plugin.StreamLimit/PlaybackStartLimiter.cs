using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.StreamLimit.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// Enforces per-user concurrent stream limits when playback starts.
/// </summary>
/// <remarks>
/// Enforcement is layered so it also works for clients that ignore remote
/// stop commands (Infuse, external players, some TV clients):
/// 1. a Stop playstate command (well-behaved clients),
/// 2. killing the server-side transcode job (clients that ignore commands),
/// 3. optionally revoking the device token (cuts direct-play HTTP streams too).
/// </remarks>
public sealed class PlaybackStartLimiter : IEventConsumer<PlaybackStartEventArgs>
{
    private static int _taskCounter;

    private readonly ISessionManager _sessionManager;
    private readonly ITranscodeManager _transcodeManager;
    private readonly IDeviceManager _deviceManager;
    private readonly StreamLimitManager _limitManager;
    private readonly ILogger<PlaybackStartLimiter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStartLimiter"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="deviceManager">Instance of the <see cref="IDeviceManager"/> interface.</param>
    /// <param name="limitManager">The stream limit manager.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackStartLimiter(
        ISessionManager sessionManager,
        ITranscodeManager transcodeManager,
        IDeviceManager deviceManager,
        StreamLimitManager limitManager,
        ILogger<PlaybackStartLimiter> logger)
    {
        _sessionManager = sessionManager;
        _transcodeManager = transcodeManager;
        _deviceManager = deviceManager;
        _limitManager = limitManager;
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
            var activeStreamsForUser = _sessionManager.Sessions.Count(s =>
                s.UserId.Equals(userId)
                && s.NowPlayingItem is not null
                && s.IsActive);

            _logger.LogInformation(
                "[{TaskNumber}] Playback started. User: {UserId}, active streams: {ActiveStreams}, limit: {MaxStreams}",
                taskNumber,
                userId,
                activeStreamsForUser,
                maxStreamsAllowed);

            if (activeStreamsForUser <= maxStreamsAllowed)
            {
                return;
            }

            await LimitPlayback(eventArgs, session, taskNumber).ConfigureAwait(false);
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

    private async Task LimitPlayback(PlaybackStartEventArgs eventArgs, SessionInfo session, int taskNumber)
    {
        _logger.LogInformation(
            "[{TaskNumber}] Limit exceeded. Stopping playback on session {SessionId} (client: {Client}, device: {Device})",
            taskNumber,
            session.Id,
            session.Client,
            session.DeviceName);

        await StopPlayback(session, taskNumber).ConfigureAwait(false);
        await KillTranscodeJobs(eventArgs, session, taskNumber).ConfigureAwait(false);

        // Give the client a moment to process the stop before showing the message,
        // otherwise some clients drop the dialog during the screen transition.
        await Task.Delay(500).ConfigureAwait(false);
        await ShowLimitMessage(session, taskNumber).ConfigureAwait(false);
        await LogoutDevice(session, taskNumber).ConfigureAwait(false);

        _logger.LogInformation("[{TaskNumber}] Stream limit enforced for session {SessionId}", taskNumber, session.Id);
    }

    private async Task StopPlayback(SessionInfo session, int taskNumber)
    {
        try
        {
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest
                {
                    Command = PlaystateCommand.Stop,
                    ControllingUserId = StreamLimitStore.NormalizeUserKey(session.UserId),
                    SeekPositionTicks = 0,
                },
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("[{TaskNumber}] Stop command sent", taskNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{TaskNumber}] Failed to send stop command", taskNumber);
        }
    }

    private async Task KillTranscodeJobs(PlaybackStartEventArgs eventArgs, SessionInfo session, int taskNumber)
    {
        if (Plugin.Instance?.Configuration.KillTranscodeJobs != true)
        {
            return;
        }

        if (string.IsNullOrEmpty(session.DeviceId))
        {
            return;
        }

        try
        {
            // Kills the ffmpeg process feeding the offending stream. Clients that
            // ignore the stop command lose their source and stop on their own.
            await _transcodeManager.KillTranscodingJobs(
                session.DeviceId,
                eventArgs.PlaySessionId,
                _ => true).ConfigureAwait(false);

            _logger.LogInformation("[{TaskNumber}] Transcode jobs killed for device {DeviceId}", taskNumber, session.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{TaskNumber}] Failed to kill transcode jobs", taskNumber);
        }
    }

    private async Task ShowLimitMessage(SessionInfo session, int taskNumber)
    {
        var configuration = Plugin.Instance?.Configuration;

        // Off by default: the stop command already makes the client surface its own
        // state; a custom popup is redundant. See PluginConfiguration.ShowLimitPopup.
        if (configuration?.ShowLimitPopup != true)
        {
            return;
        }

        try
        {
            await _sessionManager.SendMessageCommand(
                session.Id,
                session.Id,
                new MessageCommand
                {
                    Header = configuration.ResolvedMessageTitle,
                    Text = configuration.ResolvedMessageText,
                },
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("[{TaskNumber}] Limit message sent", taskNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{TaskNumber}] Failed to send limit message", taskNumber);
        }
    }

    private async Task LogoutDevice(SessionInfo session, int taskNumber)
    {
        if (Plugin.Instance?.Configuration.ForceLogoutOnLimit != true)
        {
            return;
        }

        if (string.IsNullOrEmpty(session.DeviceId))
        {
            return;
        }

        try
        {
            // Logout expects an access token or a device entity, not a session id.
            // Logging out a device deletes its record (token revoked AND the entry
            // disappears from Settings > Devices); the user must sign in again.
            var devices = _deviceManager.GetDevices(new DeviceQuery
            {
                DeviceId = session.DeviceId,
                UserId = session.UserId,
            }).Items;

            if (devices.Count == 0)
            {
                _logger.LogWarning("[{TaskNumber}] No device found to log out (device id {DeviceId})", taskNumber, session.DeviceId);
                return;
            }

            foreach (var device in devices)
            {
                await _sessionManager.Logout(device).ConfigureAwait(false);
            }

            _logger.LogInformation("[{TaskNumber}] Device {DeviceId} logged out", taskNumber, session.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{TaskNumber}] Failed to log out device", taskNumber);
        }
    }
}
