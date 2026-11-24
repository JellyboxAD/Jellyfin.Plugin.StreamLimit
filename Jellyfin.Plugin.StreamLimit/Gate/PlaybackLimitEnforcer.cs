using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamLimit.Configuration;
using Jellyfin.Plugin.StreamLimit.Notifications;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Applies the per-playback rules of the <see cref="PlaybackTracker"/> (count each playback,
/// limit video transcodes) and carries out their refusals: aborts the playback's open
/// responses, kills its ffmpeg job and reports the block like the other layers do.
/// </summary>
public sealed partial class PlaybackLimitEnforcer
{
    private readonly PlaybackTracker _playbacks;
    private readonly StreamSlotTracker _tracker;
    private readonly EnforcedDeviceRegistry _enforcedDevices;
    private readonly StreamLimitManager _limitManager;
    private readonly ISessionManager _sessionManager;
    private readonly ITranscodeManager _transcodeManager;
    private readonly StreamEnforcer _enforcer;
    private readonly BlockActivityLogger _activityLogger;
    private readonly WebhookNotifier _webhookNotifier;
    private readonly BlockMessenger _messenger;
    private readonly ILogger<PlaybackLimitEnforcer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackLimitEnforcer"/> class.
    /// </summary>
    /// <param name="playbacks">The playback tracker.</param>
    /// <param name="tracker">The gate's slot tracker.</param>
    /// <param name="enforcedDevices">The devices the reactive limiter stopped.</param>
    /// <param name="limitManager">The stream limit manager.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="enforcer">The shared stop actions.</param>
    /// <param name="activityLogger">The activity log writer.</param>
    /// <param name="webhookNotifier">The webhook notifier.</param>
    /// <param name="messenger">The blocked-message sender.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackLimitEnforcer(
        PlaybackTracker playbacks,
        StreamSlotTracker tracker,
        EnforcedDeviceRegistry enforcedDevices,
        StreamLimitManager limitManager,
        ISessionManager sessionManager,
        ITranscodeManager transcodeManager,
        StreamEnforcer enforcer,
        BlockActivityLogger activityLogger,
        WebhookNotifier webhookNotifier,
        BlockMessenger messenger,
        ILogger<PlaybackLimitEnforcer> logger)
    {
        _playbacks = playbacks;
        _tracker = tracker;
        _enforcedDevices = enforcedDevices;
        _limitManager = limitManager;
        _sessionManager = sessionManager;
        _transcodeManager = transcodeManager;
        _enforcer = enforcer;
        _activityLogger = activityLogger;
        _webhookNotifier = webhookNotifier;
        _messenger = messenger;
        _logger = logger;
    }

    /// <summary>
    /// Gets the stream limit that applies per playback for a user: their effective limit when
    /// <see cref="PluginConfiguration.CountPlaybacks"/> and the hard block are on, otherwise 0.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="userId">The user.</param>
    /// <returns>The limit, 0 when the rule does not apply.</returns>
    public int GetPlaybackLimit(PluginConfiguration configuration, Guid userId)
        => configuration.EnableHardBlock && configuration.CountPlaybacks ? Math.Max(0, _limitManager.GetEffectiveLimit(userId)) : 0;

    /// <summary>
    /// Gets the simultaneous video transcode limit of a user (0 when off or exempt).
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="userId">The user.</param>
    /// <returns>The limit, 0 when the rule does not apply.</returns>
    public int GetTranscodeLimit(PluginConfiguration configuration, Guid userId)
        => configuration.MaxTranscodesPerUser > 0 && !_limitManager.IsExempt(userId) ? configuration.MaxTranscodesPerUser : 0;

    /// <summary>
    /// Applies both rules to a user and carries out the refusals. Never throws.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The playbacks refused by this check.</returns>
    public IReadOnlyList<PlaybackDenial> Check(Guid userId)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return Array.Empty<PlaybackDenial>();
        }

        try
        {
            var playbackLimit = GetPlaybackLimit(configuration, userId);
            var transcodeLimit = GetTranscodeLimit(configuration, userId);
            // Either rule needs at least two live playbacks (limits are at least 1): the usual
            // single stream costs one cheap count per request, nothing more.
            if ((playbackLimit <= 0 && transcodeLimit <= 0) || _playbacks.CountLive(userId) < 2)
            {
                return Array.Empty<PlaybackDenial>();
            }

            var held = _tracker.GetHeldDevices(userId, GetPlayingDeviceIds(userId));
            var denials = _playbacks.Enforce(userId, playbackLimit > 0 ? held : Array.Empty<string>(), playbackLimit, transcodeLimit, IsVideoTranscode);
            foreach (var denial in denials)
            {
                Carry(configuration, denial, held);
            }

            return denials;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Playback limit check failed for user {UserId}", userId);
            return Array.Empty<PlaybackDenial>();
        }
    }

    /// <summary>
    /// Checks a user again a few times after a new playback started: a direct-play or
    /// progressive stream makes no further request the gate could refuse, and the overlap of
    /// a long download is only confirmed after <see cref="PlaybackTracker.DownloadConfirmAfter"/>.
    /// </summary>
    /// <param name="userId">The user.</param>
    public void ScheduleCheck(Guid userId)
        => _ = Task.Run(async () =>
        {
            // Absolute times after the start: 6 s, 10 s, 20 s.
            var elapsed = TimeSpan.Zero;
            foreach (var at in new[] { 6, 10, 20 })
            {
                var target = TimeSpan.FromSeconds(at);
                await Task.Delay(target - elapsed).ConfigureAwait(false);
                elapsed = target;
                Check(userId);
            }
        });

    /// <summary>
    /// Whether a playback is a video transcode right now: it has a running ffmpeg job that
    /// does not copy the video. A playback without a PlaySessionId cannot be looked up and is
    /// not counted: a stream is never refused on a guess.
    /// </summary>
    /// <param name="playback">The playback.</param>
    /// <returns>True for a running video transcode.</returns>
    public bool IsVideoTranscode(PlaybackRef playback)
    {
        if (!playback.IsVideo)
        {
            return false;
        }

        if (string.IsNullOrEmpty(playback.PlaySessionId))
        {
            return false;
        }

        var job = _transcodeManager.GetTranscodingJob(playback.PlaySessionId);
        if (job is null || job.HasExited)
        {
            return false;
        }

        string? arguments;
        try
        {
            arguments = job.Process?.StartInfo.Arguments;
        }
        catch (InvalidOperationException)
        {
            // The process is gone; the job is about to end.
            return false;
        }

        return !IsVideoCopy(arguments);
    }

    /// <summary>
    /// Whether ffmpeg arguments copy the video stream (a remux), which costs almost no CPU.
    /// Unknown arguments count as a transcode.
    /// </summary>
    /// <param name="arguments">The ffmpeg command line.</param>
    /// <returns>True when the video is copied.</returns>
    public static bool IsVideoCopy(string? arguments)
        => !string.IsNullOrEmpty(arguments) && VideoCopyRegex().IsMatch(arguments);

    // Jellyfin writes "-codec:v:0 copy"; the shorter spellings are accepted too.
    [GeneratedRegex(@"(?:^|\s)-(?:c|codec|vcodec)(?::v)?(?::0)?\s+copy(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VideoCopyRegex();

    private IReadOnlyCollection<string> GetPlayingDeviceIds(Guid userId)
        => PlayingSessions.GetPlayingDeviceIds(
            _sessionManager.Sessions,
            userId,
            DateTime.UtcNow,
            s => _enforcedDevices.IsEnforced(userId, PlayingSessions.StreamKey(s)));

    private void Carry(PluginConfiguration configuration, PlaybackDenial denial, IReadOnlyCollection<string> heldDevices)
    {
        var rule = denial.Reason == PlaybackDenialReason.TranscodeLimit ? "transcode limit" : "playback limit";
        _logger.LogInformation(
            "Refusing playback {PlaySessionId} of user {UserId} on device {DeviceId}: {Rule} of {Limit} reached ({Active} other)",
            denial.PlaySessionId ?? "(no PlaySessionId)",
            denial.UserId,
            denial.DeviceId,
            rule,
            denial.Limit,
            denial.Active);

        foreach (var abort in denial.Aborts)
        {
            try
            {
                abort();
            }
            catch (Exception)
            {
                // The response may already be completing.
            }
        }

        var context = "playback " + (denial.PlaySessionId ?? denial.DeviceId);
        if (!string.IsNullOrEmpty(denial.PlaySessionId)
            && (denial.Reason == PlaybackDenialReason.TranscodeLimit || configuration.KillTranscodeJobs))
        {
            _ = _enforcer.KillTranscodeJobsAsync(denial.DeviceId, denial.PlaySessionId, context);
        }

        try
        {
            var userSessions = _sessionManager.Sessions.Where(s => s.UserId.Equals(denial.UserId)).ToList();
            var session = userSessions.FirstOrDefault(s => string.Equals(s.DeviceId, denial.DeviceId, StringComparison.OrdinalIgnoreCase));
            var blocked = new BlockedStream(
                denial.UserId,
                session?.UserName ?? userSessions.Select(s => s.UserName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? _limitManager.GetUserName(denial.UserId),
                denial.DeviceId,
                session?.DeviceName,
                session?.Client,
                session?.RemoteEndPoint,
                denial.Limit,
                rule,
                denial.Active,
                MessageTemplate.DescribeDevices(heldDevices, userSessions));

            _activityLogger.LogBlocked(blocked);
            _webhookNotifier.NotifyBlocked(blocked);
            _ = _messenger.NotifyAsync(
                denial.UserId,
                denial.DeviceId,
                MessageTemplate.Render(configuration.ResolvedMessageTitle, blocked.MessageValues),
                MessageTemplate.Render(configuration.ResolvedMessageText, blocked.MessageValues),
                context);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not report the refused playback");
        }
    }
}
