using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Extracts a Jellyfin access token from a request the server's own resolver did not
/// attribute to a user.
/// </summary>
/// <remarks>
/// Jellyfin 12 ignores the legacy token carriers by default (EnableLegacyAuthorization is
/// off): <c>X-Emby-Token</c>, <c>X-MediaBrowser-Token</c>, <c>X-Emby-Authorization</c>, the
/// <c>Emby</c> authorization scheme and the <c>api_key</c> query. Media endpoints are
/// anonymous, so such a request is still served — but its token still identifies a
/// device, and the gate must count it.
/// </remarks>
public static class RequestTokens
{
    /// <summary>
    /// Finds the access token of a request, in Jellyfin's order of precedence.
    /// </summary>
    /// <param name="headers">The request headers.</param>
    /// <param name="query">The request query string.</param>
    /// <returns>The token, or null when the request carries none.</returns>
    public static string? Find(IHeaderDictionary headers, IQueryCollection query)
    {
        return ParseAuthorizationToken(headers["Authorization"].FirstOrDefault())
               ?? ParseAuthorizationToken(headers["X-Emby-Authorization"].FirstOrDefault())
               ?? NonEmpty(headers["X-Emby-Token"].FirstOrDefault())
               ?? NonEmpty(headers["X-MediaBrowser-Token"].FirstOrDefault())
               ?? NonEmpty(query["api_key"].FirstOrDefault())
               ?? NonEmpty(query["ApiKey"].FirstOrDefault());
    }

    /// <summary>
    /// Reads the <c>Token="..."</c> parameter of a <c>MediaBrowser</c> or <c>Emby</c>
    /// authorization header value, e.g.
    /// <c>MediaBrowser Client="Android TV", Device="Shield", DeviceId="abc", Version="1.0", Token="xyz"</c>.
    /// </summary>
    /// <param name="headerValue">The header value.</param>
    /// <returns>The token, or null when the value has another scheme or no token.</returns>
    public static string? ParseAuthorizationToken(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var value = headerValue.AsSpan().Trim();
        var firstSpace = value.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return null;
        }

        var scheme = value[..firstSpace];
        if (!scheme.Equals("MediaBrowser", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("Emby", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Comma-separated Key="Value" pairs; values may contain commas inside quotes and
        // are URL-encoded by clients, exactly as Jellyfin's own parser expects.
        var rest = value[(firstSpace + 1)..];
        var inQuotes = false;
        var start = 0;
        for (var i = 0; i <= rest.Length; i++)
        {
            if (i < rest.Length && rest[i] == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (i < rest.Length && (rest[i] != ',' || inQuotes))
            {
                continue;
            }

            var pair = rest[start..i];
            start = i + 1;

            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            if (pair[..equals].Trim().Equals("Token", StringComparison.OrdinalIgnoreCase))
            {
                var token = WebUtility.UrlDecode(pair[(equals + 1)..].Trim().Trim('"').ToString());
                return NonEmpty(token);
            }
        }

        return null;
    }

    private static string? NonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
