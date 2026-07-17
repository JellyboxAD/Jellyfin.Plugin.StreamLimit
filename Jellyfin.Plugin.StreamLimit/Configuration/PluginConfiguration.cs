using System.Xml.Serialization;

namespace Jellyfin.Plugin.StreamLimit.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
[XmlRoot("StreamLimiterConfiguration")]
public class PluginConfiguration : MediaBrowser.Model.Plugins.BasePluginConfiguration
{
    /// <summary>
    /// Default message title, used when none is configured.
    /// </summary>
    public const string DefaultMessageTitle = "Stream Limit";

    /// <summary>
    /// Default message text, used when none is configured.
    /// </summary>
    public const string DefaultMessageText = "Active streams exceeded";

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        UserStreamLimits = string.Empty;
        MessageTitle = DefaultMessageTitle;
        MessageText = DefaultMessageText;
        DefaultMaxStreams = 0;
        KillTranscodeJobs = true;
        ForceLogoutOnLimit = false;
        EnableHardBlock = true;
        BlockPlaybackInfo = true;
        BlockUnidentifiedRequests = false;
        ShowLimitPopup = false;
        EnableWebMessage = true;
        ConfigVersion = 0;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin injects a small script into
    /// the Jellyfin web client so that a blocked stream shows a single clean custom
    /// message (title + text) instead of the client's built-in "playback not allowed"
    /// dialog. Web clients only (browser, Android webview, iOS web): native apps
    /// (Swiftfin, etc.) never load the web client and are unaffected. Falls back
    /// silently to the native dialog if the web files cannot be written.
    /// </summary>
    [XmlElement(ElementName = "EnableWebMessage")]
    public bool EnableWebMessage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin pushes its own custom
    /// popup (title + text) when a stream is blocked. Off by default: a hard block
    /// already makes the client show its own "playback not allowed" dialog, which a
    /// plugin cannot suppress, so pushing an extra popup only adds a redundant second
    /// dialog on the web-based clients that receive it (native clients drop it). Turn
    /// this on only if you specifically want your custom wording on top of the
    /// client's own dialog.
    /// </summary>
    [XmlElement(ElementName = "ShowLimitPopup")]
    public bool ShowLimitPopup { get; set; }

    /// <summary>
    /// Gets or sets the schema version of this configuration. 0 means "pre-1.1 or
    /// never migrated": a config loaded from an old install has no ConfigVersion
    /// element, so XmlSerializer leaves this at the constructor default (0) and the
    /// one-time migration runs. See <see cref="ConfigurationMigration"/>.
    /// </summary>
    [XmlElement(ElementName = "ConfigVersion")]
    public int ConfigVersion { get; set; }

    /// <summary>
    /// Gets or sets the JSON string containing user stream limits.
    /// Format: object mapping user IDs (GUID without dashes) to maximum allowed streams.
    /// </summary>
    [XmlElement(ElementName = "UserStreamLimits")]
    public string UserStreamLimits { get; set; }

    /// <summary>
    /// Gets or sets the title of the message shown when the stream limit is exceeded.
    /// </summary>
    [XmlElement(ElementName = "MessageTitle")]
    public string MessageTitle { get; set; }

    /// <summary>
    /// Gets or sets the text of the message shown when the stream limit is exceeded.
    /// </summary>
    [XmlElement(ElementName = "MessageText")]
    public string MessageText { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of simultaneous streams applied to users
    /// that have no explicit limit configured. 0 or less means unlimited.
    /// </summary>
    [XmlElement(ElementName = "DefaultMaxStreams")]
    public int DefaultMaxStreams { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether active transcode jobs of the offending
    /// stream are killed server side. This stops clients that ignore the remote
    /// stop command (clients without remote-control support).
    /// </summary>
    [XmlElement(ElementName = "KillTranscodeJobs")]
    public bool KillTranscodeJobs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the offending device is logged out
    /// when the limit is exceeded. Logging out deletes the device record (its access
    /// token is revoked and the entry disappears from Settings > Devices). This is
    /// the most aggressive enforcement and also cuts direct-play HTTP streams, but
    /// it forces the user to sign in again on that device.
    /// </summary>
    [XmlElement(ElementName = "ForceLogoutOnLimit")]
    public bool ForceLogoutOnLimit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether media requests over the limit are
    /// rejected at the HTTP level (403 before any media byte is served). This works
    /// for every client, including ones that ignore remote stop commands, because
    /// the stream never starts.
    /// </summary>
    [XmlElement(ElementName = "EnableHardBlock")]
    public bool EnableHardBlock { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether PlaybackInfo requests are answered
    /// with a "not allowed" playback error when the user is at their limit, so
    /// clients show a proper error dialog before attempting to stream.
    /// </summary>
    [XmlElement(ElementName = "BlockPlaybackInfo")]
    public bool BlockPlaybackInfo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether media requests whose user cannot be
    /// identified (no usable token) are rejected instead of allowed. Off by default:
    /// fail-open avoids breaking exotic-but-legitimate clients; strict setups can
    /// enable it to close the anonymous-request loophole.
    /// </summary>
    [XmlElement(ElementName = "BlockUnidentifiedRequests")]
    public bool BlockUnidentifiedRequests { get; set; }

    /// <summary>
    /// Gets the message title to display, falling back to the default when empty.
    /// </summary>
    [XmlIgnore]
    public string ResolvedMessageTitle
        => string.IsNullOrWhiteSpace(MessageTitle) ? DefaultMessageTitle : MessageTitle;

    /// <summary>
    /// Gets the message text to display, falling back to the default when empty.
    /// </summary>
    [XmlIgnore]
    public string ResolvedMessageText
        => string.IsNullOrWhiteSpace(MessageText) ? DefaultMessageText : MessageText;
}
