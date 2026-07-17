using System;
using System.Collections.Generic;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class StreamLimitStoreTests
{
    private static readonly Guid UserA = Guid.Parse("38a5a5bb-90ab-4590-837d-16bcb0b1c1f0");
    private static readonly Guid UserB = Guid.Parse("6c3f7c1e-2d4b-4a8f-9e01-abcdefabcdef");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLimits_EmptyInput_ReturnsEmpty(string? json)
    {
        Assert.Empty(StreamLimitStore.ParseLimits(json));
    }

    [Fact]
    public void ParseLimits_NumberValues_AreParsed()
    {
        var limits = StreamLimitStore.ParseLimits("{\"38a5a5bb90ab4590837d16bcb0b1c1f0\":2}");

        Assert.Equal(2, limits[StreamLimitStore.NormalizeUserKey(UserA)]);
    }

    [Fact]
    public void ParseLimits_LegacyStringNumbers_AreParsed()
    {
        // The old config page stored input values as raw strings.
        var limits = StreamLimitStore.ParseLimits("{\"38a5a5bb90ab4590837d16bcb0b1c1f0\":\"3\"}");

        Assert.Equal(3, limits[StreamLimitStore.NormalizeUserKey(UserA)]);
    }

    [Fact]
    public void ParseLimits_PoisonedEntry_DoesNotKillOtherLimits()
    {
        // Regression: one empty value used to abort the whole dictionary parse,
        // silently disabling every limit (issues #5/#8/#18 upstream).
        var json = "{\"38a5a5bb90ab4590837d16bcb0b1c1f0\":\"\","
                   + "\"6c3f7c1e2d4b4a8f9e01abcdefabcdef\":1,"
                   + "\"junk\":2,"
                   + "\"00000000000000000000000000000000\":null}";

        var limits = StreamLimitStore.ParseLimits(json);

        Assert.Single(limits);
        Assert.Equal(1, limits[StreamLimitStore.NormalizeUserKey(UserB)]);
    }

    [Fact]
    public void ParseLimits_InvalidJson_ReturnsEmpty()
    {
        Assert.Empty(StreamLimitStore.ParseLimits("{not json"));
        Assert.Empty(StreamLimitStore.ParseLimits("[1,2]"));
    }

    [Fact]
    public void ParseLimits_DashedAndUppercaseKeys_AreNormalized()
    {
        var limits = StreamLimitStore.ParseLimits("{\"38A5A5BB-90AB-4590-837D-16BCB0B1C1F0\":4}");

        Assert.Equal(4, limits[StreamLimitStore.NormalizeUserKey(UserA)]);
    }

    [Theory]
    [InlineData("{\"38a5a5bb90ab4590837d16bcb0b1c1f0\":0}")]
    [InlineData("{\"38a5a5bb90ab4590837d16bcb0b1c1f0\":-2}")]
    public void ParseLimits_NonPositiveValues_AreDropped(string json)
    {
        Assert.Empty(StreamLimitStore.ParseLimits(json));
    }

    [Fact]
    public void SerializeLimits_RoundTrips()
    {
        var limits = new Dictionary<string, int>
        {
            [StreamLimitStore.NormalizeUserKey(UserA)] = 2,
            [StreamLimitStore.NormalizeUserKey(UserB)] = 5,
        };

        var roundTripped = StreamLimitStore.ParseLimits(StreamLimitStore.SerializeLimits(limits));

        Assert.Equal(limits, roundTripped);
    }

    [Fact]
    public void GetEffectiveLimit_ExplicitLimitWins()
    {
        var limits = new Dictionary<string, int> { [StreamLimitStore.NormalizeUserKey(UserA)] = 2 };

        Assert.Equal(2, StreamLimitStore.GetEffectiveLimit(limits, UserA, 5));
    }

    [Fact]
    public void GetEffectiveLimit_FallsBackToDefault()
    {
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(5, StreamLimitStore.GetEffectiveLimit(limits, UserA, 5));
    }

    [Fact]
    public void GetEffectiveLimit_NoLimitAnywhere_ReturnsZero()
    {
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(0, StreamLimitStore.GetEffectiveLimit(limits, UserA, 0));
        Assert.Equal(0, StreamLimitStore.GetEffectiveLimit(limits, UserA, -3));
    }

    [Fact]
    public void NormalizeUserKey_IsDashlessLowercase()
    {
        Assert.Equal("38a5a5bb90ab4590837d16bcb0b1c1f0", StreamLimitStore.NormalizeUserKey(UserA));
    }
}
