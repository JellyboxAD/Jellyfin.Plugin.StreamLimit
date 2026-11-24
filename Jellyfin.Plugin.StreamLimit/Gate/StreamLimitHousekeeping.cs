using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamLimit.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Periodic housekeeping of the plugin's in-memory state: prunes every user's gate slots
/// (aborting responses idle for too long) and the bounded registries and throttles, so
/// memory and the live view stay clean even for users who never come back.
/// </summary>
public sealed class StreamLimitHousekeeping : IHostedService, IDisposable
{
    /// <summary>
    /// Interval between two sweeps.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly StreamSlotTracker _tracker;
    private readonly EnforcedDeviceRegistry _enforcedDevices;
    private readonly PlaySessionRegistry _playSessions;
    private readonly BlockMessenger _messenger;
    private readonly BlockActivityLogger _activityLogger;
    private readonly WebhookNotifier _webhookNotifier;
    private readonly PlaybackTracker _playbacks;
    private readonly PlaybackLimitEnforcer _playbackEnforcer;
    private readonly ILogger<StreamLimitHousekeeping> _logger;
    private Timer? _timer;
    private int _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitHousekeeping"/> class.
    /// </summary>
    /// <param name="tracker">The gate's slot tracker.</param>
    /// <param name="enforcedDevices">The registry of devices the limit was enforced on.</param>
    /// <param name="playSessions">The registry of the sessions' PlaySessionIds.</param>
    /// <param name="messenger">The blocked-message sender (throttle).</param>
    /// <param name="activityLogger">The activity log writer (throttle).</param>
    /// <param name="webhookNotifier">The webhook notifier (throttle).</param>
    /// <param name="playbacks">The per-playback tracker.</param>
    /// <param name="playbackEnforcer">The per-playback rules, re-checked for users with several live playbacks.</param>
    /// <param name="logger">The logger.</param>
    public StreamLimitHousekeeping(
        StreamSlotTracker tracker,
        EnforcedDeviceRegistry enforcedDevices,
        PlaySessionRegistry playSessions,
        BlockMessenger messenger,
        BlockActivityLogger activityLogger,
        WebhookNotifier webhookNotifier,
        PlaybackTracker playbacks,
        PlaybackLimitEnforcer playbackEnforcer,
        ILogger<StreamLimitHousekeeping> logger)
    {
        _tracker = tracker;
        _enforcedDevices = enforcedDevices;
        _playSessions = playSessions;
        _messenger = messenger;
        _activityLogger = activityLogger;
        _webhookNotifier = webhookNotifier;
        _playbacks = playbacks;
        _playbackEnforcer = playbackEnforcer;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Sweep(), null, Interval, Interval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    /// <summary>
    /// Runs one sweep. Never throws.
    /// </summary>
    public void Sweep()
    {
        // A slow sweep must not overlap the next tick.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var aborted = _tracker.Sweep();
            if (aborted > 0)
            {
                _logger.LogInformation("Stream gate aborted {Count} media response(s) idle for more than {Minutes} minutes", aborted, StreamSlotTracker.IdleRequestAbortAfter.TotalMinutes);
            }

            // Users with several live playbacks: a direct-play tab past the overlap grace is
            // caught here even if its scheduled check was missed.
            foreach (var userId in _playbacks.Sweep())
            {
                _playbackEnforcer.Check(userId);
            }

            _enforcedDevices.Prune();
            _playSessions.Prune();
            _messenger.Prune();
            _activityLogger.PruneThrottle();
            _webhookNotifier.PruneThrottle();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream limit housekeeping failed");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
