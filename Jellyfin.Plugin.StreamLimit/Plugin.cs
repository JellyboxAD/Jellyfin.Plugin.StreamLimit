using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.StreamLimit.Configuration;
using Jellyfin.Plugin.StreamLimit.Notifications;
using Jellyfin.Plugin.StreamLimit.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;
        MigrateConfiguration(logger);
    }

    private void MigrateConfiguration(ILogger<Plugin> logger)
    {
        try
        {
            if (ConfigurationMigration.Migrate(Configuration))
            {
                SaveConfiguration();
                logger.LogInformation(
                    "StreamLimit configuration migrated to schema v{Version}",
                    ConfigurationMigration.CurrentVersion);
            }
        }
        catch (Exception ex)
        {
            // A migration failure must not stop the plugin from loading: the tolerant
            // runtime parser still reads the old config correctly.
            logger.LogError(ex, "StreamLimit configuration migration failed; keeping the existing config");
        }
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "StreamLimiter";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("d98fbe02-daf3-4c09-a832-4b4e1d07326c");

    /// <inheritdoc />
    public override string Description => "Limit the number of simultaneous streams per user.";

    /// <summary>
    /// Gets the name of the configuration file.
    /// </summary>
    public override string ConfigurationFileName => "Jellyfin.Plugin.StreamLimit.xml";

    /// <inheritdoc />
    /// <remarks>
    /// Ignores a webhook URL that is not an absolute http/https URL (the configuration page
    /// already refuses one): the previously stored URL is kept, a single warning is logged,
    /// and the rest of the configuration is saved normally.
    /// </remarks>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming)
        {
            var (url, ignored) = WebhookPayloads.SanitizeOnSave(incoming.WebhookUrl, Configuration.WebhookUrl);
            if (ignored)
            {
                // Scheme only: the rejected value could still contain a secret.
                _logger.LogWarning(
                    "StreamLimit: ignored invalid webhook URL (scheme: {Scheme}); keeping the previous one",
                    WebhookPayloads.DescribeScheme(incoming.WebhookUrl));
            }

            incoming.WebhookUrl = url;
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        // Leave the web client exactly as Jellyfin installed it: remove the tag that
        // versions up to 1.1.0.1 wrote into index.html on disk (no-op otherwise).
        WebInjectionService.RemoveLegacyBlock(ApplicationPaths.WebPath, _logger);
        base.OnUninstalling();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,

                // Sidebar entry in the dashboard: jellyfin-web labels it with DisplayName
                // (empty without it) and draws MenuIcon as a Material icon.
                EnableInMainMenu = true,
                DisplayName = "Stream Limiter",
                MenuIcon = "block",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        };
    }
}
