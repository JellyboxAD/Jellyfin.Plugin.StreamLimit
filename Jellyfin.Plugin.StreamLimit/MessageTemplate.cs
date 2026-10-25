using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// The values a blocked-message template can refer to. A null value renders as an
/// empty string (e.g. when the blocked request could not be attributed to a user).
/// </summary>
/// <param name="User">The user's name.</param>
/// <param name="Active">The user's streams already counted by the plugin (the blocked one excluded).</param>
/// <param name="Limit">The user's effective limit.</param>
/// <param name="Devices">Comma-separated names of the user's other playing devices.</param>
public sealed record MessageValues(string? User, int? Active, int? Limit, string? Devices)
{
    /// <summary>
    /// Gets the values used when nothing is known: every placeholder renders empty.
    /// </summary>
    public static MessageValues Empty { get; } = new(null, null, null, null);
}

/// <summary>
/// Renders the placeholders of the blocked-message title and text:
/// <c>{{user}}</c>, <c>{{active}}</c>, <c>{{limit}}</c> and <c>{{devices}}</c>.
/// </summary>
/// <remarks>
/// Case-insensitive, whitespace inside the braces is allowed, unknown placeholders are left
/// untouched. The result is plain text: every consumer (web popup via textContent, response
/// headers, Jellyfin display messages) treats it as text, never as markup.
/// </remarks>
public static class MessageTemplate
{
    private static readonly Regex Placeholder = new(
        @"\{\{\s*(user|active|limit|devices)\s*\}\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Replaces the known placeholders of a template.
    /// </summary>
    /// <param name="template">The configured title or text.</param>
    /// <param name="values">The values of this block.</param>
    /// <returns>The rendered text.</returns>
    public static string Render(string? template, MessageValues values)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return Placeholder.Replace(template, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "user" => values.User ?? string.Empty,
            "active" => values.Active?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "limit" => values.Limit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            _ => values.Devices ?? string.Empty,
        });
    }

    /// <summary>
    /// Describes devices for <c>{{devices}}</c>: the device name of the user's session on
    /// each device, falling back to the device id; distinct and sorted.
    /// </summary>
    /// <param name="deviceIds">The device ids to describe.</param>
    /// <param name="userSessions">The user's sessions (any state), used for the names.</param>
    /// <returns>The comma-separated names, empty when there are none.</returns>
    public static string DescribeDevices(IEnumerable<string> deviceIds, IEnumerable<SessionInfo> userSessions)
    {
        var sessions = userSessions as IReadOnlyCollection<SessionInfo> ?? userSessions.ToList();
        var names = deviceIds
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => sessions.FirstOrDefault(s => string.Equals(s.DeviceId, id, StringComparison.OrdinalIgnoreCase)
                                                       && !string.IsNullOrWhiteSpace(s.DeviceName))?.DeviceName ?? id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", names);
    }
}
