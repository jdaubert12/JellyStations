namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>
/// Paces an MPEG-TS byte stream to wall-clock time using the PCR clock embedded in the packets.
/// The encoder is allowed to run ahead of real time by <see cref="Lead"/>; beyond that the caller is
/// delayed, which back-pressures ffmpeg through the pipe. Independent of ffmpeg's own -re pacing.
/// Timing uses the wall clock so it stays in step with the schedule.
/// </summary>
public sealed class TsPacer
{
    private const int TsPacket = 188;
    private const long PcrHz = 90_000;
    private const long PcrWrap = 1L << 33;

    private DateTime _wallStart;
    private long _firstPcr = -1;
    private long _lastPcr = -1;
    private long _unwrapped;
    private long _marginTicks;
    private long _minMarginTicks = long.MaxValue;

    /// <summary>How far ahead of real time the stream may run.</summary>
    public TimeSpan Lead { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Stream time (from the first PCR) that has been released so far.</summary>
    public TimeSpan Position => _firstPcr < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(_unwrapped / (double)PcrHz);

    /// <summary>Absolute PCR (seconds) of the newest released packet, or null before the first PCR.</summary>
    public double? LastPcrSeconds => _firstPcr < 0 ? null : (_firstPcr + _unwrapped) / (double)PcrHz;

    /// <summary>
    /// How far the encoder is ahead of the wall clock, measured when the newest chunk arrived (before any pacing
    /// delay): about <see cref="Lead"/> when ffmpeg keeps up, shrinking towards zero when it only just manages, and
    /// negative when it cannot produce real time, which is what viewers experience as stutter. Null before the first PCR.
    /// </summary>
    public TimeSpan? Margin => _firstPcr < 0 ? null : TimeSpan.FromTicks(_marginTicks);

    /// <summary>The smallest <see cref="Margin"/> seen since the clock (re)started.</summary>
    public TimeSpan? MinMargin => _firstPcr < 0 || _minMarginTicks == long.MaxValue ? null : TimeSpan.FromTicks(_minMarginTicks);

    /// <summary>
    /// Waits, if needed, until the newest PCR in the chunk is no more than <see cref="Lead"/> ahead of the wall clock.
    /// The chunk must be aligned to TS packet boundaries.
    /// </summary>
    public async ValueTask PaceAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        var pcr = LastPcr(chunk.Span);
        if (pcr < 0)
        {
            return;
        }

        if (_firstPcr < 0)
        {
            _firstPcr = pcr;
            _lastPcr = pcr;
            _unwrapped = 0;
            _wallStart = DateTime.UtcNow;
            _marginTicks = 0;
            _minMarginTicks = long.MaxValue;
            return;
        }

        var delta = pcr - _lastPcr;
        if (delta < -PcrWrap / 2)
        {
            delta += PcrWrap;
        }
        else if (delta > PcrWrap / 2)
        {
            delta -= PcrWrap;
        }

        // Backwards jumps (new encoder segment with a smaller offset) resync rather than stall.
        if (delta < -PcrHz * 2)
        {
            _firstPcr = pcr;
            _lastPcr = pcr;
            _unwrapped = 0;
            _wallStart = DateTime.UtcNow;
            _marginTicks = 0;
            _minMarginTicks = long.MaxValue;
            return;
        }

        _lastPcr = pcr;
        _unwrapped += Math.Max(0, delta);

        // Wall-clock based (same clock the schedule uses). Re-checked after every delay so an inaccurate
        // timer (e.g. a VM whose monotonic clock drifts) cannot make the stream run fast or slow.
        var streamTime = TimeSpan.FromSeconds(_unwrapped / (double)PcrHz);
        _marginTicks = (streamTime - (DateTime.UtcNow - _wallStart)).Ticks;
        if (_marginTicks < _minMarginTicks)
        {
            _minMarginTicks = _marginTicks;
        }
        while (true)
        {
            var ahead = streamTime - (DateTime.UtcNow - _wallStart) - Lead;
            if (ahead <= TimeSpan.FromMilliseconds(5))
            {
                break;
            }

            await Task.Delay(ahead > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : ahead, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Returns the last PCR base (90 kHz) found in the chunk, or -1.</summary>
    public static long LastPcr(ReadOnlySpan<byte> data)
    {
        long result = -1;
        for (var i = 0; i + TsPacket <= data.Length; i += TsPacket)
        {
            var p = data.Slice(i, TsPacket);
            if (p[0] != 0x47)
            {
                continue;
            }

            var afc = (p[3] >> 4) & 0x3;
            if ((afc & 0x2) == 0)
            {
                continue;
            }

            var afLen = p[4];
            if (afLen < 7)
            {
                continue;
            }

            var flags = p[5];
            if ((flags & 0x10) == 0)
            {
                continue;
            }

            var pcrBase = ((long)p[6] << 25) | ((long)p[7] << 17) | ((long)p[8] << 9) | ((long)p[9] << 1) | ((long)(p[10] >> 7));
            result = pcrBase;
        }

        return result;
    }
}

/// <summary>Re-chunks an arbitrary byte stream into TS-packet-aligned blocks.</summary>
public sealed class TsAligner
{
    private const int TsPacket = 188;
    private byte[] _carry = Array.Empty<byte>();

    /// <summary>Returns a packet-aligned block containing any carried bytes plus the input, or empty if less than one packet is available.</summary>
    public ReadOnlyMemory<byte> Push(ReadOnlySpan<byte> data)
    {
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

        // Resync to the first sync byte if the stream is misaligned.
        var start = 0;
        while (start < combined.Length && combined[start] != 0x47)
        {
            start++;
        }

        var usable = combined.Length - start;
        var aligned = usable / TsPacket * TsPacket;
        _carry = aligned == usable ? Array.Empty<byte>() : combined.AsSpan(start + aligned).ToArray();
        return aligned == 0 ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(combined, start, aligned);
    }
}
