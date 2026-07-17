namespace Jellyfin.Plugin.StreamLimit.Configuration;

/// <summary>
/// One-time, in-place upgrade of a persisted <see cref="PluginConfiguration"/> from
/// an older StreamLimit version to the current schema.
/// </summary>
/// <remarks>
/// Pure and side-effect free apart from mutating the passed configuration object, so
/// it is unit-testable without a running server. The caller persists the config when
/// <see cref="Migrate"/> returns true.
/// </remarks>
public static class ConfigurationMigration
{
    /// <summary>
    /// The current configuration schema version. Bump this when a future change needs
    /// its own migration step.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Upgrades <paramref name="configuration"/> in place to <see cref="CurrentVersion"/>.
    /// </summary>
    /// <param name="configuration">The configuration to migrate.</param>
    /// <returns>True when the configuration was changed and should be saved.</returns>
    public static bool Migrate(PluginConfiguration configuration)
    {
        if (configuration is null || configuration.ConfigVersion >= CurrentVersion)
        {
            return false;
        }

        // v0 -> v1: the pre-1.1 config was written by Newtonsoft and could hold
        // dashed user-id keys, values serialized as strings, or an empty string for a
        // cleared limit — the latter used to break the whole map at read time. Re-parse
        // tolerantly and re-serialize so the stored map is clean, keyed by dashless
        // lower-case ids, with invalid entries dropped.
        var limits = StreamLimitStore.ParseLimits(configuration.UserStreamLimits);
        configuration.UserStreamLimits = limits.Count == 0
            ? string.Empty
            : StreamLimitStore.SerializeLimits(limits);

        // Old installs may have left the message fields empty (issue #6: empty popup).
        if (string.IsNullOrWhiteSpace(configuration.MessageTitle))
        {
            configuration.MessageTitle = PluginConfiguration.DefaultMessageTitle;
        }

        if (string.IsNullOrWhiteSpace(configuration.MessageText))
        {
            configuration.MessageText = PluginConfiguration.DefaultMessageText;
        }

        // New enforcement options (EnableHardBlock, BlockPlaybackInfo, KillTranscodeJobs,
        // DefaultMaxStreams, ForceLogoutOnLimit, BlockUnidentifiedRequests) are absent
        // from an old XML, so XmlSerializer already left them at their constructor
        // defaults — no action needed here.

        configuration.ConfigVersion = CurrentVersion;
        return true;
    }
}
