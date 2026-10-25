using System;
using Jellyfin.Plugin.StreamLimit.Gate;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class MessageTemplateTests
{
    private static readonly MessageValues Values = new("alice", 2, 2, "Living room TV, Phone");

    [Fact]
    public void Render_ReplacesAllPlaceholders()
    {
        var text = MessageTemplate.Render("{{user}}, you already watch on {{devices}} ({{active}}/{{limit}}).", Values);

        Assert.Equal("alice, you already watch on Living room TV, Phone (2/2).", text);
    }

    [Theory]
    [InlineData("{{USER}}", "alice")]
    [InlineData("{{ User }}", "alice")]
    [InlineData("{{Limit}}/{{ACTIVE}}", "2/2")]
    [InlineData("{{user}}{{user}}", "alicealice")]
    public void Render_IsCaseAndSpaceInsensitive(string template, string expected)
    {
        Assert.Equal(expected, MessageTemplate.Render(template, Values));
    }

    [Theory]
    [InlineData("{{server}} {{user}}", "{{server}} alice")]
    [InlineData("{user} {{{user}}}", "{user} {alice}")]
    [InlineData("{{ }}", "{{ }}")]
    [InlineData("no placeholders", "no placeholders")]
    public void Render_LeavesUnknownPlaceholdersAsIs(string template, string expected)
    {
        Assert.Equal(expected, MessageTemplate.Render(template, Values));
    }

    [Fact]
    public void Render_EmptyValues_RenderBlank()
    {
        Assert.Equal("Limit  reached by ", MessageTemplate.Render("Limit {{limit}} reached by {{user}}", MessageValues.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Render_NoTemplate_ReturnsEmpty(string? template)
    {
        Assert.Equal(string.Empty, MessageTemplate.Render(template, Values));
    }

    [Fact]
    public void Render_ValuesAreInsertedVerbatim()
    {
        // Plain text in, plain text out: consumers render with textContent, so markup in a
        // user or device name must reach them untouched (never interpreted, never doubled).
        var values = new MessageValues("<img src=x onerror=alert(1)>", 1, 1, "\"Tv\" & <b>");

        Assert.Equal("<img src=x onerror=alert(1)> on \"Tv\" & <b>", MessageTemplate.Render("{{user}} on {{devices}}", values));
    }

    [Fact]
    public void DescribeDevices_UsesSessionNames_FallsBackToId_DistinctSorted()
    {
        var tv = Session("tv-1", "Living room TV");
        var tvSecondSession = Session("TV-1", "Living room TV");
        var nameless = Session("phone-1", string.Empty);

        var text = MessageTemplate.DescribeDevices(["tv-1", "phone-1", "ext-player", "TV-1", ""], [tv, tvSecondSession, nameless]);

        Assert.Equal("ext-player, Living room TV, phone-1", text);
    }

    [Fact]
    public void DescribeDevices_None_IsEmpty()
    {
        Assert.Equal(string.Empty, MessageTemplate.DescribeDevices([], []));
    }

    [Fact]
    public void BlockedStream_ExposesMessageValues()
    {
        var blocked = new BlockedStream(Guid.NewGuid(), "bob", "dev", "Tv", "Web", "1.2.3.4", 3, "HTTP gate", 3, "Phone");

        Assert.Equal(new MessageValues("bob", 3, 3, "Phone"), blocked.MessageValues);
    }

    private static SessionInfo Session(string deviceId, string deviceName)
        => new(null!, NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = Guid.Empty,
            DeviceId = deviceId,
            DeviceName = deviceName,
        };
}

public class GateHeaderTests
{
    [Theory]
    [InlineData(null, "X-StreamLimit, X-StreamLimit-Title, X-StreamLimit-Message")]
    [InlineData("", "X-StreamLimit, X-StreamLimit-Title, X-StreamLimit-Message")]
    [InlineData("X-Custom", "X-Custom, X-StreamLimit, X-StreamLimit-Title, X-StreamLimit-Message")]
    [InlineData("x-streamlimit-title, X-Other", "x-streamlimit-title, X-Other, X-StreamLimit, X-StreamLimit-Message")]
    public void MergeExposedHeaders_AddsOurHeadersOnce(string? existing, string expected)
    {
        Assert.Equal(expected, StreamGateFilter.MergeExposedHeaders(existing));
    }

    [Fact]
    public void HeaderEncoding_RoundTripsUtf8()
    {
        // What the gate sends and what inject.js decodes with decodeURIComponent.
        const string Text = "Déjà 2/2 — «TV» & 📺";
        var encoded = Uri.EscapeDataString(Text);

        Assert.Matches("^[\\x21-\\x7E]*$", encoded);
        Assert.Equal(Text, Uri.UnescapeDataString(encoded));
    }
}
