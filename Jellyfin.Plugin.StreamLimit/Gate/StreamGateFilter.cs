using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.StreamLimit.Configuration;
using Jellyfin.Plugin.StreamLimit.Notifications;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Global MVC resource filter that rejects media requests over the user's stream
/// limit at the HTTP level, before any media byte is served.
/// </summary>
/// <remarks>
/// This is the enforcement layer that works for every client, including ones that
/// ignore remote stop commands (Infuse, external players, Streamyfin in background): a stream that
/// is never served cannot be played. The filter fails open — requests whose user
/// cannot be identified are never blocked.
/// </remarks>
public sealed class StreamGateFilter : IAsyncResourceFilter
{
    private readonly StreamSlotTracker _tracker;
    private readonly EnforcedDeviceRegistry _enforcedDevices;
    private readonly BlockActivityLogger _activityLogger;
    private readonly WebhookNotifier _webhookNotifier;
    private readonly StreamEnforcer _enforcer;
    private readonly IUserManager _userManager;
    private readonly StreamLimitManager _limitManager;
    private readonly ISessionManager _sessionManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IDeviceManager _deviceManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StreamGateFilter> _logger;
    private readonly KeyedThrottle _notifyThrottle = new(TimeSpan.FromSeconds(30));

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamGateFilter"/> class.
    /// </summary>
    /// <param name="tracker">The slot tracker.</param>
    /// <param name="enforcedDevices">The devices the reactive limiter just stopped.</param>
    /// <param name="activityLogger">The activity log writer for blocked streams.</param>
    /// <param name="webhookNotifier">The webhook notifier.</param>
    /// <param name="enforcer">The shared stop actions (display message).</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="limitManager">The stream limit manager.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="authorizationContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    /// <param name="deviceManager">Instance of the <see cref="IDeviceManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public StreamGateFilter(
        StreamSlotTracker tracker,
        EnforcedDeviceRegistry enforcedDevices,
        BlockActivityLogger activityLogger,
        WebhookNotifier webhookNotifier,
        StreamEnforcer enforcer,
        IUserManager userManager,
        StreamLimitManager limitManager,
        ISessionManager sessionManager,
        IAuthorizationContext authorizationContext,
        IDeviceManager deviceManager,
        ILibraryManager libraryManager,
        ILogger<StreamGateFilter> logger)
    {
        _tracker = tracker;
        _enforcedDevices = enforcedDevices;
        _activityLogger = activityLogger;
        _webhookNotifier = webhookNotifier;
        _enforcer = enforcer;
        _userManager = userManager;
        _limitManager = limitManager;
        _sessionManager = sessionManager;
        _authorizationContext = authorizationContext;
        _deviceManager = deviceManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var configuration = Plugin.Instance?.Configuration;
        var descriptor = context.ActionDescriptor as ControllerActionDescriptor;
        var kind = StreamGateEndpoints.Classify(descriptor?.ControllerName, descriptor?.ActionName);

        if (kind == GateKind.None
            || configuration is null
            || !configuration.EnableHardBlock
            || (kind == GateKind.StartupCheck && !configuration.BlockPlaybackInfo))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Theme songs and video backdrops play through the same endpoints while the
        // user merely browses the library; they must never hold a playback slot.
        if (kind == GateKind.MediaBytes && IsThemeMedia(context))
        {
            await next().ConfigureAwait(false);
            return;
        }

        Guid userId;
        string deviceId;
        try
        {
            var identity = await ResolveIdentityAsync(context.HttpContext).ConfigureAwait(false);
            if (identity is null)
            {
                // Fail open by default: an unidentifiable request is Jellyfin's to
                // police, not ours. Strict setups can opt into fail-closed.
                if (kind == GateKind.MediaBytes && configuration.BlockUnidentifiedRequests)
                {
                    _logger.LogInformation("Stream gate denied unidentified media request on {Path}", context.HttpContext.Request.Path);
                    var unidentified = RenderMessage(MessageValues.Empty);
                    TagBlockedResponse(context.HttpContext, unidentified);
                    context.Result = Forbidden(unidentified.Text);
                    return;
                }

                await next().ConfigureAwait(false);
                return;
            }

            (userId, deviceId) = identity.Value;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream gate could not resolve the request identity; letting the request through");
            await next().ConfigureAwait(false);
            return;
        }

        var limit = _limitManager.GetEffectiveLimit(userId);
        if (limit <= 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var playingDeviceIds = GetPlayingDeviceIds(userId);

        if (kind == GateKind.StartupCheck)
        {
            if (_tracker.WouldAllow(userId, deviceId, limit, playingDeviceIds))
            {
                await next().ConfigureAwait(false);
                return;
            }

            _logger.LogInformation(
                "Stream gate denied playback negotiation. User: {UserId}, device: {DeviceId}, limit: {Limit}",
                userId,
                deviceId,
                limit);
            var negotiationBlock = ReportBlocked(context.HttpContext, userId, deviceId, limit, playingDeviceIds);
            context.Result = CreateStartupDenialResult(descriptor!.ActionName, negotiationBlock.Text);
            return;
        }

        if (!_tracker.TryEnter(userId, deviceId, limit, playingDeviceIds))
        {
            _logger.LogInformation(
                "Stream gate denied media request. User: {UserId}, device: {DeviceId}, limit: {Limit}, action: {Action}",
                userId,
                deviceId,
                limit,
                descriptor!.ActionName);
            var mediaBlock = ReportBlocked(context.HttpContext, userId, deviceId, limit, playingDeviceIds);
            context.Result = Forbidden(mediaBlock.Text);
            return;
        }

        // Admitted: if the reactive limiter stopped this device earlier, it is now a
        // legitimate stream again and its session must count normally.
        _enforcedDevices.ClearOnReentry(userId, deviceId);

        // Hold the slot for the whole response: a direct-play download is a single
        // request that can last hours and must keep counting the entire time.
        // The abort handle lets an admin kick cut this response immediately.
        var registration = _tracker.BeginRequest(userId, deviceId, context.HttpContext.Abort);
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            _tracker.EndRequest(userId, deviceId, registration);
        }
    }

    /// <summary>
    /// The response headers carrying a block's resolved message: percent-encoded UTF-8
    /// (<c>decodeURIComponent</c> in the web script), since header values must be ASCII.
    /// </summary>
    public const string TitleHeader = "X-StreamLimit-Title";

    /// <summary>
    /// See <see cref="TitleHeader"/>.
    /// </summary>
    public const string MessageHeader = "X-StreamLimit-Message";

    private static void TagBlockedResponse(HttpContext httpContext, (string Title, string Text) message)
    {
        // Lets the injected web script reliably recognize our block on any response
        // and show this block's resolved wording.
        var headers = httpContext.Response.Headers;
        headers["X-StreamLimit"] = "1";
        headers[TitleHeader] = Uri.EscapeDataString(message.Title);
        headers[MessageHeader] = Uri.EscapeDataString(message.Text);

        // Jellyfin's CORS policy exposes no headers, so a web client served from another
        // origin could not read ours. Expose them on cross-origin requests; merged at
        // response start so a value set later in the pipeline is kept.
        if (!string.IsNullOrEmpty(httpContext.Request.Headers.Origin))
        {
            httpContext.Response.OnStarting(
                static state =>
                {
                    var response = (HttpResponse)state;
                    response.Headers.AccessControlExposeHeaders = MergeExposedHeaders(response.Headers.AccessControlExposeHeaders.ToString());
                    return Task.CompletedTask;
                },
                httpContext.Response);
        }
    }

    /// <summary>
    /// Adds the plugin's headers to an <c>Access-Control-Expose-Headers</c> value.
    /// </summary>
    /// <param name="existing">The current value, may be empty.</param>
    /// <returns>The merged value.</returns>
    public static string MergeExposedHeaders(string? existing)
    {
        var names = (existing ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        foreach (var name in new[] { "X-StreamLimit", TitleHeader, MessageHeader })
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return string.Join(", ", names);
    }

    private static ObjectResult Forbidden(string text)
        => new(new { Message = text }) { StatusCode = StatusCodes.Status403Forbidden };

    private static IActionResult CreateStartupDenialResult(string actionName, string text)
    {
        // PlaybackInfo callers understand a typed error response and show a proper
        // "playback not allowed" dialog; other negotiation endpoints get a plain 403.
        if (actionName is "GetPlaybackInfo" or "GetPostedPlaybackInfo")
        {
            return new OkObjectResult(new PlaybackInfoResponse
            {
                MediaSources = Array.Empty<MediaSourceInfo>(),
                ErrorCode = PlaybackErrorCode.NotAllowed,
            });
        }

        return Forbidden(text);
    }

    private static string? GetClaim(ClaimsPrincipal? principal, string type)
        => principal?.Claims.FirstOrDefault(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Value;

    private async Task<(Guid UserId, string DeviceId)?> ResolveIdentityAsync(HttpContext httpContext)
    {
        // 1. Claims stamped by Jellyfin's authentication handler (value names are
        //    stable strings; Jellyfin-UserId is a dashless GUID).
        if (string.Equals(GetClaim(httpContext.User, "Jellyfin-IsApiKey"), "True", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var deviceId = GetClaim(httpContext.User, "Jellyfin-DeviceId");
        if (Guid.TryParse(GetClaim(httpContext.User, "Jellyfin-UserId"), out var userId) && !userId.Equals(Guid.Empty))
        {
            return (userId, NormalizeDeviceId(httpContext, deviceId));
        }

        // 2. Jellyfin's authorization resolver (works on anonymous endpoints too;
        //    memoized per request by the server).
        var authorizationInfo = await _authorizationContext.GetAuthorizationInfo(httpContext).ConfigureAwait(false);
        if (authorizationInfo.IsApiKey)
        {
            return null;
        }

        if (!authorizationInfo.UserId.Equals(Guid.Empty))
        {
            return (authorizationInfo.UserId, NormalizeDeviceId(httpContext, authorizationInfo.DeviceId));
        }

        // 3. Legacy token carriers (X-Emby-Token, X-MediaBrowser-Token, the Emby
        //    authorization scheme, X-Emby-Authorization, ?api_key=) are ignored by the
        //    resolver on Jellyfin 12 (EnableLegacyAuthorization defaults to false there),
        //    but the token still identifies a device: look it up directly so those
        //    requests stay gated.
        var token = RequestTokens.Find(httpContext.Request.Headers, httpContext.Request.Query);
        if (!string.IsNullOrEmpty(token))
        {
            var device = _deviceManager.GetDevices(new DeviceQuery { AccessToken = token }).Items.FirstOrDefault();
            if (device is not null && !device.UserId.Equals(Guid.Empty))
            {
                return (device.UserId, NormalizeDeviceId(httpContext, device.DeviceId));
            }
        }

        return null;
    }

    private static string NormalizeDeviceId(HttpContext httpContext, string? deviceId)
    {
        // Prefer the deviceId embedded in the stream URL: cast receivers (Chromecast,
        // DLNA renderers) fetch bytes with the SENDER's token, and counting them
        // under the token's device would double-count one physical stream.
        var fromQuery = httpContext.Request.Query["deviceId"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fromQuery))
        {
            return fromQuery;
        }

        return string.IsNullOrEmpty(deviceId) ? "(unknown-device)" : deviceId;
    }

    private bool IsThemeMedia(ResourceExecutingContext context)
    {
        if (!context.RouteData.Values.TryGetValue("itemId", out var raw)
            || !Guid.TryParse(raw?.ToString(), out var itemId))
        {
            return false;
        }

        try
        {
            var item = _libraryManager.GetItemById(itemId);
            return item?.ExtraType is ExtraType.ThemeSong or ExtraType.ThemeVideo;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream gate could not resolve item {ItemId}", itemId);
            return false;
        }
    }

    // Devices with live HTTP traffic are covered by the tracker regardless. Devices the
    // reactive limiter just stopped are left out: their client may keep a zombie
    // session alive (progress reports after Stop, no stopped report on error).
    private IReadOnlyCollection<string> GetPlayingDeviceIds(Guid userId)
        => PlayingSessions.GetPlayingDeviceIds(
            _sessionManager.Sessions,
            userId,
            DateTime.UtcNow,
            s => _enforcedDevices.IsEnforced(userId, PlayingSessions.StreamKey(s)));

    private static (string Title, string Text) RenderMessage(MessageValues values)
    {
        var configuration = Plugin.Instance?.Configuration;
        return (
            MessageTemplate.Render(configuration?.ResolvedMessageTitle ?? PluginConfiguration.DefaultMessageTitle, values),
            MessageTemplate.Render(configuration?.ResolvedMessageText ?? PluginConfiguration.DefaultMessageText, values));
    }

    // Everything a denial does besides answering: resolve the message for this block, tag
    // the response, and (throttled) push the display message, the activity entry and the
    // webhook. Never throws: the denial itself must always go out.
    private (string Title, string Text) ReportBlocked(
        HttpContext httpContext,
        Guid userId,
        string deviceId,
        int limit,
        IReadOnlyCollection<string> playingDeviceIds)
    {
        var message = RenderMessage(MessageValues.Empty);
        try
        {
            var userSessions = _sessionManager.Sessions.Where(s => s.UserId.Equals(userId)).ToList();
            var session = userSessions.FirstOrDefault(s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            var otherDevices = _tracker.GetHeldDevices(userId, playingDeviceIds)
                .Where(id => !string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // The session (when the device has one) knows the friendly device name and
            // client; the token's claims are the fallback for anonymous media requests.
            var blocked = new BlockedStream(
                userId,
                session?.UserName ?? userSessions.Select(s => s.UserName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? _userManager.GetUserById(userId)?.Username,
                deviceId,
                session?.DeviceName ?? GetClaim(httpContext.User, "Jellyfin-Device"),
                session?.Client ?? GetClaim(httpContext.User, "Jellyfin-Client"),
                httpContext.Connection.RemoteIpAddress?.ToString() ?? session?.RemoteEndPoint,
                limit,
                "HTTP gate",
                otherDevices.Count,
                MessageTemplate.DescribeDevices(otherDevices, userSessions));

            message = RenderMessage(blocked.MessageValues);
            NotifyDeviceThrottled(userId, deviceId, message);
            _activityLogger.LogBlocked(blocked);

            _webhookNotifier.NotifyBlocked(blocked);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream gate could not report the block");
        }

        TagBlockedResponse(httpContext, message);
        return message;
    }

    private void NotifyDeviceThrottled(Guid userId, string deviceId, (string Title, string Text) message)
    {
        // Off by default: the client already shows its own "not allowed" dialog on a
        // hard block, so an extra popup is redundant. See PluginConfiguration.ShowLimitPopup.
        if (Plugin.Instance?.Configuration.ShowLimitPopup != true)
        {
            return;
        }

        if (!_notifyThrottle.TryAcquire(KeyedThrottle.UserDeviceKey(userId, deviceId)))
        {
            return;
        }

        var sessions = _sessionManager.Sessions
            .Where(s => s.UserId.Equals(userId)
                        && string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Best effort, detached: denial responses must not wait on message delivery.
        _ = Task.Run(async () =>
        {
            foreach (var session in sessions)
            {
                await _enforcer.SendMessageAsync(session, message.Title, message.Text, "gate").ConfigureAwait(false);
            }
        });
    }
}
