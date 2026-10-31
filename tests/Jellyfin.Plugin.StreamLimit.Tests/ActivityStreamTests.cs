using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamLimit.Gate;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Jellyfin.Plugin.StreamLimit.Tests;

public class ActivityStreamTests
{
    private readonly TestClock _clock = new();

    [Fact]
    public async Task Writes_PassThrough_AndSignalAtMostOncePerInterval()
    {
        var inner = new MemoryStream();
        var signals = 0;
        var stream = new ActivityStream(inner, () => signals++, _clock);

        await stream.WriteAsync(new byte[] { 1, 2, 3 });
        await stream.WriteAsync(new byte[] { 4 }.AsMemory());
        stream.Write(new byte[] { 5 }, 0, 1);
        Assert.Equal(1, signals);

        _clock.Advance(ActivityStream.SignalInterval);
        await stream.WriteAsync(new byte[] { 6 }, 0, 1);

        Assert.Equal(2, signals);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, inner.ToArray());
    }

    [Fact]
    public async Task EmptyWrites_AreNoActivity()
    {
        var signals = 0;
        var stream = new ActivityStream(new MemoryStream(), () => signals++, _clock);

        await stream.WriteAsync(ReadOnlyMemory<byte>.Empty);
        await stream.FlushAsync();

        Assert.Equal(0, signals);
    }

    [Fact]
    public async Task FailedWrite_IsNoActivity_AndThrowingCallbackNeverBreaksTheResponse()
    {
        var signals = 0;
        var failing = new ActivityStream(new ThrowingStream(), () => signals++, _clock);
        await Assert.ThrowsAsync<IOException>(() => failing.WriteAsync(new byte[] { 1 }).AsTask());
        Assert.Equal(0, signals);

        var inner = new MemoryStream();
        var stream = new ActivityStream(inner, () => throw new InvalidOperationException(), _clock);
        await stream.WriteAsync(new byte[] { 1 });
        Assert.Equal(1, inner.Length);
    }

    [Fact]
    public void Dispose_LeavesTheServerStreamOpen()
    {
        var inner = new MemoryStream();
        new ActivityStream(inner, () => { }, _clock).Dispose();

        Assert.True(inner.CanWrite);
    }

    [Fact]
    public async Task ThroughStreamResponseBodyFeature_SendFileAndPipeWriterReachTheBody()
    {
        // What the gate installs: file results (SendFileAsync) and PipeWriter-based writers
        // must still reach the real body, and count as activity.
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 10, 20, 30, 40, 50 });
            var inner = new MemoryStream();
            var signals = 0;
            var feature = new StreamResponseBodyFeature(new ActivityStream(inner, () => signals++, _clock));
            IHttpResponseBodyFeature body = feature;

            await body.StartAsync();
            await body.SendFileAsync(path, 1, 3, default);
            _clock.Advance(ActivityStream.SignalInterval);
            await body.Writer.WriteAsync(new byte[] { 99 });
            await body.CompleteAsync();

            Assert.Equal(new byte[] { 20, 30, 40, 99 }, inner.ToArray());
            Assert.Equal(2, signals);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
            => throw new IOException("connection reset");
    }
}
