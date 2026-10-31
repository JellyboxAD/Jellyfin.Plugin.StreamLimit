using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamLimit.Gate;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.StreamLimit.Notifications;

/// <summary>
/// Sends the resolved blocked message to the blocked device's sessions (see
/// <see cref="MessageTargets"/>), shared by the HTTP gate and the PlaybackStart limiter.
/// </summary>
/// <remarks>
/// One blocked playback attempt produces a burst of denials (PlaybackInfo, playlists,
/// segments, client retries), so messages are throttled to one per user and device every
/// <see cref="ThrottleInterval"/>.
/// </remarks>
public sealed class BlockMessenger
{
    /// <summary>
    /// Minimum time between two messages to the same user and device.
    /// </summary>
    public static readonly TimeSpan ThrottleInterval = TimeSpan.FromSeconds(30);

    private readonly ISessionManager _sessionManager;
    private readonly StreamEnforcer _enforcer;
    private readonly KeyedThrottle _throttle = new(ThrottleInterval);

    /// <summary>
    /// Initializes a new instance of the <see cref="BlockMessenger"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="enforcer">The shared stop actions (display message).</param>
    public BlockMessenger(ISessionManager sessionManager, StreamEnforcer enforcer)
    {
        _sessionManager = sessionManager;
        _enforcer = enforcer;
    }

    /// <summary>
    /// Sends the message of a block, unless disabled, throttled or nobody can receive it.
    /// </summary>
    /// <param name="userId">The blocked user.</param>
    /// <param name="deviceId">The blocked device id.</param>
    /// <param name="title">The resolved title.</param>
    /// <param name="text">The resolved text.</param>
    /// <param name="context">Log prefix identifying the caller.</param>
    /// <returns>A task completing once the messages were sent (best effort, never throws).</returns>
    public async Task NotifyAsync(Guid userId, string deviceId, string title, string text, string context)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return;
        }

        var targets = MessageTargets.Select(
            _sessionManager.Sessions.ToList(),
            userId,
            deviceId,
            configuration.ShowLimitPopup,
            configuration.NotifyNativeApps);
        if (targets.Count == 0 || !_throttle.TryAcquire(KeyedThrottle.UserDeviceKey(userId, deviceId)))
        {
            return;
        }

        foreach (var target in targets)
        {
            await _enforcer.SendMessageAsync(target.Session, title, text, target.TimeoutMs, context + "/" + target.Reason).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drops expired throttle entries (periodic housekeeping).
    /// </summary>
    public void Prune() => _throttle.Prune();
}
