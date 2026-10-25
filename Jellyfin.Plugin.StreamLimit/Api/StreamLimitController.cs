using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamLimit.Gate;
using Jellyfin.Plugin.StreamLimit.Notifications;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit.Api;

/// <summary>
/// Stream limit administration endpoints. Restricted to elevated users:
/// otherwise any authenticated user could raise their own limit.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("[controller]/[action]")]
[Produces(MediaTypeNames.Application.Json)]
public class StreamLimitController : ControllerBase
{
    private readonly ILogger<StreamLimitController> _logger;
    private readonly IUserManager _userManager;
    private readonly StreamLimitManager _limitManager;
    private readonly ISessionManager _sessionManager;
    private readonly StreamSlotTracker _tracker;
    private readonly EnforcedDeviceRegistry _enforcedDevices;
    private readonly PlaySessionRegistry _playSessions;
    private readonly StreamEnforcer _enforcer;
    private readonly BlockActivityLogger _activityLogger;
    private readonly WebhookNotifier _webhookNotifier;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitController"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="limitManager">The stream limit manager.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="tracker">The gate's slot tracker.</param>
    /// <param name="enforcedDevices">The registry of devices the limit was enforced on.</param>
    /// <param name="playSessions">The registry of the sessions' PlaySessionIds.</param>
    /// <param name="enforcer">The shared stop actions.</param>
    /// <param name="activityLogger">The activity log writer.</param>
    /// <param name="webhookNotifier">The webhook notifier.</param>
    public StreamLimitController(
        ILogger<StreamLimitController> logger,
        IUserManager userManager,
        StreamLimitManager limitManager,
        ISessionManager sessionManager,
        StreamSlotTracker tracker,
        EnforcedDeviceRegistry enforcedDevices,
        PlaySessionRegistry playSessions,
        StreamEnforcer enforcer,
        BlockActivityLogger activityLogger,
        WebhookNotifier webhookNotifier)
    {
        _logger = logger;
        _userManager = userManager;
        _limitManager = limitManager;
        _sessionManager = sessionManager;
        _tracker = tracker;
        _enforcedDevices = enforcedDevices;
        _playSessions = playSessions;
        _enforcer = enforcer;
        _activityLogger = activityLogger;
        _webhookNotifier = webhookNotifier;
    }

    /// <summary>
    /// Gets every user's currently counted streams: playing sessions and gate slots, one
    /// entry per device.
    /// </summary>
    /// <returns>The users with at least one stream.</returns>
    [HttpGet]
    public ActionResult<IReadOnlyList<ActiveUserStreams>> ActiveStreams()
    {
        var result = ActiveStreamsBuilder.Build(
            _sessionManager.Sessions.ToList(),
            _tracker.GetSnapshot(),
            DateTime.UtcNow,
            _limitManager.GetEffectiveLimit,
            _limitManager.IsExempt,
            _limitManager.GetUserName,
            _enforcedDevices.IsEnforced);
        return Ok(result);
    }

    /// <summary>
    /// Stops a user's stream on one device: stop command (server-side stop for sessions
    /// without remote control), transcode kill, gate slot release and, optionally, device
    /// logout. Recorded in the activity log.
    /// </summary>
    /// <param name="userId">The user id (with or without dashes).</param>
    /// <param name="deviceId">The device id (the session id for a session without device id).</param>
    /// <param name="logout">Whether to also log the device out.</param>
    /// <returns>What was done.</returns>
    [HttpPost]
    public async Task<IActionResult> KickStream([FromQuery] string userId, [FromQuery] string deviceId, [FromQuery] bool logout = false)
    {
        if (!Guid.TryParse(userId, out var userGuid) || userGuid.Equals(Guid.Empty))
        {
            return BadRequest("Invalid user id");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return BadRequest("Invalid device id");
        }

        var sessions = _sessionManager.Sessions
            .Where(s => s.UserId.Equals(userGuid)
                        && (string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(s.Id, deviceId, StringComparison.Ordinal)))
            .ToList();

        // Excluded from the count right away, whatever the client keeps reporting.
        _enforcedDevices.Record(userGuid, deviceId);

        var stopped = 0;
        foreach (var session in sessions.Where(s => s.NowPlayingItem is not null))
        {
            // Read before stopping: our own stop event forgets it.
            var playSessionId = _playSessions.Get(session.Id);
            if (await _enforcer.StopAsync(session, playSessionId, null, "admin").ConfigureAwait(false))
            {
                stopped++;
            }

            await _enforcer.KillTranscodeJobsAsync(session.DeviceId, playSessionId, "admin").ConfigureAwait(false);
        }

        // Again: a server-side stop publishes a stop event whose releaser clears it.
        _enforcedDevices.Record(userGuid, deviceId);
        // Cut the device's media responses still streaming (a direct-play download is one
        // long request) and free its slot now, instead of waiting for the abandoned-slot rule.
        var (aborted, released) = _tracker.AbortDevice(userGuid, deviceId);
        var target = sessions.FirstOrDefault();
        var loggedOut = logout
                        && await _enforcer.LogoutDeviceAsync(userGuid, target?.DeviceId ?? deviceId, "admin").ConfigureAwait(false);

        var adminId = Guid.TryParse(User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value, out var parsedAdmin)
            ? parsedAdmin
            : Guid.Empty;
        var adminName = _limitManager.GetUserName(adminId);
        var kicked = new BlockedStream(
            userGuid,
            target?.UserName ?? _limitManager.GetUserName(userGuid),
            deviceId,
            target?.DeviceName,
            target?.Client,
            target?.RemoteEndPoint,
            _limitManager.GetEffectiveLimit(userGuid),
            "administrator");
        _activityLogger.LogAdminKick(adminId, adminName, kicked, loggedOut);
        _webhookNotifier.NotifyAdminKick(adminName, kicked);

        _logger.LogInformation(
            "Admin {Admin} stopped the stream of user {UserId} on device {DeviceId}: {Stopped} session(s) stopped, {Aborted} response(s) aborted, slot released: {Released}, logged out: {LoggedOut}",
            adminName,
            userGuid,
            deviceId,
            stopped,
            aborted,
            released,
            loggedOut);

        return Ok(new { stopped, aborted, released, loggedOut });
    }

    /// <summary>
    /// Sends a test notification to a webhook.
    /// </summary>
    /// <param name="url">The webhook URL to test; the saved one when omitted.</param>
    /// <returns>Whether the endpoint answered 2xx, and its HTTP status.</returns>
    [HttpPost]
    public async Task<IActionResult> TestWebhook([FromQuery] string? url = null)
    {
        var target = string.IsNullOrWhiteSpace(url) ? Plugin.Instance?.Configuration.WebhookUrl : url;
        if (!WebhookPayloads.TryParse(target, out var uri))
        {
            return BadRequest(new { ok = false, status = (int?)null, error = "No valid http(s) webhook URL" });
        }

        var (ok, status, error) = await _webhookNotifier.SendTestAsync(uri).ConfigureAwait(false);
        return Ok(new { ok, status, error });
    }

    /// <summary>
    /// Gets the stream limit configured for a user.
    /// </summary>
    /// <param name="userId">The user id (with or without dashes).</param>
    /// <returns>The explicit and effective limit for the user.</returns>
    [HttpGet]
    public IActionResult GetUserStreamLimit([FromQuery] string userId)
    {
        if (!Guid.TryParse(userId, out var userGuid))
        {
            return BadRequest("Invalid user id");
        }

        if (_userManager.GetUserById(userGuid) is null)
        {
            return NotFound("User does not exist");
        }

        return Ok(new
        {
            userId = StreamLimitStore.NormalizeUserKey(userGuid),
            streamsAllowed = _limitManager.GetEffectiveLimit(userGuid),
            explicitLimit = _limitManager.GetExplicitLimit(userGuid),
        });
    }

    /// <summary>
    /// Gets all explicit per-user limits and the default limit.
    /// </summary>
    /// <returns>The limit map.</returns>
    [HttpGet]
    public IActionResult GetAllStreamLimits()
    {
        return Ok(new
        {
            defaultMaxStreams = Plugin.Instance?.Configuration.DefaultMaxStreams ?? 0,
            limits = _limitManager.GetAllLimits(),
        });
    }

    /// <summary>
    /// Sets the stream limit for a user. A value of 0 removes the explicit
    /// limit so the default limit applies again.
    /// </summary>
    /// <param name="userId">The user id (with or without dashes).</param>
    /// <param name="streamsAllowed">The maximum number of simultaneous streams, 0 to remove.</param>
    /// <returns>Status of the operation.</returns>
    [HttpPost]
    public IActionResult SetUserStreamLimit([FromQuery] string userId, [FromQuery] int streamsAllowed)
    {
        if (!Guid.TryParse(userId, out var userGuid))
        {
            return BadRequest("Invalid user id");
        }

        if (streamsAllowed < 0)
        {
            return BadRequest("streamsAllowed must be 0 (unlimited) or a positive number");
        }

        if (_userManager.GetUserById(userGuid) is null)
        {
            return NotFound("User does not exist");
        }

        try
        {
            _limitManager.SetLimit(userGuid, streamsAllowed);
            return Ok(new
            {
                userId = StreamLimitStore.NormalizeUserKey(userGuid),
                streamsAllowed,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set stream limit for user {UserId}", userGuid);
            return StatusCode(500, "Failed to save the stream limit");
        }
    }

    /// <summary>
    /// Sets the title and text of the message shown when a stream is blocked.
    /// </summary>
    /// <param name="alertMessage">The message text.</param>
    /// <param name="title">The message title.</param>
    /// <returns>Status of the operation.</returns>
    [HttpPost]
    public IActionResult SetAlertMessage([FromQuery] string alertMessage, [FromQuery] string title)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, "Plugin instance is not available");
        }

        try
        {
            plugin.Configuration.MessageText = alertMessage ?? string.Empty;
            plugin.Configuration.MessageTitle = title ?? string.Empty;
            plugin.SaveConfiguration();
            return Ok(new { title, message = alertMessage });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update alert message");
            return StatusCode(500, "Failed to save the alert message");
        }
    }
}
