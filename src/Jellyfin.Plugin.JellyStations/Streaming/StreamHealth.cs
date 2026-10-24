using System.Globalization;

namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>A point-in-time picture of a station's encoder, taken for the dashboard.</summary>
public sealed class EncoderSnapshot
{
    public bool ProcessRunning { get; init; }

    public string? Title { get; init; }

    public bool IsSlate { get; init; }

    public DateTime? SegmentStartedUtc { get; init; }

    /// <summary>The hardware decoder the station's profile asks for (after "auto" was resolved), or empty.</summary>
    public string RequestedDecoder { get; init; } = string.Empty;

    /// <summary>The hardware decoder the current process was started with (empty after a session-level fallback).</summary>
    public string ActiveDecoder { get; init; } = string.Empty;

    /// <summary>True when the device failed outright earlier in this session and decoding was switched to software.</summary>
    public bool DecoderDisabled { get; init; }

    /// <summary>ffmpeg's own line saying it fell back to software for this file, if it printed one.</summary>
    public string? DecoderFallback { get; init; }

    /// <summary>Why the plugin chose software for this program although the profile has a hardware decoder.</summary>
    public string? DecoderSkipReason { get; init; }

    public bool DecoderSkipAfterFailure { get; init; }

    /// <summary>GPU device node the process holds open, when the host can tell.</summary>
    public string? GpuDevice { get; init; }

    public bool CanInspectDevices { get; init; }

    public string VideoEncoder { get; init; } = string.Empty;

    public bool ToneMapping { get; init; }

    public bool SourceIsHdr { get; init; }

    public bool Overlay { get; init; }

    /// <summary>CPU cores the ffmpeg process has been using since the previous sample (1.0 = one core flat out).</summary>
    public double? CpuCores { get; init; }

    /// <summary>How far the encoder is ahead of real time; negative means viewers are stalling.</summary>
    public double? AheadSeconds { get; init; }

    public double? MinAheadSeconds { get; init; }

    /// <summary>Seconds since ffmpeg last produced output, while a process is running.</summary>
    public double? SecondsSinceOutput { get; init; }

    public int Warnings { get; init; }

    public string? LastWarning { get; init; }

    public static bool IsHardwareEncoder(string encoder) =>
        encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)
        || encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase)
        || encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)
        || encoder.EndsWith("_amf", StringComparison.OrdinalIgnoreCase)
        || encoder.EndsWith("_videotoolbox", StringComparison.OrdinalIgnoreCase)
        || encoder.EndsWith("_v4l2m2m", StringComparison.OrdinalIgnoreCase)
        || encoder.EndsWith("_mf", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The dashboard's reading of an <see cref="EncoderSnapshot"/>: a decoder verdict, a traffic light and the reasons.</summary>
public static class StreamHealth
{
    /// <summary>Below this much lead the encoder is only just keeping real time.</summary>
    public const double TightAheadSeconds = 0.4;

    /// <summary>A program older than this has had time to build its lead; before that a small margin is normal start-up.</summary>
    public const double WarmUpSeconds = 8;

    /// <summary>No output for this long from a live process is a stall.</summary>
    public const double StallSeconds = 6;

    /// <summary>More CPU than this for one station points at software decoding of a heavy source.</summary>
    public const double HeavyCpuCores = 3.0;

    public const string DecoderSoftware = "software";
    public const string DecoderHardware = "hardware";
    public const string DecoderUnverified = "unverified";
    public const string DecoderFallback = "fallback";
    public const string DecoderDisabled = "disabled";
    public const string DecoderSkipped = "skipped";

    public sealed record Verdict(string Decoder, string State, IReadOnlyList<string> Problems);

    public static Verdict Assess(EncoderSnapshot s, DateTime nowUtc)
    {
        var problems = new List<string>();
        var level = 0; // 0 ok, 1 warn, 2 bad

        string decoder;
        if (s.IsSlate)
        {
            decoder = DecoderSoftware;
        }
        else if (s.RequestedDecoder.Length == 0)
        {
            decoder = DecoderSoftware;
        }
        else if (s.DecoderDisabled)
        {
            decoder = DecoderDisabled;
            level = Math.Max(level, 2);
            problems.Add(s.RequestedDecoder + " decoding failed when the stream started; this session decodes in software. Check the device and driver, then restart the encoder.");
        }
        else if (s.ActiveDecoder.Length == 0)
        {
            decoder = DecoderSkipped;
            if (s.DecoderSkipAfterFailure)
            {
                level = Math.Max(level, 1);
                problems.Add(s.RequestedDecoder + " could not decode this program; it plays in software: " + (s.DecoderSkipReason ?? "see the log"));
            }
        }
        else if (s.DecoderFallback is not null)
        {
            decoder = DecoderFallback;
            level = Math.Max(level, 2);
            problems.Add("ffmpeg could not use " + s.ActiveDecoder + " for this file and is decoding in software: " + s.DecoderFallback);
        }
        else if (s.GpuDevice is not null)
        {
            decoder = DecoderHardware;
        }
        else if (!s.CanInspectDevices || !s.ProcessRunning)
        {
            decoder = DecoderUnverified;
        }
        else
        {
            // Linux, process alive, hwaccel requested, no fallback line, yet no GPU node open: the decoder is not in use.
            decoder = DecoderUnverified;
            level = Math.Max(level, 1);
            problems.Add("ffmpeg was started with " + s.ActiveDecoder + " decoding but holds no GPU device open; the decoder is probably not in use.");
        }

        if (s.ProcessRunning && s.SecondsSinceOutput is { } quiet && quiet > StallSeconds)
        {
            level = Math.Max(level, 2);
            problems.Add("No output from ffmpeg for " + quiet.ToString("0", CultureInfo.InvariantCulture) + " s.");
        }

        var age = s.SegmentStartedUtc.HasValue ? (nowUtc - s.SegmentStartedUtc.Value).TotalSeconds : 0;
        if (s.ProcessRunning && s.AheadSeconds is { } ahead && age > WarmUpSeconds)
        {
            if (ahead < 0)
            {
                level = Math.Max(level, 2);
                problems.Add("The encoder is " + (-ahead).ToString("0.0", CultureInfo.InvariantCulture) + " s behind real time; viewers see stutter.");
            }
            else if (ahead < TightAheadSeconds)
            {
                level = Math.Max(level, 1);
                problems.Add("The encoder is only " + ahead.ToString("0.0", CultureInfo.InvariantCulture) + " s ahead of real time; it barely keeps up.");
            }
        }

        if (s.MinAheadSeconds is { } min && min < 0 && (s.AheadSeconds ?? 0) >= 0 && age > WarmUpSeconds)
        {
            level = Math.Max(level, 1);
            problems.Add("The encoder fell " + (-min).ToString("0.0", CultureInfo.InvariantCulture) + " s behind real time earlier in this program.");
        }

        if (s.CpuCores is { } cores && cores > HeavyCpuCores && !s.IsSlate)
        {
            level = Math.Max(level, 1);
            problems.Add("ffmpeg is using " + cores.ToString("0.0", CultureInfo.InvariantCulture) + " CPU cores for one station" + (decoder == DecoderHardware ? "." : "; a heavy source is probably being decoded in software."));
        }

        return new Verdict(decoder, level switch { 0 => "ok", 1 => "warn", _ => "bad" }, problems);
    }
}
