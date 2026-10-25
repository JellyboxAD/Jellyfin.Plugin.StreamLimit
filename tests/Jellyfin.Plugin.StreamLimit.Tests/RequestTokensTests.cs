using System.Collections.Generic;
using Jellyfin.Plugin.StreamLimit.Gate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class RequestTokensTests
{
    [Theory]
    [InlineData("MediaBrowser Client=\"Android TV\", Device=\"Shield\", DeviceId=\"abc\", Version=\"0.17\", Token=\"tok123\"", "tok123")]
    [InlineData("Emby UserId=\"u\", Client=\"Old app\", Token=\"tok456\"", "tok456")]
    [InlineData("mediabrowser Token=\"lower\"", "lower")]
    [InlineData("MediaBrowser Token=tok-unquoted, Client=\"x\"", "tok-unquoted")]
    [InlineData("MediaBrowser Client=\"Comma, inside\", Token=\"after-comma\"", "after-comma")]
    [InlineData("MediaBrowser Device=\"My%20TV\", Token=\"enc%2Fod\"", "enc/od")]
    [InlineData("  MediaBrowser   Token=\"padded\"  ", "padded")]
    public void ParseAuthorizationToken_ReadsToken(string header, string expected)
    {
        Assert.Equal(expected, RequestTokens.ParseAuthorizationToken(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer abc.def.ghi")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("MediaBrowser")]
    [InlineData("MediaBrowser Client=\"x\", Device=\"y\"")]
    [InlineData("MediaBrowser Token=\"\"")]
    [InlineData("MediaBrowser NotToken=\"abc\"")]
    public void ParseAuthorizationToken_NoToken_ReturnsNull(string? header)
    {
        Assert.Null(RequestTokens.ParseAuthorizationToken(header));
    }

    [Theory]
    [InlineData("X-Emby-Token", "emby-token")]
    [InlineData("X-MediaBrowser-Token", "mb-token")]
    [InlineData("x-emby-token", "lowercase-header")]
    public void Find_ReadsTokenHeaders(string header, string token)
    {
        var headers = new HeaderDictionary { [header] = token };

        Assert.Equal(token, RequestTokens.Find(headers, QueryCollection.Empty));
    }

    [Fact]
    public void Find_ReadsXEmbyAuthorization()
    {
        var headers = new HeaderDictionary { ["X-Emby-Authorization"] = "Emby Client=\"c\", Token=\"legacy\"" };

        Assert.Equal("legacy", RequestTokens.Find(headers, QueryCollection.Empty));
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("ApiKey")]
    [InlineData("API_KEY")]
    public void Find_ReadsQueryKeys(string name)
    {
        // Case-insensitive like the QueryCollection ASP.NET Core builds for a request.
        var query = new QueryCollection(new Dictionary<string, StringValues>(System.StringComparer.OrdinalIgnoreCase) { [name] = "query-token" });

        Assert.Equal("query-token", RequestTokens.Find(new HeaderDictionary(), query));
    }

    [Fact]
    public void Find_AuthorizationHeaderWinsOverLegacyCarriers()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "MediaBrowser Token=\"primary\"",
            ["X-Emby-Token"] = "secondary",
        };
        var query = new QueryCollection(new Dictionary<string, StringValues> { ["api_key"] = "third" });

        Assert.Equal("primary", RequestTokens.Find(headers, query));
    }

    [Fact]
    public void Find_ForeignAuthorizationScheme_FallsBackToOtherCarriers()
    {
        // A reverse proxy's Basic auth must not hide the Jellyfin token.
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "Basic dXNlcjpwYXNz",
            ["X-Emby-Token"] = "jellyfin-token",
        };

        Assert.Equal("jellyfin-token", RequestTokens.Find(headers, QueryCollection.Empty));
    }

    [Fact]
    public void Find_NothingPresent_ReturnsNull()
    {
        Assert.Null(RequestTokens.Find(new HeaderDictionary(), QueryCollection.Empty));
    }
}
