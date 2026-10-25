using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// The actions that stop a stream, shared by the reactive limiter and the admin live view:
/// stop command (plus a server-side stop for sessions that cannot receive it), transcode
/// kill, display message and device logout. Every action is best effort and only logs its
/// failures.
/// </summary>
public sealed class StreamEnforcer
{
    private readonly ISessionManager _sessionManager;
    private readonly ITranscodeManager _transcodeManager;
    private readonly IDeviceManager _deviceManager;
    private readonly ILogger<StreamEnforcer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamEnforcer"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="deviceManager">Instance of the <see cref="IDeviceManager"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public StreamEnforcer(
        ISessionManager sessionManager,
        ITranscodeManager transcodeManager,
        IDeviceManager deviceManager,
        ILogger<StreamEnforcer> logger)
    {
        _sessionManager = sessionManager;
        _transcodeManager = transcodeManager;
        _deviceManager = deviceManager;
        _logger = logger;
    }

    /// <summary>
    /// Stops a session's playback: a Stop playstate command, plus a server-side playback
    /// stop when the session has no remote-control channel (the command goes nowhere).
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="playSessionId">The PlaySessionId of the playback, when known.</param>
    /// <param name="mediaSourceId">The media source id, when known.</param>
    /// <param name="context">Log prefix identifying the caller.</param>
    /// <returns>True when the command was delivered or the playback was stopped server side.</returns>
    public async Task<bool> StopAsync(SessionInfo session, string? playSessionId, string? mediaSourceId, string context)
    {
        var stopped = false;
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

            stopped = true;
            _logger.LogInformation("[{Context}] Stop command sent to session {SessionId}", context, session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Context}] Failed to send stop command to session {SessionId}", context, session.Id);
        }

        if (session.SupportsRemoteControl)
        {
            return stopped;
        }

        // The client has no remote-control channel (jellyfin-android's native player,
        // HTTP-only clients), so the Stop command above went nowhere. Stop the playback
        // on the server's side instead: this clears the session's NowPlayingItem, so the
        // session stops counting as a stream.
        try
        {
            await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
            {
                SessionId = session.Id,
                ItemId = session.NowPlayingItem?.Id ?? Guid.Empty,
                PlaySessionId = playSessionId,
                MediaSourceId = mediaSourceId,
                PositionTicks = session.PlayState?.PositionTicks,
            }).ConfigureAwait(false);

            _logger.LogInformation("[{Context}] Session {SessionId} cannot receive commands; playback stopped server side", context, session.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Context}] Failed to stop playback server side", context);
            return stopped;
        }
    }

    /// <summary>
    /// Kills the transcode job feeding a playback. Skipped without a PlaySessionId: Jellyfin
    /// would then kill every transcode job of the device, which could take down an
    /// unrelated, legitimate stream sharing that device id.
    /// </summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="playSessionId">The PlaySessionId of the playback.</param>
    /// <param name="context">Log prefix identifying the caller.</param>
    /// <returns>True when a kill was requested.</returns>
    public async Task<bool> KillTranscodeJobsAsync(string? deviceId, string? playSessionId, string context)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(playSessionId))
        {
            return false;
        }

        try
        {
            // Kills the ffmpeg process feeding the offending stream. Clients that
            // ignore the stop command lose their source and stop on their own.
            await _transcodeManager.KillTranscodingJobs(deviceId, playSessionId, _ => true).ConfigureAwait(false);
            _logger.LogInformation("[{Context}] Transcode jobs killed for device {DeviceId}", context, deviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Context}] Failed to kill transcode jobs", context);
            return false;
        }
    }

    /// <summary>
    /// Sends a Jellyfin display message to a session.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="title">The message title.</param>
    /// <param name="text">The message text.</param>
    /// <param name="context">Log prefix identifying the caller.</param>
    /// <returns>A task.</returns>
    public async Task SendMessageAsync(SessionInfo session, string title, string text, string context)
    {
        try
        {
            await _sessionManager.SendMessageCommand(
                session.Id,
                session.Id,
                new MessageCommand
                {
                    Header = title,
                    Text = text,
                },
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("[{Context}] Limit message sent", context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Context}] Failed to send limit message", context);
        }
    }

    /// <summary>
    /// Logs a device out. Logging out deletes the device record (its access token is
    /// revoked and the entry disappears from Settings &gt; Devices); the user must sign in
    /// again on that device.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device id.</param>
    /// <param name="context">Log prefix identifying the caller.</param>
    /// <returns>True when at least one device record was logged out.</returns>
    public async Task<bool> LogoutDeviceAsync(Guid userId, string? deviceId, string context)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return false;
        }

        try
        {
            // Logout expects an access token or a device entity, not a session id.
            var devices = _deviceManager.GetDevices(new DeviceQuery
            {
                DeviceId = deviceId,
                UserId = userId,
            }).Items;

            if (devices.Count == 0)
            {
                _logger.LogWarning("[{Context}] No device found to log out (device id {DeviceId})", context, deviceId);
                return false;
            }

            foreach (var device in devices)
            {
                await _sessionManager.Logout(device).ConfigureAwait(false);
            }

            _logger.LogInformation("[{Context}] Device {DeviceId} logged out", context, deviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Context}] Failed to log out device", context);
            return false;
        }
    }
}
