using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.StreamLimit.Gate;

/// <summary>
/// Write-only pass-through around a response body that reports when bytes were actually
/// written, so the gate counts a response by its real activity instead of by the mere fact
/// that its connection is open.
/// </summary>
/// <remarks>
/// The signal is raised after a write completed (a write blocked on a dead connection is not
/// activity) and at most once per <see cref="SignalInterval"/>. The inner stream is never
/// disposed: it belongs to the server.
/// </remarks>
public sealed class ActivityStream : Stream
{
    /// <summary>
    /// Minimum time between two activity signals.
    /// </summary>
    public static readonly TimeSpan SignalInterval = TimeSpan.FromSeconds(1);

    private readonly Stream _inner;
    private readonly Action _onWrite;
    private readonly TimeProvider _clock;
    private long _lastSignalTimestamp;
    private bool _signalled;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivityStream"/> class.
    /// </summary>
    /// <param name="inner">The response body to write to.</param>
    /// <param name="onWrite">Called (throttled) after bytes were written.</param>
    /// <param name="clock">The time source used for throttling.</param>
    public ActivityStream(Stream inner, Action onWrite, TimeProvider? clock = null)
    {
        _inner = inner;
        _onWrite = onWrite;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Signal(count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Signal(buffer.Length);
    }

    /// <inheritdoc />
    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        Signal(1);
    }

    /// <inheritdoc />
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Signal(count);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Signal(buffer.Length);
    }

    /// <inheritdoc />
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        => TaskToAsyncResult.Begin(WriteAsync(buffer, offset, count, CancellationToken.None), callback, state);

    /// <inheritdoc />
    public override void EndWrite(IAsyncResult asyncResult) => TaskToAsyncResult.End(asyncResult);

    private void Signal(int count)
    {
        if (count <= 0)
        {
            return;
        }

        // Writes of one response are sequential; a lost race only costs one extra signal.
        var now = _clock.GetTimestamp();
        if (_signalled && _clock.GetElapsedTime(_lastSignalTimestamp, now) < SignalInterval)
        {
            return;
        }

        _signalled = true;
        _lastSignalTimestamp = now;
        try
        {
            _onWrite();
        }
        catch (Exception)
        {
            // Accounting must never break a response.
        }
    }
}
