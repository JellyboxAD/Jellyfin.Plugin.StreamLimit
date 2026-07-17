using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit.Web;

/// <summary>
/// Injects (or removes) the plugin's client script into the Jellyfin web client's
/// index.html at startup, so a blocked stream can show a single custom message on
/// web clients. Runs once at boot: the edit is re-applied on every start, which
/// makes it survive Jellyfin/web updates that overwrite index.html.
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
        var enabled = Plugin.Instance?.Configuration.EnableWebMessage ?? false;
        Apply(enabled);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Apply(bool enabled)
    {
        var indexPath = GetIndexPath();
        if (indexPath is null)
        {
            _logger.LogWarning("StreamLimit: web client index.html not found; the web message is unavailable");
            return;
        }

        try
        {
            var original = File.ReadAllText(indexPath);
            var updated = enabled ? WebInjection.Add(original) : WebInjection.Remove(original);

            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(indexPath, updated);
                _logger.LogInformation(
                    "StreamLimit: web message {State} in {Path}",
                    enabled ? "injected" : "removed",
                    indexPath);
            }
        }
        catch (Exception ex)
        {
            // A read-only web folder (common in Docker) must not break startup; the
            // block still works, clients just show their own dialog.
            _logger.LogWarning(ex, "StreamLimit: could not update the web client; falling back to the native dialog");
        }
    }

    private string? GetIndexPath()
    {
        var webPath = _appPaths.WebPath;
        if (string.IsNullOrEmpty(webPath))
        {
            return null;
        }

        var indexPath = Path.Combine(webPath, "index.html");
        return File.Exists(indexPath) ? indexPath : null;
    }
}
