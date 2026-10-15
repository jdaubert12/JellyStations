using System.Threading.Channels;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>
/// Fan-out of one MPEG-TS byte stream to any number of readers. Keeps a short pre-roll so a new
/// reader immediately receives recent PAT/PMT tables and a keyframe.
/// </summary>
public sealed class BroadcastBuffer
{
    private const int TsPacket = 188;
    private const int PreRollBytes = 2 * 1024 * 1024;
    private const int ConsumerQueueChunks = 1024;

    private readonly object _lock = new();
    private readonly LinkedList<ReadOnlyMemory<byte>> _preRoll = new();
    private readonly List<Channel<ReadOnlyMemory<byte>>> _consumers = new();
    private byte[] _carry = Array.Empty<byte>();
    private long _preRollSize;
    private bool _completed;

    public long TotalBytes { get; private set; }

    public int ConsumerCount
    {
        get
        {
            lock (_lock)
            {
                return _consumers.Count;
            }
        }
    }

    /// <summary>Appends raw bytes from the encoder; chunks are re-aligned to TS packet boundaries.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        byte[] combined;
        if (_carry.Length == 0)
        {
            combined = data.ToArray();
        }
        else
        {
            combined = new byte[_carry.Length + data.Length];
            _carry.CopyTo(combined, 0);
            data.CopyTo(combined.AsSpan(_carry.Length));
        }

        var aligned = combined.Length / TsPacket * TsPacket;
        _carry = aligned == combined.Length ? Array.Empty<byte>() : combined.AsSpan(aligned).ToArray();
        if (aligned == 0)
        {
            return;
        }

        var chunk = new ReadOnlyMemory<byte>(combined, 0, aligned);
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            TotalBytes += aligned;
            _preRoll.AddLast(chunk);
            _preRollSize += aligned;
            while (_preRollSize > PreRollBytes && _preRoll.First is not null)
            {
                _preRollSize -= _preRoll.First.Value.Length;
                _preRoll.RemoveFirst();
            }

            foreach (var c in _consumers)
            {
                c.Writer.TryWrite(chunk);
            }
        }
    }

    /// <summary>Creates a reader that first receives the pre-roll and then live data.</summary>
    public ConsumerStream Subscribe()
    {
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(ConsumerQueueChunks)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_lock)
        {
            foreach (var chunk in _preRoll)
            {
                channel.Writer.TryWrite(chunk);
            }

            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _consumers.Add(channel);
            }
        }

        return new ConsumerStream(channel.Reader, () => Unsubscribe(channel));
    }

    /// <summary>Drops the pre-roll (used after an encoder restart so stale data is not replayed).</summary>
    public void ClearPreRoll()
    {
        lock (_lock)
        {
            _preRoll.Clear();
            _preRollSize = 0;
            _carry = Array.Empty<byte>();
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            foreach (var c in _consumers)
            {
                c.Writer.TryComplete();
            }

            _consumers.Clear();
        }
    }

    private void Unsubscribe(Channel<ReadOnlyMemory<byte>> channel)
    {
        lock (_lock)
        {
            _consumers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }
}
