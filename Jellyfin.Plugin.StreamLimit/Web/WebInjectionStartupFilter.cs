using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit.Web;

/// <summary>
/// Adds the plugin's client script to the web client's index.html as it is served, via
/// a middleware placed in front of Jellyfin's pipeline.
/// </summary>
/// <remarks>
/// Nothing is written to the web folder, so it works with read-only installs (Docker,
/// distro packages), survives web-client updates, and the
/// <see cref="Configuration.PluginConfiguration.EnableWebMessage"/> setting is read per
/// request: toggling it needs no restart. The middleware is defensive: it only touches
/// a 200 text/html response of the index page and serves the original bytes on any error
/// of its own.
/// </remarks>
public sealed class WebInjectionStartupFilter : IStartupFilter
{
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<WebInjectionStartupFilter> _logger;
    private int _loggedOnce;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="configurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public WebInjectionStartupFilter(IServerConfigurationManager configurationManager, ILogger<WebInjectionStartupFilter> logger)
    {
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Jellyfin maps its whole pipeline under the BaseUrl it read at startup (a
            // BaseUrl change needs a restart), so reading it once here matches the server.
            var baseUrl = ReadBaseUrl();

            // Registered ahead of the rest of the pipeline, so this runs outermost: the
            // stripped Accept-Encoding then reliably yields an uncompressed body.
            app.Use((context, nextMiddleware) => InvokeAsync(context, nextMiddleware, baseUrl));
            next(app);
        };
    }

    private string ReadBaseUrl()
    {
        try
        {
            return _configurationManager.GetNetworkConfiguration().BaseUrl ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamLimit: could not read the server BaseUrl; assuming none for the web message");
            return string.Empty;
        }
    }

    private async Task InvokeAsync(HttpContext context, RequestDelegate next, string baseUrl)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method)
            || !WebInjection.IsIndexRequest(request.PathBase.Add(request.Path).Value, baseUrl)
            || Plugin.Instance?.Configuration.EnableWebMessage != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Ask for a complete, plain 200 we can rewrite: no compression, no partial
        // content, no 304 against a validator of the unmodified file.
        request.Headers.Remove("Accept-Encoding");
        request.Headers.Remove("Range");
        request.Headers.Remove("If-Range");
        request.Headers.Remove("If-None-Match");
        request.Headers.Remove("If-Modified-Since");

        var response = context.Response;
        var originalBody = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            // A downstream failure is not ours to swallow: the real response has not
            // started, so the host's exception handler can still render it.
            response.Body = originalBody;
        }

        var original = buffer.ToArray();
        byte[]? rewritten = null;
        try
        {
            if (!response.HasStarted
                && response.StatusCode == StatusCodes.Status200OK
                && (response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                var html = Encoding.UTF8.GetString(original);
                var injected = WebInjection.Add(html);
                if (!string.Equals(html, injected, StringComparison.Ordinal))
                {
                    rewritten = Encoding.UTF8.GetBytes(injected);

                    // The body changed: the static handler's validators no longer apply,
                    // and range requests on the rewritten document are not supported.
                    response.ContentLength = rewritten.Length;
                    response.Headers.Remove("ETag");
                    response.Headers.Remove("Last-Modified");
                    response.Headers.Remove("Accept-Ranges");

                    // Never let a client keep this shell: Android webviews in particular
                    // hold on to a stale index.html (old script, or none after toggling).
                    response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    response.Headers.Pragma = "no-cache";
                    response.Headers.Expires = "0";

                    if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                    {
                        _logger.LogInformation("StreamLimit: web message script added to the web client (served on the fly)");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Never break the web client: serve the page exactly as Jellyfin produced it.
            if (rewritten is not null && !response.HasStarted)
            {
                response.ContentLength = original.Length;
            }

            rewritten = null;
            _logger.LogWarning(ex, "StreamLimit: could not add the web message script; serving the original page");
        }

        // Anything else (304, redirect, non-HTML, an index without </body>) passes
        // through byte for byte with its original headers.
        var output = rewritten ?? original;
        if (output.Length > 0)
        {
            await originalBody.WriteAsync(output, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
