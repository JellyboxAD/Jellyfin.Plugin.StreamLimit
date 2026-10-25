using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.StreamLimit.Notifications;

/// <summary>
/// One webhook notification.
/// </summary>
/// <param name="Event">The event: <c>stream_blocked</c>, <c>admin_kick</c> or <c>test</c>.</param>
/// <param name="Title">Short title.</param>
/// <param name="Body">One-line description.</param>
/// <param name="User">The user's name.</param>
/// <param name="UserId">The user id (dashless).</param>
/// <param name="Device">The device name.</param>
/// <param name="DeviceId">The device id.</param>
/// <param name="Client">The client application.</param>
/// <param name="Ip">The client's IP address.</param>
/// <param name="Active">The user's other streams counted by the plugin.</param>
/// <param name="Limit">The user's effective limit.</param>
/// <param name="Server">The server name.</param>
/// <param name="Timestamp">When it happened (UTC).</param>
public sealed record WebhookEvent(
    string Event,
    string Title,
    string Body,
    string? User,
    string? UserId,
    string? Device,
    string? DeviceId,
    string? Client,
    string? Ip,
    int? Active,
    int? Limit,
    string? Server,
    DateTimeOffset Timestamp);

/// <summary>
/// Pure helpers for webhook URLs and payloads.
/// </summary>
/// <remarks>
/// Discord webhooks get a Discord embed; every other URL gets a generic JSON document whose
/// <c>title</c>/<c>body</c>/<c>type</c> fields also make it a valid Apprise API
/// <c>/notify</c> request. Payloads never contain access tokens.
/// </remarks>
public static class WebhookPayloads
{
    /// <summary>The event of a blocked stream.</summary>
    public const string StreamBlocked = "stream_blocked";

    /// <summary>The event of a stream stopped by an administrator.</summary>
    public const string AdminKick = "admin_kick";

    /// <summary>The event of the configuration page's "Send test" button.</summary>
    public const string Test = "test";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Whether a configured webhook URL is acceptable: empty (disabled) or an absolute
    /// http/https URL.
    /// </summary>
    /// <param name="url">The configured URL.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidUrl(string? url)
        => string.IsNullOrWhiteSpace(url) || TryParse(url, out _);

    /// <summary>
    /// Parses a webhook URL, accepting absolute http/https URLs only.
    /// </summary>
    /// <param name="url">The configured URL.</param>
    /// <param name="uri">The parsed URL.</param>
    /// <returns>True when the URL can be used.</returns>
    public static bool TryParse(string? url, out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    /// <summary>
    /// Decides the webhook URL to store on a configuration save: the new one when valid,
    /// otherwise the previously stored one (or empty when that is invalid too).
    /// </summary>
    /// <param name="incoming">The URL being saved.</param>
    /// <param name="previous">The URL currently stored.</param>
    /// <returns>The URL to store, and whether the incoming one was ignored.</returns>
    public static (string Url, bool Ignored) SanitizeOnSave(string? incoming, string? previous)
    {
        if (IsValidUrl(incoming))
        {
            return (incoming?.Trim() ?? string.Empty, false);
        }

        return (IsValidUrl(previous) ? previous?.Trim() ?? string.Empty : string.Empty, true);
    }

    /// <summary>
    /// The scheme of a (possibly invalid) URL for logs, without anything that could be a
    /// secret: e.g. <c>ftp</c>, or <c>none</c> when there is no recognizable scheme.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>The scheme.</returns>
    public static string DescribeScheme(string? url)
    {
        var value = url?.Trim() ?? string.Empty;
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon > 32)
        {
            return "none";
        }

        var scheme = value[..colon];
        foreach (var c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return "none";
            }
        }

        return scheme.ToLowerInvariant();
    }

    /// <summary>
    /// Whether a URL is a Discord webhook.
    /// </summary>
    /// <param name="uri">The webhook URL.</param>
    /// <returns>True for discord.com / discordapp.com <c>/api/webhooks/...</c> URLs.</returns>
    public static bool IsDiscord(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var discordHost = host is "discord.com" or "discordapp.com"
                          || host.EndsWith(".discord.com", StringComparison.Ordinal)
                          || host.EndsWith(".discordapp.com", StringComparison.Ordinal);
        return discordHost && uri.AbsolutePath.StartsWith("/api/webhooks", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A safe description of a webhook URL for logs: scheme and host only (Discord and many
    /// other services carry their secret in the path or query).
    /// </summary>
    /// <param name="uri">The webhook URL.</param>
    /// <returns>The redacted URL.</returns>
    public static string Redact(Uri uri) => uri.Scheme + "://" + uri.Authority + "/…";

    /// <summary>
    /// Builds the request body for a URL.
    /// </summary>
    /// <param name="uri">The webhook URL.</param>
    /// <param name="evt">The event.</param>
    /// <returns>The JSON body.</returns>
    public static string Build(Uri uri, WebhookEvent evt)
        => IsDiscord(uri) ? BuildDiscord(evt) : BuildGeneric(evt);

    /// <summary>
    /// Builds the generic JSON body (Apprise API compatible).
    /// </summary>
    /// <param name="evt">The event.</param>
    /// <returns>The JSON body.</returns>
    public static string BuildGeneric(WebhookEvent evt)
        => JsonSerializer.Serialize(
            new
            {
                title = evt.Title,
                body = evt.Body,
                type = "info",
                @event = evt.Event,
                user = evt.User,
                userId = evt.UserId,
                device = evt.Device,
                deviceId = evt.DeviceId,
                client = evt.Client,
                ip = evt.Ip,
                active = evt.Active,
                limit = evt.Limit,
                server = evt.Server,
                timestamp = Iso(evt.Timestamp),
            },
            Options);

    /// <summary>
    /// Builds the Discord webhook body (one embed).
    /// </summary>
    /// <param name="evt">The event.</param>
    /// <returns>The JSON body.</returns>
    public static string BuildDiscord(WebhookEvent evt)
    {
        var activeLimit = evt.Active is null && evt.Limit is null
            ? "-"
            : string.Create(CultureInfo.InvariantCulture, $"{(evt.Active?.ToString(CultureInfo.InvariantCulture) ?? "?")} / {(evt.Limit?.ToString(CultureInfo.InvariantCulture) ?? "?")}");

        return JsonSerializer.Serialize(
            new
            {
                username = "Stream Limiter",
                embeds = new[]
                {
                    new
                    {
                        title = evt.Title,
                        description = evt.Body,
                        color = ColorOf(evt.Event),
                        fields = new[]
                        {
                            // Discord rejects empty field values.
                            Field("User", evt.User ?? evt.UserId),
                            Field("Device", evt.Device ?? evt.DeviceId),
                            Field("Client", evt.Client),
                            Field("IP", evt.Ip),
                            Field("Active/Limit", activeLimit),
                        },
                        timestamp = Iso(evt.Timestamp),
                    },
                },
            },
            Options);
    }

    private static object Field(string name, string? value)
        => new { name, value = string.IsNullOrWhiteSpace(value) ? "-" : value, inline = true };

    private static int ColorOf(string evt) => evt switch
    {
        StreamBlocked => 0xE53935,
        AdminKick => 0xFB8C00,
        _ => 0x00A4DC,
    };

    private static string Iso(DateTimeOffset timestamp)
        => timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
