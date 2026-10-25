using System;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class BlockActivityLoggerTests
{
    private static readonly Guid User = Guid.Parse("38a5a5bb-90ab-4590-837d-16bcb0b1c1f0");

    [Fact]
    public void Describe_IncludesUserDeviceClientAndAddress()
    {
        var (name, shortOverview, overview) = BlockActivityLogger.Describe(new BlockedStream(
            User, "alice", "dev-42", "Living room TV", "Jellyfin Android TV", "192.168.1.20", 2, "HTTP gate"));

        Assert.Equal("alice was blocked from streaming on Living room TV (stream limit)", name);
        Assert.Equal("Limit of 2 simultaneous streams reached", shortOverview);
        Assert.Contains("User: alice", overview, StringComparison.Ordinal);
        Assert.Contains("Device: Living room TV (id dev-42)", overview, StringComparison.Ordinal);
        Assert.Contains("Client: Jellyfin Android TV", overview, StringComparison.Ordinal);
        Assert.Contains("IP address: 192.168.1.20", overview, StringComparison.Ordinal);
        Assert.Contains("Blocked by: HTTP gate", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_MissingDetails_FallBack()
    {
        var (name, shortOverview, overview) = BlockActivityLogger.Describe(new BlockedStream(
            User, null, "dev-42", " ", null, null, 1, "playback start limiter"));

        // Never an empty name: Jellyfin's ActivityLog constructor rejects one.
        Assert.Equal("38a5a5bb90ab4590837d16bcb0b1c1f0 was blocked from streaming on dev-42 (stream limit)", name);
        Assert.Equal("Limit of 1 simultaneous stream reached", shortOverview);
        Assert.Contains("Client: unknown", overview, StringComparison.Ordinal);
        Assert.Contains("IP address: unknown", overview, StringComparison.Ordinal);
    }
}
