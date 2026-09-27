using System;

namespace Jellyfin.Plugin.StreamLimit.Web;

/// <summary>
/// Pure, testable helpers to add or remove the plugin's script tag inside the
/// Jellyfin web client's index.html. Kept side-effect free so it can be unit tested
/// without touching the filesystem.
/// </summary>
public static class WebInjection
{
    /// <summary>
    /// Marker comment wrapping the injected block so it can be found and removed
    /// idempotently across restarts and web-client updates.
    /// </summary>
    public const string BeginMarker = "<!-- BEGIN Jellyfin.Plugin.StreamLimit -->";

    /// <summary>
    /// End marker of the injected block.
    /// </summary>
    public const string EndMarker = "<!-- END Jellyfin.Plugin.StreamLimit -->";

    /// <summary>
    /// The script tag injected before the closing body tag. Relative to index.html
    /// ({BaseUrl}/web/), so it still resolves when the server has a BaseUrl set.
    /// </summary>
    public const string ScriptTag = "<script defer src=\"../StreamLimit/inject.js\"></script>";

    /// <summary>
    /// Removes any previously injected block from the html.
    /// </summary>
    /// <param name="html">The index.html contents.</param>
    /// <returns>The html without the plugin block.</returns>
    public static string Remove(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        while (true)
        {
            var start = html.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return html;
            }

            var end = html.IndexOf(EndMarker, start, StringComparison.Ordinal);
            if (end < 0)
            {
                // Malformed: drop from the begin marker to the end of the string.
                return html.Substring(0, start).TrimEnd();
            }

            end += EndMarker.Length;

            // Also swallow a single trailing newline left behind, to avoid growth.
            if (end < html.Length && html[end] == '\n')
            {
                end++;
            }

            html = html.Substring(0, start) + html.Substring(end);
        }
    }

    /// <summary>
    /// Ensures the plugin's script block is present exactly once, immediately before
    /// the closing body tag. Removing any prior block first makes this idempotent.
    /// </summary>
    /// <param name="html">The index.html contents.</param>
    /// <returns>The html with the plugin block, or the input unchanged when there is no body tag.</returns>
    public static string Add(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        var clean = Remove(html);

        const string closingBody = "</body>";
        var idx = clean.LastIndexOf(closingBody, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return clean;
        }

        var block = BeginMarker + "\n" + ScriptTag + "\n" + EndMarker + "\n";
        return clean.Substring(0, idx) + block + clean.Substring(idx);
    }

    /// <summary>
    /// Whether the html already contains the plugin block.
    /// </summary>
    /// <param name="html">The index.html contents.</param>
    /// <returns>True when the block is present.</returns>
    public static bool IsInjected(string html)
        => !string.IsNullOrEmpty(html) && html.Contains(BeginMarker, StringComparison.Ordinal);
}
