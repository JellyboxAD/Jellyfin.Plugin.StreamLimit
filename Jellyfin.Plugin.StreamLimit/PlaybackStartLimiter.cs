using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.StreamLimit.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using MediaBrowser.Controller.Events;
using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.StreamLimit.Limiter;

public sealed class PlaybackStartLimiter : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly ISessionManager _sessionManager;
    private readonly IHttpContextAccessor _authenticationManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDeviceManager _deviceManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<PlaybackStartLimiter> _logger;
    private PluginConfiguration? _configuration;
    private Dictionary<string, int> _userData = new();

    private static int _taskCounter;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStartLimiter"/> class.
    /// </summary>
    public PlaybackStartLimiter(
        [NotNull] ISessionManager sessionManager,
        [NotNull] IHttpContextAccessor authenticationManager,
        [NotNull] ILoggerFactory loggerFactory,
        [NotNull] IDeviceManager deviceManager,
        [NotNull] IMediaSourceManager mediaSourceManager)
    {
        _sessionManager = sessionManager;
        _authenticationManager = authenticationManager;
        _loggerFactory = loggerFactory;
        _deviceManager = deviceManager;
        _mediaSourceManager = mediaSourceManager;
        _logger = loggerFactory.CreateLogger<PlaybackStartLimiter>();
        _configuration = Plugin.Instance?.Configuration as PluginConfiguration;

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += (sender, args) =>
            {
                _configuration = args as PluginConfiguration;
                LoadUserData();
            };
        }

        LoadUserData();
    }

    private void LoadUserData()
    {
        if (_configuration == null)
        {
            _logger.LogWarning("Configuration is null when loading user data");
            return;
        }

        var configurationUserStreamLimits = _configuration.UserStreamLimits;
        if (string.IsNullOrEmpty(configurationUserStreamLimits))
        {
            return;
        }

        try
        {
            _userData = JsonConvert.DeserializeObject<Dictionary<string, int>>(configurationUserStreamLimits) ?? new Dictionary<string, int>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert configurationUserStreamLimits to object");
        }
    }

    private static int GetNextTaskNumber() => Interlocked.Increment(ref _taskCounter);

    public async Task OnEvent(PlaybackStartEventArgs e)
    {
        var taskNumber = GetNextTaskNumber();
        _logger.LogInformation("[{TaskNumber}] ---------------[StreamLimit_Start]---------------", taskNumber);

        try
        {
            if (e.Users.Count == 0 || e.Users[0].Id == Guid.Empty)
            {
                _logger.LogInformation("[{TaskNumber}] [Error] Invalid user ID", taskNumber);
                return;
            }

            if (e.Session?.Id == null)
            {
                _logger.LogInformation("[{TaskNumber}] [Error] Invalid session", taskNumber);
                return;
            }

            var userId = e.Users[0].Id.ToString();
            _logger.LogInformation("[{TaskNumber}] Playback Started : {UserId}", taskNumber, userId);
            _logger.LogInformation("[{TaskNumber}] PlaySessionId: {PlaySessionId}", taskNumber, e.PlaySessionId);
            _logger.LogInformation("[{TaskNumber}] MediaSourceId: {MediaSourceId}", taskNumber, e.MediaSourceId);
            _logger.LogInformation("[{TaskNumber}] Device: {DeviceName} ({DeviceId})", taskNumber, e.Session.DeviceName, e.Session.DeviceId);
            _logger.LogInformation("[{TaskNumber}] Client: {Client}", taskNumber, e.Session.Client);
            _logger.LogInformation("[{TaskNumber}] Session.PlayState.LiveStreamId: {LiveStreamId}", taskNumber, e.Session.PlayState?.LiveStreamId ?? "(null)");

            var activeStreamsForUser = _sessionManager.Sessions.Count(s =>
                s.UserId == Guid.Parse(userId) &&
                s.NowPlayingItem != null &&
                s.IsActive);
            _logger.LogInformation("[{TaskNumber}] Streaming Active : {ActiveStreams}", taskNumber, activeStreamsForUser);

            var userDataKey = userId.Replace("-", string.Empty);
            var maxStreamsAllowed = _userData.GetValueOrDefault(userDataKey);

            _logger.LogInformation(
                "[{TaskNumber}] Streaming Limit  : {MaxStreams} [{HasLimit}]",
                taskNumber,
                maxStreamsAllowed,
                maxStreamsAllowed > 0 ? "Y" : "N");

            if (maxStreamsAllowed > 0 && activeStreamsForUser > maxStreamsAllowed)
            {
                await LimitPlayback(e.Session, taskNumber);
            }
            else
            {
                _logger.LogInformation(
                    "[{TaskNumber}] {Status} : Play Bypass",
                    taskNumber,
                    maxStreamsAllowed > 0 ? "Not Limited" : "No In Limit");
            }
        }
        catch (Exception ex)
        {
            LogError(ex, e, taskNumber);
        }

        _logger.LogInformation("[{TaskNumber}] ----------------[StreamLimit_End]----------------", taskNumber);
    }

    private async Task LimitPlayback(SessionInfo session, int taskNumber)
    {
        _logger.LogInformation("[{TaskNumber}] Attempting to stop playback for session {SessionId}", taskNumber, session.Id);

        try
        {
            // 1. PRIORITY: Close the LiveStream to cut network stream (for external players like VLC, Infuse, mobile apps)
            if (session.PlayState?.LiveStreamId != null)
            {
                _logger.LogInformation("[{TaskNumber}] LiveStreamId detected: {LiveStreamId}", taskNumber, session.PlayState.LiveStreamId);
                await CloseLiveStream(session.PlayState.LiveStreamId, taskNumber);
            }
            else
            {
                _logger.LogInformation("[{TaskNumber}] No LiveStreamId found in session PlayState", taskNumber);
                
                // Try to find and close any LiveStream using MediaSourceId
                if (session.PlayState?.MediaSourceId != null)
                {
                    _logger.LogInformation("[{TaskNumber}] Attempting to find LiveStream via MediaSourceId: {MediaSourceId}", taskNumber, session.PlayState.MediaSourceId);
                    try
                    {
                        var liveStreamInfo = _mediaSourceManager.GetLiveStreamInfo(session.PlayState.MediaSourceId);
                        if (liveStreamInfo != null)
                        {
                            _logger.LogInformation("[{TaskNumber}] Found LiveStream via MediaSourceId, closing it", taskNumber);
                            await CloseLiveStream(session.PlayState.MediaSourceId, taskNumber);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation("[{TaskNumber}] No LiveStream found for MediaSourceId (this is normal for Direct Play): {Error}", taskNumber, ex.Message);
                    }
                }
            }

            // 2. Stop playback command (for web players that listen to commands)
            await StopPlayback(session, taskNumber);
            
            // Wait a bit and verify playback actually stopped
            await Task.Delay(500);
            if (!await VerifyPlaybackStopped(session, taskNumber))
            {
                _logger.LogWarning("[{TaskNumber}] ⚠️ Playback did NOT stop after command - trying aggressive methods", taskNumber);
                
                // Try multiple stop commands
                for (int i = 0; i < 3; i++)
                {
                    await StopPlayback(session, taskNumber);
                    await Task.Delay(200);
                    
                    if (await VerifyPlaybackStopped(session, taskNumber))
                    {
                        _logger.LogInformation("[{TaskNumber}] ✅ Playback stopped after {Attempts} attempts", taskNumber, i + 2);
                        break;
                    }
                }
            }
            
            // 3. Show message to user
            await ShowLimitMessage(session, taskNumber);
            
            // 4. Logout session as final measure
            await LogoutSession(session, taskNumber);

            // Final verification
            await Task.Delay(500);
            var finalCheck = await VerifyPlaybackStopped(session, taskNumber);
            _logger.LogInformation(
                "[{TaskNumber}] {Result} : Play {Status}",
                taskNumber,
                finalCheck ? "SUCCESS" : "PARTIAL",
                finalCheck ? "Fully Canceled" : "Stopped but session may persist");
        }
        catch (Exception stopEx)
        {
            _logger.LogError(stopEx, "[{TaskNumber}] Failed to stop playback", taskNumber);
            throw;
        }
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
                    ControllingUserId = session.UserId.ToString(),
                    SeekPositionTicks = 0,
                },
                CancellationToken.None);

            _logger.LogInformation("[{TaskNumber}] Stop command sent (not verified yet)", taskNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TaskNumber}] Failed to send stop command", taskNumber);
            throw;
        }
    }

    private async Task<bool> VerifyPlaybackStopped(SessionInfo session, int taskNumber)
    {
        try
        {
            // Wait a moment for the session state to update
            await Task.Delay(100);
            
            // Get fresh session data
            var updatedSession = _sessionManager.Sessions.FirstOrDefault(s => s.Id == session.Id);
            
            if (updatedSession == null)
            {
                _logger.LogInformation("[{TaskNumber}] ✅ Session no longer exists - playback stopped", taskNumber);
                return true;
            }

            var isStillPlaying = updatedSession.NowPlayingItem != null && updatedSession.IsActive;
            
            if (isStillPlaying)
            {
                _logger.LogWarning(
                    "[{TaskNumber}] ❌ Playback still active! NowPlayingItem: {Item}, IsActive: {IsActive}",
                    taskNumber,
                    updatedSession.NowPlayingItem?.Name ?? "(null)",
                    updatedSession.IsActive);
                return false;
            }
            else
            {
                _logger.LogInformation(
                    "[{TaskNumber}] ✅ Playback stopped confirmed - NowPlayingItem is null or session inactive",
                    taskNumber);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TaskNumber}] Failed to verify playback stopped", taskNumber);
            return false; // Assume not stopped if we can't verify
        }
    }

    private async Task ShowLimitMessage(SessionInfo session, int taskNumber)
    {
        try
        {
            var MessageText = _configuration?.MessageTitle ?? "Stream Limit";
            var messageText = _configuration?.MessageText ?? "Active streams exceeded";

            await _sessionManager.SendMessageCommand(
                session.Id,
                session.Id,
                new MessageCommand
                {
                    Header = MessageText,
                    Text = messageText,
                },
                CancellationToken.None);

            _logger.LogInformation("[{TaskNumber}] Successfully sent message command", taskNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TaskNumber}] Failed to send message command", taskNumber);
            throw;
        }
    }

    private async Task CloseLiveStream(string liveStreamId, int taskNumber)
    {
        try
        {
            _logger.LogInformation("[{TaskNumber}] Attempting to close LiveStream {LiveStreamId}", taskNumber, liveStreamId);
            
            await _mediaSourceManager.CloseLiveStream(liveStreamId);
            
            _logger.LogInformation("[{TaskNumber}] ✅ Successfully closed LiveStream - network stream cut!", taskNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TaskNumber}] Failed to close LiveStream {LiveStreamId}", taskNumber, liveStreamId);
            // Don't throw - continue with other stop methods
        }
    }

    private async Task LogoutSession(SessionInfo session, int taskNumber)
    {
        try
        {
            // First, try to end the session directly (more aggressive than logout)
            _logger.LogInformation("[{TaskNumber}] Forcefully ending session {SessionId}", taskNumber, session.Id);
            await _sessionManager.ReportSessionEnded(session.Id);
            _logger.LogInformation("[{TaskNumber}] Session ended via ReportSessionEnded", taskNumber);
            
            // Also try logout as backup
            await _sessionManager.Logout(session.Id);
            _logger.LogInformation("[{TaskNumber}] Successfully logged out session", taskNumber);
        }
        catch (Exception rex)
        {
            _logger.LogWarning(rex, "[{TaskNumber}] Failed to logout session", taskNumber);
        }
    }

    private void LogError(Exception ex, PlaybackStartEventArgs e, int taskNumber)
    {
        if (ex.InnerException != null)
        {
            _logger.LogError(ex.InnerException, "[{TaskNumber}] Inner exception", taskNumber);
        }

        _logger.LogError(
            ex,
            "[{TaskNumber}] Error details - Users: {Users}, PlaySessionId: {PlaySessionId}, Session: {Session}",
            taskNumber,
            e.Users,
            e.PlaySessionId,
            e.Session);
    }
}

