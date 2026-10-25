using Jellyfin.Plugin.StreamLimit.Configuration;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class ConfigurationMigrationTests
{
    [Fact]
    public void Migrate_LegacyDashedKeys_AreNormalizedToDashless()
    {
        // Pre-1.1 configs stored dashed user ids (the old server did Replace("-","") at
        // lookup time). Migration must persist them dashless so the map is consistent.
        var config = new PluginConfiguration
        {
            UserStreamLimits = "{\"38a5a5bb-90ab-4590-837d-16bcb0b1c1f0\":2}",
            ConfigVersion = 0,
        };

        var changed = ConfigurationMigration.Migrate(config);

        Assert.True(changed);
        Assert.Equal("{\"38a5a5bb90ab4590837d16bcb0b1c1f0\":2}", config.UserStreamLimits);
        Assert.Equal(ConfigurationMigration.CurrentVersion, config.ConfigVersion);
    }

    [Fact]
    public void Migrate_PoisonedEntry_IsDroppedWithoutLosingValidLimits()
    {
        var config = new PluginConfiguration
        {
            UserStreamLimits = "{\"38a5a5bb90ab4590837d16bcb0b1c1f0\":\"\","
                               + "\"6c3f7c1e2d4b4a8f9e01abcdefabcdef\":\"3\"}",
            ConfigVersion = 0,
        };

        var changed = ConfigurationMigration.Migrate(config);

        Assert.True(changed);
        Assert.Equal("{\"6c3f7c1e2d4b4a8f9e01abcdefabcdef\":3}", config.UserStreamLimits);
    }

    [Fact]
    public void Migrate_EmptyConfig_StaysEmptyButIsStamped()
    {
        var config = new PluginConfiguration { UserStreamLimits = string.Empty, ConfigVersion = 0 };

        var changed = ConfigurationMigration.Migrate(config);

        Assert.True(changed);
        Assert.Equal(string.Empty, config.UserStreamLimits);
        Assert.Equal(ConfigurationMigration.CurrentVersion, config.ConfigVersion);
    }

    [Fact]
    public void Migrate_BackfillsEmptyMessages()
    {
        var config = new PluginConfiguration
        {
            MessageTitle = string.Empty,
            MessageText = "   ",
            ConfigVersion = 0,
        };

        ConfigurationMigration.Migrate(config);

        Assert.Equal("Stream Limit", config.MessageTitle);
        Assert.Equal("Active streams exceeded", config.MessageText);
    }

    [Fact]
    public void Migrate_KeepsExistingMessages()
    {
        var config = new PluginConfiguration
        {
            MessageTitle = "Custom title",
            MessageText = "Custom text",
            ConfigVersion = 0,
        };

        ConfigurationMigration.Migrate(config);

        Assert.Equal("Custom title", config.MessageTitle);
        Assert.Equal("Custom text", config.MessageText);
    }

    [Fact]
    public void Migrate_DefaultsForNewOptions_ArePreserved()
    {
        // A fresh PluginConfiguration already carries the safe defaults; migration
        // must not disturb them.
        var config = new PluginConfiguration { ConfigVersion = 0 };

        ConfigurationMigration.Migrate(config);

        Assert.True(config.EnableHardBlock);
        Assert.True(config.BlockPlaybackInfo);
        Assert.True(config.KillTranscodeJobs);
        Assert.False(config.ForceLogoutOnLimit);
        Assert.False(config.BlockUnidentifiedRequests);
        Assert.Equal(0, config.DefaultMaxStreams);
        Assert.True(config.ExemptAdministrators);
        Assert.True(config.LogBlocksToActivity);
        Assert.Equal(string.Empty, config.WebhookUrl);
        Assert.True(config.WebhookOnBlock);
        Assert.False(config.WebhookOnAdminKick);
    }

    [Fact]
    public void OptionsAddedIn1102_DefaultOnForExistingV1Config()
    {
        // A 1.1.0.x config (already v1) has no ExemptAdministrators/LogBlocksToActivity
        // element: XmlSerializer keeps the constructor defaults and no migration step runs.
        const string Xml = "<?xml version=\"1.0\"?>"
                           + "<StreamLimiterConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
                           + "<ConfigVersion>1</ConfigVersion>"
                           + "<UserStreamLimits>{\"38a5a5bb90ab4590837d16bcb0b1c1f0\":2}</UserStreamLimits>"
                           + "<EnableHardBlock>true</EnableHardBlock>"
                           + "</StreamLimiterConfiguration>";

        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var reader = new System.IO.StringReader(Xml);
        var config = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.True(config.ExemptAdministrators);
        Assert.True(config.LogBlocksToActivity);
        Assert.False(ConfigurationMigration.Migrate(config));
    }

    [Fact]
    public void Migrate_AlreadyCurrent_IsNoOp()
    {
        var config = new PluginConfiguration
        {
            UserStreamLimits = "{\"38a5a5bb-90ab-4590-837d-16bcb0b1c1f0\":2}",
            ConfigVersion = ConfigurationMigration.CurrentVersion,
        };

        var changed = ConfigurationMigration.Migrate(config);

        Assert.False(changed);
        // Untouched: an already-migrated config keeps whatever it had.
        Assert.Equal("{\"38a5a5bb-90ab-4590-837d-16bcb0b1c1f0\":2}", config.UserStreamLimits);
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var config = new PluginConfiguration
        {
            UserStreamLimits = "{\"38a5a5bb-90ab-4590-837d-16bcb0b1c1f0\":2}",
            ConfigVersion = 0,
        };

        Assert.True(ConfigurationMigration.Migrate(config));
        var afterFirst = config.UserStreamLimits;
        Assert.False(ConfigurationMigration.Migrate(config));
        Assert.Equal(afterFirst, config.UserStreamLimits);
    }
}
