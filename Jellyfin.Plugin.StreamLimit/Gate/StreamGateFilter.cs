using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.StreamLimit.Configuration;
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
/// ignore remote stop commands (Swiftfin, Infuse, external players): a stream that
/// is never served cannot be played. The filter fails open — requests whose user
/// cannot be identified are never blocked.
/// </remarks>
public sealed class StreamGateFilter : IAsyncResourceFilter
{
    private static readonly TimeSpan NotifyThrottle = TimeSpan.FromSeconds(30);

    private readonly StreamSlotTracker _tracker;
    private readonly StreamLimitManager _limitManager;
    private readonly ISessionManager _sessionManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IDeviceManager _deviceManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StreamGateFilter> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastNotified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamGateFilter"/> class.
    /// </summary>
    /// <param name="tracker">The slot tracker.</param>
    /// <param name="limitManager">The stream limit manager.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="authorizationContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    /// <param name="deviceManager">Instance of the <see cref="IDeviceManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public StreamGateFilter(
        StreamSlotTracker tracker,
        StreamLimitManager limitManager,
        ISessionManager sessionManager,
        IAuthorizationContext authorizationContext,
        IDeviceManager deviceManager,
        ILibraryManager libraryManager,
        ILogger<StreamGateFilter> logger)
    {
        _tracker = tracker;
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
                    context.Result = new ObjectResult(new { Message = GetDenialMessage() }) { StatusCode = StatusCodes.Status403Forbidden };
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
            NotifyDeviceThrottled(userId, deviceId);
            TagBlockedResponse(context);
            context.Result = CreateStartupDenialResult(descriptor!.ActionName);
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
            NotifyDeviceThrottled(userId, deviceId);
            TagBlockedResponse(context);
            context.Result = new ObjectResult(new { Message = GetDenialMessage() }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        // Hold the slot for the whole response: a direct-play download is a single
        // request that can last hours and must keep counting the entire time.
        _tracker.BeginRequest(userId, deviceId);
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            _tracker.EndRequest(userId, deviceId);
        }
    }

    private static void TagBlockedResponse(ResourceExecutingContext context)
    {
        // Lets the injected web script reliably recognize our block on any response
        // (headers are readable via XHR getResponseHeader on same-origin requests).
        context.HttpContext.Response.Headers["X-StreamLimit"] = "1";
    }

    private IActionResult CreateStartupDenialResult(string actionName)
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

        return new ObjectResult(new { Message = GetDenialMessage() }) { StatusCode = StatusCodes.Status403Forbidden };
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

        // 3. Legacy ?api_key= tokens are ignored by the resolver on Jellyfin 12
        //    (EnableLegacyAuthorization defaults to false there), but the token still
        //    identifies a device: look it up directly so those URLs stay gated.
        var token = httpContext.Request.Query["api_key"].FirstOrDefault()
                    ?? httpContext.Request.Query["ApiKey"].FirstOrDefault();
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

    private IReadOnlyCollection<string> GetPlayingDeviceIds(Guid userId)
    {
        // Only sessions with a recent playback check-in count: a crashed client's
        // session keeps NowPlayingItem set until Jellyfin's 5-10 minute reaper runs,
        // and must not lock the user out that long. Devices with live HTTP traffic
        // are covered by the tracker regardless.
        var staleBefore = DateTime.UtcNow.AddMinutes(-3);
        return _sessionManager.Sessions
            .Where(s => s.UserId.Equals(userId)
                        && s.NowPlayingItem is not null
                        && s.IsActive
                        && s.LastPlaybackCheckIn >= staleBefore
                        && !string.IsNullOrEmpty(s.DeviceId))
            .Select(s => s.DeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetDenialMessage()
        => Plugin.Instance?.Configuration.ResolvedMessageText ?? PluginConfiguration.DefaultMessageText;

    private void NotifyDeviceThrottled(Guid userId, string deviceId)
    {
        // Off by default: the client already shows its own "not allowed" dialog on a
        // hard block, so an extra popup is redundant. See PluginConfiguration.ShowLimitPopup.
        if (Plugin.Instance?.Configuration.ShowLimitPopup != true)
        {
            return;
        }

        var key = StreamLimitStore.NormalizeUserKey(userId) + "|" + deviceId;
        var now = DateTimeOffset.UtcNow;
        var last = _lastNotified.GetOrAdd(key, DateTimeOffset.MinValue);
        if (now - last < NotifyThrottle || !_lastNotified.TryUpdate(key, now, last))
        {
            return;
        }

        // Opportunistic pruning keeps the throttle map from growing forever on
        // servers with many rotating browser device ids.
        foreach (var (staleKey, seenAt) in _lastNotified)
        {
            if (now - seenAt > TimeSpan.FromMinutes(10))
            {
                _lastNotified.TryRemove(staleKey, out _);
            }
        }

        var configuration = Plugin.Instance?.Configuration;
        var title = configuration?.ResolvedMessageTitle ?? PluginConfiguration.DefaultMessageTitle;
        var text = configuration?.ResolvedMessageText ?? PluginConfiguration.DefaultMessageText;

        var sessions = _sessionManager.Sessions
            .Where(s => s.UserId.Equals(userId)
                        && string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Best effort, detached: denial responses must not wait on message delivery.
        _ = Task.Run(async () =>
        {
            foreach (var session in sessions)
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
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to notify session {SessionId} about the stream limit", session.Id);
                }
            }
        });
    }
}
