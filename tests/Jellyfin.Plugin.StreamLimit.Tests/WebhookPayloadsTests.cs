using System;
using System.Text.Json;
using Jellyfin.Plugin.StreamLimit.Notifications;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class WebhookPayloadsTests
{
    private static readonly WebhookEvent Blocked = new(
        WebhookPayloads.StreamBlocked,
        "Stream blocked",
        "alice was blocked from streaming on TV (limit 2, 2 already playing).",
        "alice",
        "38a5a5bb90ab4590837d16bcb0b1c1f0",
        "TV",
        "dev-1",
        "Jellyfin Web",
        "192.168.1.20",
        2,
        2,
        "MyServer",
        new DateTimeOffset(2026, 10, 25, 12, 30, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://discord.com/api/webhooks/1/abc")]
    [InlineData("http://apprise:8000/notify/apprise")]
    [InlineData(" https://example.org/hook ")]
    public void IsValidUrl_AcceptsEmptyAndHttp(string? url)
    {
        Assert.True(WebhookPayloads.IsValidUrl(url));
    }

    [Theory]
    [InlineData("ftp://example.org/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.org/hook")]
    [InlineData("/relative/path")]
    [InlineData("http://")]
    public void IsValidUrl_RejectsEverythingElse(string url)
    {
        Assert.False(WebhookPayloads.IsValidUrl(url));
        Assert.False(WebhookPayloads.TryParse(url, out _));
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/abc", true)]
    [InlineData("https://discordapp.com/api/webhooks/1/abc", true)]
    [InlineData("https://canary.discord.com/api/webhooks/1/abc", true)]
    [InlineData("https://DISCORD.com/API/Webhooks/1/abc", true)]
    [InlineData("https://discord.com/channels/1", false)]
    [InlineData("https://notdiscord.com/api/webhooks/1", false)]
    [InlineData("https://discord.com.evil.example/api/webhooks/1", false)]
    [InlineData("http://apprise:8000/notify", false)]
    public void IsDiscord_MatchesDiscordWebhooksOnly(string url, bool expected)
    {
        Assert.Equal(expected, WebhookPayloads.IsDiscord(new Uri(url)));
    }

    [Fact]
    public void Redact_HidesPathAndQuery()
    {
        var redacted = WebhookPayloads.Redact(new Uri("https://discord.com/api/webhooks/123/SECRET-TOKEN?wait=true"));

        Assert.Equal("https://discord.com/…", redacted);
        Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGeneric_HasAppriseFieldsAndEventDetails()
    {
        using var doc = JsonDocument.Parse(WebhookPayloads.BuildGeneric(Blocked));
        var root = doc.RootElement;

        Assert.Equal("Stream blocked", root.GetProperty("title").GetString());
        Assert.Equal(Blocked.Body, root.GetProperty("body").GetString());
        Assert.Equal("info", root.GetProperty("type").GetString());
        Assert.Equal("stream_blocked", root.GetProperty("event").GetString());
        Assert.Equal("alice", root.GetProperty("user").GetString());
        Assert.Equal("38a5a5bb90ab4590837d16bcb0b1c1f0", root.GetProperty("userId").GetString());
        Assert.Equal("TV", root.GetProperty("device").GetString());
        Assert.Equal("dev-1", root.GetProperty("deviceId").GetString());
        Assert.Equal("Jellyfin Web", root.GetProperty("client").GetString());
        Assert.Equal("192.168.1.20", root.GetProperty("ip").GetString());
        Assert.Equal(2, root.GetProperty("active").GetInt32());
        Assert.Equal(2, root.GetProperty("limit").GetInt32());
        Assert.Equal("MyServer", root.GetProperty("server").GetString());
        Assert.Equal("2026-10-25T12:30:00.000Z", root.GetProperty("timestamp").GetString());
    }

    [Fact]
    public void BuildDiscord_IsOneEmbedWithFields()
    {
        using var doc = JsonDocument.Parse(WebhookPayloads.BuildDiscord(Blocked));
        var root = doc.RootElement;

        Assert.Equal("Stream Limiter", root.GetProperty("username").GetString());
        var embed = Assert.Single(root.GetProperty("embeds").EnumerateArray());
        Assert.Equal("Stream blocked", embed.GetProperty("title").GetString());
        Assert.Equal(Blocked.Body, embed.GetProperty("description").GetString());
        Assert.Equal(0xE53935, embed.GetProperty("color").GetInt32());
        Assert.Equal("2026-10-25T12:30:00.000Z", embed.GetProperty("timestamp").GetString());

        var fields = embed.GetProperty("fields");
        Assert.Equal(5, fields.GetArrayLength());
        Assert.Equal("User", fields[0].GetProperty("name").GetString());
        Assert.Equal("alice", fields[0].GetProperty("value").GetString());
        Assert.Equal("Active/Limit", fields[4].GetProperty("name").GetString());
        Assert.Equal("2 / 2", fields[4].GetProperty("value").GetString());
    }

    [Fact]
    public void BuildDiscord_EmptyValues_BecomeDash()
    {
        // Discord rejects an embed field with an empty value.
        var test = Blocked with { User = null, UserId = null, Client = "", Ip = null, Active = null, Limit = null, Event = WebhookPayloads.Test };

        using var doc = JsonDocument.Parse(WebhookPayloads.BuildDiscord(test));
        var embed = doc.RootElement.GetProperty("embeds")[0];

        foreach (var field in embed.GetProperty("fields").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("value").GetString()));
        }

        Assert.Equal(0x00A4DC, embed.GetProperty("color").GetInt32());
    }

    [Fact]
    public void Build_PicksTheFormatFromTheUrl()
    {
        Assert.Contains("\"embeds\"", WebhookPayloads.Build(new Uri("https://discord.com/api/webhooks/1/x"), Blocked), StringComparison.Ordinal);
        Assert.Contains("\"type\":\"info\"", WebhookPayloads.Build(new Uri("https://apprise.local/notify"), Blocked), StringComparison.Ordinal);
    }
}
