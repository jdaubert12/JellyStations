using System.Threading.Channels;

namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>A read-only, non-seekable Stream over one consumer's channel of TS chunks.</summary>
public sealed class ConsumerStream : Stream
{
    private readonly ChannelReader<ReadOnlyMemory<byte>> _reader;
    private readonly Action _onDispose;
    private ReadOnlyMemory<byte> _current;
    private int _pos;
    private bool _disposed;

    internal ConsumerStream(ChannelReader<ReadOnlyMemory<byte>> reader, Action onDispose)
    {
        _reader = reader;
        _onDispose = onDispose;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (!EnsureCurrentBlocking())
        {
            return 0;
        }

        return CopyOut(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!await EnsureCurrentAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        return CopyOut(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _onDispose();
        }

        base.Dispose(disposing);
    }

    private int CopyOut(Span<byte> buffer)
    {
        var available = _current.Length - _pos;
        var n = Math.Min(available, buffer.Length);
        _current.Span.Slice(_pos, n).CopyTo(buffer);
        _pos += n;
        if (_pos >= _current.Length)
        {
            _current = default;
            _pos = 0;
        }

        return n;
    }

    private bool EnsureCurrentBlocking()
    {
        while (_pos >= _current.Length)
        {
            if (_reader.TryRead(out var next))
            {
                _current = next;
                _pos = 0;
                continue;
            }

            if (!_reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<bool> EnsureCurrentAsync(CancellationToken ct)
    {
        while (_pos >= _current.Length)
        {
            if (_reader.TryRead(out var next))
            {
                _current = next;
                _pos = 0;
                continue;
            }

            if (!await _reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }
}
