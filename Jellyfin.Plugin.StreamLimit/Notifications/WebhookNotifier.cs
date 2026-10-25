using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamLimit.Gate;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit.Notifications;

/// <summary>
/// Posts webhook notifications (Discord, Apprise, or any JSON endpoint) when a stream is
/// blocked or stopped by an administrator.
/// </summary>
/// <remarks>
/// Sending is detached from the request that triggered it, times out after
/// <see cref="Timeout"/> and only logs failures: a slow or broken webhook never delays or
/// breaks enforcement. Block notifications are throttled to one per user and device every
/// <see cref="ThrottleInterval"/>, like the activity log. The URL is never logged in full
/// (Discord keeps its secret in the path).
/// </remarks>
public sealed class WebhookNotifier
{
    /// <summary>
    /// Request timeout.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Minimum time between two block notifications for the same user and device.
    /// </summary>
    public static readonly TimeSpan ThrottleInterval = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<WebhookNotifier> _logger;
    private readonly KeyedThrottle _throttle = new(ThrottleInterval);

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookNotifier"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="applicationHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public WebhookNotifier(IHttpClientFactory httpClientFactory, IServerApplicationHost applicationHost, ILogger<WebhookNotifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>
    /// Notifies a blocked stream, unless no webhook is set, block notifications are off, or
    /// this user and device were notified recently.
    /// </summary>
    /// <param name="blocked">The blocked stream.</param>
    public void NotifyBlocked(BlockedStream blocked)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null
            || !configuration.WebhookOnBlock
            || !WebhookPayloads.TryParse(configuration.WebhookUrl, out var uri)
            || !_throttle.TryAcquire(KeyedThrottle.UserDeviceKey(blocked.UserId, blocked.DeviceId)))
        {
            return;
        }

        var user = blocked.UserName ?? StreamLimitStore.NormalizeUserKey(blocked.UserId);
        var device = string.IsNullOrWhiteSpace(blocked.DeviceName) ? blocked.DeviceId : blocked.DeviceName;
        var evt = CreateEvent(
            WebhookPayloads.StreamBlocked,
            "Stream blocked",
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{user} was blocked from streaming on {device} (limit {blocked.Limit}, {blocked.Active} already playing)."),
            blocked);
        SendDetached(uri, evt);
    }

    /// <summary>
    /// Notifies a stream stopped from the admin live view, when enabled.
    /// </summary>
    /// <param name="adminName">The administrator's name.</param>
    /// <param name="stopped">The stopped stream.</param>
    public void NotifyAdminKick(string? adminName, BlockedStream stopped)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null
            || !configuration.WebhookOnAdminKick
            || !WebhookPayloads.TryParse(configuration.WebhookUrl, out var uri))
        {
            return;
        }

        var user = stopped.UserName ?? StreamLimitStore.NormalizeUserKey(stopped.UserId);
        var device = string.IsNullOrWhiteSpace(stopped.DeviceName) ? stopped.DeviceId : stopped.DeviceName;
        var evt = CreateEvent(
            WebhookPayloads.AdminKick,
            "Stream stopped by an administrator",
            $"{adminName ?? "An administrator"} stopped the stream of {user} on {device}.",
            stopped);
        SendDetached(uri, evt);
    }

    /// <summary>
    /// Sends a test notification and waits for the answer.
    /// </summary>
    /// <param name="uri">The webhook URL.</param>
    /// <returns>Whether the endpoint answered 2xx, its status code, and an error description.</returns>
    public async Task<(bool Ok, int? Status, string? Error)> SendTestAsync(Uri uri)
    {
        var server = _applicationHost.FriendlyName;
        var evt = new WebhookEvent(
            WebhookPayloads.Test,
            "Stream Limiter test",
            $"Test notification from Stream Limiter on {server}.",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            server,
            DateTimeOffset.UtcNow);
        return await SendAsync(uri, evt).ConfigureAwait(false);
    }

    private WebhookEvent CreateEvent(string eventName, string title, string body, BlockedStream stream)
        => new(
            eventName,
            title,
            body,
            stream.UserName,
            StreamLimitStore.NormalizeUserKey(stream.UserId),
            stream.DeviceName,
            stream.DeviceId,
            stream.Client,
            stream.RemoteAddress,
            stream.Active,
            stream.Limit,
            _applicationHost.FriendlyName,
            DateTimeOffset.UtcNow);

    private void SendDetached(Uri uri, WebhookEvent evt)
        => _ = Task.Run(async () =>
        {
            var (ok, status, error) = await SendAsync(uri, evt).ConfigureAwait(false);
            if (!ok)
            {
                _logger.LogWarning(
                    "StreamLimit: webhook {Url} failed for {Event} (status {Status}): {Error}",
                    WebhookPayloads.Redact(uri),
                    evt.Event,
                    status,
                    error);
            }
        });

    private async Task<(bool Ok, int? Status, string? Error)> SendAsync(Uri uri, WebhookEvent evt)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            using var content = new StringContent(WebhookPayloads.Build(uri, evt), Encoding.UTF8, "application/json");
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return (response.IsSuccessStatusCode, status, response.IsSuccessStatusCode ? null : response.ReasonPhrase);
        }
        catch (OperationCanceledException)
        {
            return (false, null, "timed out after " + Timeout.TotalSeconds + " s");
        }
        catch (Exception ex)
        {
            // Message only: an exception's ToString could carry the full URL.
            return (false, null, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
