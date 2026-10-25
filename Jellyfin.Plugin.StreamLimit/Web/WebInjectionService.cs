using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit.Web;

/// <summary>
/// Removes the script block that versions up to 1.1.0.1 wrote into the web client's
/// index.html on disk. The script is now added on the fly as the page is served (see
/// <see cref="WebInjectionStartupFilter"/>), so the file must go back to its pristine
/// state: otherwise turning the web message off would keep serving the old tag.
/// </summary>
public sealed class WebInjectionService : IHostedService
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<WebInjectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebInjectionService"/> class.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public WebInjectionService(IApplicationPaths appPaths, ILogger<WebInjectionService> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        RemoveLegacyBlock(_appPaths.WebPath, _logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Removes the plugin's block from index.html on disk, when present. Best effort: an
    /// unwritable web folder only logs a warning.
    /// </summary>
    /// <param name="webPath">The web client folder.</param>
    /// <param name="logger">The logger.</param>
    public static void RemoveLegacyBlock(string? webPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(webPath))
        {
            return;
        }

        var indexPath = Path.Combine(webPath, "index.html");
        try
        {
            if (!File.Exists(indexPath))
            {
                return;
            }

            var original = File.ReadAllText(indexPath);
            if (!WebInjection.IsInjected(original))
            {
                return;
            }

            File.WriteAllText(indexPath, WebInjection.Remove(original));
            logger.LogInformation("StreamLimit: removed the old on-disk web message tag from {Path}", indexPath);
        }
        catch (Exception ex)
        {
            // Harmless for the block itself; the served page still gets exactly one
            // script block, since the on-the-fly injection replaces any existing one.
            logger.LogWarning(ex, "StreamLimit: could not remove the old web message tag from {Path}", indexPath);
        }
    }
}
