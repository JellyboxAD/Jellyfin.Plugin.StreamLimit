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

    [Fact]
    public void Add_OnPageWithLegacyOnDiskBlock_KeepsExactlyOneBlock()
    {
        // index.html still carrying the tag written by <= 1.1.0.1 (read-only folder that
        // could not be cleaned): the on-the-fly injection must not add a second one.
        var legacy = Html.Replace(
            "</body>",
            WebInjection.BeginMarker + "\n" + WebInjection.ScriptTag + "\n" + WebInjection.EndMarker + "\n</body>");

        var served = WebInjection.Add(legacy);

        Assert.Equal(
            served.IndexOf(WebInjection.ScriptTag, System.StringComparison.Ordinal),
            served.LastIndexOf(WebInjection.ScriptTag, System.StringComparison.Ordinal));
    }

    [Fact]
    public void ScriptTag_CarriesPluginVersion()
    {
        var version = typeof(WebInjection).Assembly.GetName().Version!;

        Assert.Contains("src=\"../StreamLimit/inject.js?v=" + version + "\"", WebInjection.ScriptTag, System.StringComparison.Ordinal);
        Assert.Equal("<script defer src=\"../StreamLimit/inject.js\"></script>", WebInjection.BuildScriptTag(null));
    }

    [Fact]
    public void Add_ReplacesBlockOfAnOlderVersion()
    {
        // A page still carrying an older (or unversioned, <= 1.1.0.1) tag gets exactly the
        // current one: the markers, not the tag text, drive idempotence.
        var oldTag = WebInjection.BuildScriptTag(new System.Version(1, 1, 0, 1));
        var stale = Html.Replace(
            "</body>",
            WebInjection.BeginMarker + "\n" + oldTag + "\n" + WebInjection.EndMarker + "\n</body>");

        var served = WebInjection.Add(stale);

        Assert.DoesNotContain(oldTag, served, System.StringComparison.Ordinal);
        Assert.Contains(WebInjection.ScriptTag, served, System.StringComparison.Ordinal);
        Assert.Equal(
            served.IndexOf(WebInjection.BeginMarker, System.StringComparison.Ordinal),
            served.LastIndexOf(WebInjection.BeginMarker, System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/web", "")]
    [InlineData("/web/", "")]
    [InlineData("/web/index.html", "")]
    [InlineData("/WEB/Index.HTML", "")]
    [InlineData("/web/", null)]
    [InlineData("/jellyfin/web/", "/jellyfin")]
    [InlineData("/jellyfin/web", "/jellyfin")]
    [InlineData("/jellyfin/web/index.html", "/jellyfin")]
    [InlineData("/Jellyfin/Web/", "/jellyfin")]
    [InlineData("/jellyfin/web/", "jellyfin")]
    [InlineData("/jellyfin/web/", "/jellyfin/")]
    [InlineData("/media/jf/web/", "/media/jf")]
    public void IsIndexRequest_MatchesIndexPage(string path, string? baseUrl)
    {
        Assert.True(WebInjection.IsIndexRequest(path, baseUrl));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("/web/main.bundle.js", "")]
    [InlineData("/web/index.html.map", "")]
    [InlineData("/web/config.json", "")]
    [InlineData("/webhooks/", "")]
    [InlineData("/other/web/", "")]
    [InlineData("/web/", "/jellyfin")]
    [InlineData("/jellyfin/web/", "")]
    [InlineData("/jellyfin/web/main.js", "/jellyfin")]
    [InlineData("/StreamLimit/inject.js", "")]
    public void IsIndexRequest_IgnoresEverythingElse(string? path, string? baseUrl)
    {
        Assert.False(WebInjection.IsIndexRequest(path, baseUrl));
    }
}
