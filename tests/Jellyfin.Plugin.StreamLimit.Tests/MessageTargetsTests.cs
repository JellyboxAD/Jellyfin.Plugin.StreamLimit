using System;
using System.Linq;
using Jellyfin.Plugin.StreamLimit.Notifications;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class MessageTargetsTests
{
    private static readonly Guid User = Guid.Parse("60215b81-93b8-4049-856a-5668308c7ca7");

    [Theory]
    [InlineData("Jellyfin Web", true)]
    [InlineData("Jellyfin for Android", true)]
    [InlineData("Jellyfin Android", true)]
    [InlineData("Jellyfin iOS", true)]
    [InlineData("Jellyfin iPadOS", true)]
    [InlineData("Jellyfin (iOS)", true)]
    [InlineData("Jellyfin Mobile", true)]
    [InlineData("Jellyfin for Android TV", false)]
    [InlineData("Jellyfin for Android TV (debug)", false)]
    [InlineData("Android TV", false)]
    [InlineData("Swiftfin iOS", false)]
    [InlineData("Swiftfin tvOS", false)]
    [InlineData("Streamyfin", false)]
    [InlineData("Findroid", false)]
    [InlineData("AFinity", false)]
    [InlineData("Jellyfin Media Player", false)]
    [InlineData("Kodi", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWebBased_MatchesTheWebClientsOnly(string? client, bool expected)
    {
        Assert.Equal(expected, MessageTargets.IsWebBased(client));
    }

    [Theory]
    [InlineData("fc79c3743402303260215b81-93b8-4049-856a-5668308c7ca7", "fc79c37434023032")]
    [InlineData("fc79c37434023032" + "60215B81-93B8-4049-856A-5668308C7CA7", "fc79c37434023032")]
    [InlineData("fc79c37434023032" + "60215b8193b84049856a5668308c7ca7", "fc79c37434023032")]
    [InlineData("60215b81-93b8-4049-856a-5668308c7ca7", null)]
    [InlineData("fc79c37434023032", null)]
    [InlineData("abc6c3f7c1e-2d4b-4a8f-9e01-abcdefabcdef", null)]
    [InlineData(null, null)]
    public void CompanionDeviceId_StripsTheUserIdSuffix(string? deviceId, string? expected)
    {
        Assert.Equal(expected, MessageTargets.CompanionDeviceId(deviceId, User));
    }

    [Fact]
    public void Select_NativeAppsByDefault_WithATimeout()
    {
        var tv = Session("tv", "Jellyfin for Android TV");
        var web = Session("browser", "Jellyfin Web");

        var targets = MessageTargets.Select([tv, web], User, "tv", false, true);

        var target = Assert.Single(targets);
        Assert.Same(tv, target.Session);
        Assert.Equal(MessageTargets.NativeTimeoutMs, target.TimeoutMs);
        Assert.Equal("native", target.Reason);
    }

    [Fact]
    public void Select_WebClients_OnlyWithShowLimitPopup_AsADialog()
    {
        var web = Session("browser", "Jellyfin Web");

        Assert.Empty(MessageTargets.Select([web], User, "browser", false, true));
        var target = Assert.Single(MessageTargets.Select([web], User, "BROWSER", true, false));
        Assert.Null(target.TimeoutMs);
        Assert.Equal("web", target.Reason);
    }

    [Fact]
    public void Select_AndroidNativePlayer_RoutesToTheAppsWebUi()
    {
        // jellyfin-android: the native player reports baseId + userId (no websocket), the web
        // UI (which can show a message) baseId. Both report the web-based client name.
        var player = Session("fc79c3743402303260215b81-93b8-4049-856a-5668308c7ca7", "Jellyfin for Android");
        var webUi = Session("fc79c37434023032", "Jellyfin for Android");
        var other = Session("other", "Jellyfin Web");

        var targets = MessageTargets.Select([player, webUi, other], User, player.DeviceId, false, true);

        var target = Assert.Single(targets);
        Assert.Same(webUi, target.Session);
        Assert.Equal("companion", target.Reason);
        Assert.Null(target.TimeoutMs);
    }

    [Fact]
    public void Select_NothingWhenBothOptionsAreOff_OrForOtherUsers()
    {
        var tv = Session("tv", "Jellyfin for Android TV");
        var stranger = Session("tv", "Jellyfin for Android TV", Guid.NewGuid());

        Assert.Empty(MessageTargets.Select([tv], User, "tv", false, false));
        Assert.Same(tv, MessageTargets.Select([tv, stranger], User, "tv", false, true).Single().Session);
    }

    private static SessionInfo Session(string deviceId, string client, Guid? userId = null)
        => new(null!, NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId ?? User,
            DeviceId = deviceId,
            Client = client,
        };
}
