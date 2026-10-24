namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>What one ffmpeg process was asked to do, plus what its stderr revealed while it ran.</summary>
public sealed class SegmentTelemetry
{
    private string? _decoderFallback;
    private string? _lastWarning;
    private string? _firstError;
    private int _warnings;

    public SegmentTelemetry(string title, bool isSlate, string hardwareDecode, string videoEncoder, bool toneMapping, bool sourceIsHdr, bool overlay)
    {
        StartedUtc = DateTime.UtcNow;
        Title = title;
        IsSlate = isSlate;
        HardwareDecode = hardwareDecode;
        VideoEncoder = videoEncoder;
        ToneMapping = toneMapping;
        SourceIsHdr = sourceIsHdr;
        Overlay = overlay;
    }

    public DateTime StartedUtc { get; }

    public string Title { get; }

    public bool IsSlate { get; }

    /// <summary>The -hwaccel kind this process was started with, or empty for software decoding.</summary>
    public string HardwareDecode { get; }

    public string VideoEncoder { get; }

    public bool ToneMapping { get; }

    public bool SourceIsHdr { get; }

    public bool Overlay { get; }

    /// <summary>Why this program is decoded in software although the profile has a hardware decoder, or null.</summary>
    public string? DecoderSkipReason { get; init; }

    /// <summary>True when <see cref="DecoderSkipReason"/> comes from the hardware decoder having failed on this program.</summary>
    public bool DecoderSkipAfterFailure { get; init; }

    /// <summary>The first stderr line that looks like an error: usually the real reason, where the last line is only the consequence.</summary>
    public string? FirstError => Volatile.Read(ref _firstError);

    /// <summary>The stderr line in which ffmpeg gave up on the hardware decoder and continued in software, if any.</summary>
    public string? DecoderFallback => Volatile.Read(ref _decoderFallback);

    public int Warnings => Volatile.Read(ref _warnings);

    public string? LastWarning => Volatile.Read(ref _lastWarning);

    /// <summary>Feeds one stderr line. ffmpeg runs at warning level, so every line counts as a warning.</summary>
    public void Observe(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        Interlocked.Increment(ref _warnings);
        Volatile.Write(ref _lastWarning, trimmed);
        if (_firstError is null && FfmpegStderr.LooksLikeError(trimmed))
        {
            Interlocked.CompareExchange(ref _firstError, trimmed, null);
        }
        if (HardwareDecode.Length > 0 && _decoderFallback is null && FfmpegStderr.IsHardwareDecodeFallback(trimmed))
        {
            Interlocked.CompareExchange(ref _decoderFallback, trimmed, null);
        }
    }
}

/// <summary>Recognizes the ffmpeg log lines that say how decoding is really being done.</summary>
public static class FfmpegStderr
{
    // Printed at warning level when the hwaccel was set up but cannot take this stream; ffmpeg then decodes in software.
    private static readonly string[] FallbackMarkers =
    {
        "failed setup for format",
        "hwaccel initialisation returned error",
        "error creating a qsv device",
        "error initializing an internal mfx session",
        "error initializing the mfx session",
        "failed to initialise vaapi",
        "failed to create a vaapi device",
        "device creation failed",
        "no device available for decoder",
        "could not open the hardware device",
        "cannot load libcuda",
        "failed to initialise cuda",
        "not supported by this hwaccel",
        "could not dynamically load libva",
        "failed to open the drm device"
    };

    /// <summary>A line that names a cause rather than a consequence ("Nothing was written into output file" is the consequence).</summary>
    public static bool LooksLikeError(string line) =>
        (line.Contains("error", StringComparison.OrdinalIgnoreCase)
         || line.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
         || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
         || line.Contains("not supported", StringComparison.OrdinalIgnoreCase)
         || line.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        && !line.Contains("Nothing was written into output file", StringComparison.Ordinal)
        && !line.StartsWith("Exiting with exit code", StringComparison.Ordinal)
        && !line.StartsWith("Conversion failed", StringComparison.Ordinal);

    /// <summary>True when the line reports that hardware decoding was requested but will not be used.</summary>
    public static bool IsHardwareDecodeFallback(string line)
    {
        foreach (var marker in FallbackMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex StreamMapping = new(@"\(\w+ \(([A-Za-z0-9_]+)\) -> \w+ \([A-Za-z0-9_]+\)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Lines in which ffmpeg names the hardware decoder it uses. With "-hwaccel auto" it says so outright ("Using auto
    /// hwaccel type qsv with new default device"); an explicit hwaccel on a native decoder prints nothing on success.
    /// A full GPU decoder shows in the stream mapping ("(hevc (hevc_qsv) -> h264 (libx264))", at info level and up).
    /// Returns the hwaccel kind, or null.
    /// </summary>
    public static string? HardwareDecodeInUse(string line)
    {
        const string marker = "hwaccel type ";
        var i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var start = i + marker.Length;
            var end = start;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            {
                end++;
            }

            return end > start ? line[start..end].ToLowerInvariant() : null;
        }

        var m = StreamMapping.Match(line);
        return m.Success ? KindOfDecoder(m.Groups[1].Value) : null;
    }

    /// <summary>"hevc_qsv" is the qsv kind, "h264_cuvid" the cuda kind; a native or software decoder is no kind.</summary>
    public static string? KindOfDecoder(string decoder)
    {
        var d = decoder.ToLowerInvariant();
        if (d.EndsWith("_qsv", StringComparison.Ordinal))
        {
            return "qsv";
        }

        if (d.EndsWith("_cuvid", StringComparison.Ordinal))
        {
            return "cuda";
        }

        return null;
    }
}
