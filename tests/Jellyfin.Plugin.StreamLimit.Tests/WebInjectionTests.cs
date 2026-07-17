using Jellyfin.Plugin.StreamLimit.Web;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class WebInjectionTests
{
    private const string Html = "<html><head></head><body><div id=\"app\"></div></body></html>";

    [Fact]
    public void Add_InsertsBlockBeforeBody()
    {
        var result = WebInjection.Add(Html);

        Assert.True(WebInjection.IsInjected(result));
        Assert.Contains(WebInjection.ScriptTag, result, System.StringComparison.Ordinal);
        Assert.True(result.IndexOf(WebInjection.ScriptTag, System.StringComparison.Ordinal)
                    < result.IndexOf("</body>", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Add_IsIdempotent()
    {
        var once = WebInjection.Add(Html);
        var twice = WebInjection.Add(once);

        Assert.Equal(once, twice);
        // Exactly one block.
        Assert.Equal(once.IndexOf(WebInjection.BeginMarker, System.StringComparison.Ordinal),
                     once.LastIndexOf(WebInjection.BeginMarker, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Remove_RestoresOriginal()
    {
        var injected = WebInjection.Add(Html);
        var removed = WebInjection.Remove(injected);

        Assert.False(WebInjection.IsInjected(removed));
        Assert.DoesNotContain(WebInjection.ScriptTag, removed, System.StringComparison.Ordinal);
        Assert.Contains("<div id=\"app\"></div>", removed, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_StripsDuplicateBlocksFromLegacyInjections()
    {
        var doubled = Html.Replace(
            "</body>",
            WebInjection.BeginMarker + "\n" + WebInjection.ScriptTag + "\n" + WebInjection.EndMarker + "\n"
            + WebInjection.BeginMarker + "\n" + WebInjection.ScriptTag + "\n" + WebInjection.EndMarker + "\n</body>");

        var removed = WebInjection.Remove(doubled);

        Assert.False(WebInjection.IsInjected(removed));
    }

    [Fact]
    public void Add_NoBodyTag_ReturnsUnchanged()
    {
        const string noBody = "<html><head></head></html>";
        Assert.Equal(noBody, WebInjection.Add(noBody));
    }

    [Fact]
    public void Add_ThenReAdd_DoesNotAccumulate()
    {
        // Simulates repeated restarts: the injected file is fed back in each time.
        var content = Html;
        for (var i = 0; i < 5; i++)
        {
            content = WebInjection.Add(content);
        }

        var first = content.IndexOf(WebInjection.BeginMarker, System.StringComparison.Ordinal);
        var last = content.LastIndexOf(WebInjection.BeginMarker, System.StringComparison.Ordinal);
        Assert.Equal(first, last);
    }
}
