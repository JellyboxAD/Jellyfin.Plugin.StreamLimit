using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.StreamLimit.Notifications;

/// <summary>
/// One session the blocked message is sent to.
/// </summary>
/// <param name="Session">The session.</param>
/// <param name="TimeoutMs">The display timeout: null shows a dialog the user dismisses (web clients), a value shows a timed notice.</param>
/// <param name="Reason">Why it is targeted: "web", "native" or "companion".</param>
public sealed record MessageTarget(SessionInfo Session, long? TimeoutMs, string Reason);

/// <summary>
/// Decides which sessions receive the server display message of a block. Pure, so it is
/// unit-testable.
/// </summary>
/// <remarks>
/// Web-based clients (jellyfin-web in a browser, the jellyfin-android and jellyfin-ios apps,
/// which are jellyfin-web in a webview) already show the plugin's injected popup, so the server message is
/// sent to them only with <c>ShowLimitPopup</c>. Native apps (Android TV, Swiftfin, Streamyfin,
/// Findroid, AFinity, Kodi...) cannot run that popup: without a server message they show nothing
/// meaningful (an endless spinner), so they get it with <c>NotifyNativeApps</c>.
/// </remarks>
public static class MessageTargets
{
    /// <summary>
    /// Display timeout of the message on native apps. Without a timeout jellyfin-web shows a
    /// modal dialog; native apps show a toast or a timed notice (AFinity hides it after 6 s
    /// when no timeout is given).
    /// </summary>
    public const long NativeTimeoutMs = 10000;

    // Client names of the web-based apps, as they report them (checked against their source):
    // jellyfin-web "Jellyfin Web"; jellyfin-android "Jellyfin for Android" (before mid-2026
    // "Jellyfin Android"); jellyfin-ios "Jellyfin iOS" / "Jellyfin iPadOS" (formerly
    // "Jellyfin (iOS)"). Jellyfin Media Player / Jellyfin Desktop may run a bundled web client
    // without the injected popup, so they are treated as native: a timed notice at worst
    // duplicates the popup, while no message at all would leave the user in front of a spinner.
    private static readonly string[] WebBasedClients =
    {
        "Jellyfin Web",
        "Jellyfin Android",
        "Jellyfin for Android",
        "Jellyfin iOS",
        "Jellyfin iPadOS",
        "Jellyfin (iOS)",
        "Jellyfin (iPadOS)",
        "Jellyfin for iOS",
        "Jellyfin for iPadOS",
        "Jellyfin Mobile",
    };

    /// <summary>
    /// Whether a client runs jellyfin-web (and with it the plugin's injected popup).
    /// </summary>
    /// <param name="client">The session's client name.</param>
    /// <returns>True for web-based clients.</returns>
    public static bool IsWebBased(string? client)
    {
        if (string.IsNullOrWhiteSpace(client))
        {
            return false;
        }

        // "Jellyfin for Android TV" (formerly "Android TV") is the native TV app.
        if (client.Contains("Android TV", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var name in WebBasedClients)
        {
            if (client.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the device id of the web UI session that goes with a jellyfin-android native-player
    /// device id. The app's native player reports under <c>baseId + userId</c> (the user id with
    /// dashes, see jellyfin-android ApiClientController) on a session without a websocket, while
    /// the app's web UI (which can show a message) uses <c>baseId</c>.
    /// </summary>
    /// <param name="deviceId">The blocked device id.</param>
    /// <param name="userId">The user.</param>
    /// <returns>The base device id, or null when the device id does not end with the user id.</returns>
    public static string? CompanionDeviceId(string? deviceId, Guid userId)
    {
        if (string.IsNullOrEmpty(deviceId) || userId.Equals(Guid.Empty))
        {
            return null;
        }

        foreach (var suffix in new[] { userId.ToString("D"), userId.ToString("N") })
        {
            if (deviceId.Length > suffix.Length && deviceId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return deviceId[..^suffix.Length];
            }
        }

        return null;
    }

    /// <summary>
    /// Selects the sessions that receive the message of a block.
    /// </summary>
    /// <param name="sessions">All server sessions.</param>
    /// <param name="userId">The blocked user.</param>
    /// <param name="deviceId">The blocked device id.</param>
    /// <param name="showLimitPopup">Also send to web-based clients (they show the injected popup already).</param>
    /// <param name="notifyNativeApps">Send to native apps, and to the web UI of a jellyfin-android native player.</param>
    /// <returns>The targets, one per session.</returns>
    public static IReadOnlyList<MessageTarget> Select(
        IEnumerable<SessionInfo> sessions,
        Guid userId,
        string? deviceId,
        bool showLimitPopup,
        bool notifyNativeApps)
    {
        var result = new List<MessageTarget>();
        if (string.IsNullOrEmpty(deviceId) || (!showLimitPopup && !notifyNativeApps))
        {
            return result;
        }

        var companion = CompanionDeviceId(deviceId, userId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!session.UserId.Equals(userId) || string.IsNullOrEmpty(session.DeviceId) || !seen.Add(session.Id))
            {
                continue;
            }

            if (string.Equals(session.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsWebBased(session.Client))
                {
                    if (notifyNativeApps)
                    {
                        result.Add(new MessageTarget(session, NativeTimeoutMs, "native"));
                    }
                }
                else if (showLimitPopup)
                {
                    result.Add(new MessageTarget(session, null, "web"));
                }
            }
            else if (companion is not null && string.Equals(session.DeviceId, companion, StringComparison.OrdinalIgnoreCase))
            {
                // The native player cannot show the injected popup and has no websocket: tell
                // the app's web UI instead, as a dialog that waits until the user is back.
                result.Add(new MessageTarget(session, null, "companion"));
            }
        }

        return result;
    }
}
