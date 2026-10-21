using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>What this ffmpeg build can do with hardware.</summary>
public sealed class FfmpegCapabilities
{
    public string Version { get; init; } = string.Empty;

    /// <summary>Output of -hwaccels: the -hwaccel kinds the build was compiled with.</summary>
    public IReadOnlyList<string> Hwaccels { get; init; } = Array.Empty<string>();

    /// <summary>Every H.264 encoder the build has, software and hardware.</summary>
    public IReadOnlyList<string> H264Encoders { get; init; } = Array.Empty<string>();

    public bool HasZscale { get; init; }

    public bool HasTonemap { get; init; }

    public string? Error { get; init; }
}

/// <summary>Result of one hardware check run.</summary>
public sealed class HardwareTestResult
{
    public string RequestedDecoder { get; init; } = string.Empty;

    public string? Device { get; init; }

    public string VideoEncoder { get; init; } = string.Empty;

    public string File { get; init; } = string.Empty;

    /// <summary>ffmpeg's description of the source video stream (codec, pixel format, size).</summary>
    public string? SourceVideo { get; init; }

    /// <summary>hardware, software, fallback, failed or not-requested.</summary>
    public string Decoder { get; init; } = string.Empty;

    /// <summary>The hwaccel kind ffmpeg said it attached, when it said so.</summary>
    public string? DecoderInUse { get; init; }

    public string? DecoderFallback { get; init; }

    /// <summary>ok or failed.</summary>
    public string Encoder { get; init; } = string.Empty;

    public int ExitCode { get; init; }

    /// <summary>Encode speed relative to real time, from ffmpeg's final statistics (1.0 = exactly real time).</summary>
    public double? Speed { get; init; }

    public double Seconds { get; init; }

    public string Command { get; init; } = string.Empty;

    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    /// <summary>Set when the plugin itself chose software for this file (codec, profile or bit depth the decoder is not given).</summary>
    public string? DecoderSkipReason { get; init; }

    public bool Healthy => Problems.Count == 0;

    public HardwareTestResult WithDecoderSkipped(string requested, string reason) => new()
    {
        RequestedDecoder = requested,
        Device = Device,
        VideoEncoder = VideoEncoder,
        File = File,
        SourceVideo = SourceVideo,
        Decoder = "skipped",
        DecoderSkipReason = reason,
        Encoder = Encoder,
        ExitCode = ExitCode,
        Speed = Speed,
        Seconds = Seconds,
        Command = Command,
        Log = Log,
        Problems = Problems
    };
}

/// <summary>Asks ffmpeg what it can do and runs a short, as-fast-as-possible transcode to see what it actually does.</summary>
public static class HardwareProbe
{
    private const int MaxLogLines = 300;
    private static readonly ConcurrentDictionary<string, FfmpegCapabilities> CapabilityCache = new(StringComparer.Ordinal);
    private static readonly Regex StreamVideo = new(@"Stream #0:\d+(?:\[[^\]]*\])?(?:\([^)]*\))?: Video: (.+)$", RegexOptions.Compiled);
    private static readonly Regex SpeedStat = new(@"speed=\s*([0-9]+(?:\.[0-9]+)?)x", RegexOptions.Compiled);
    private static readonly Regex EncoderLine = new(@"^\s*V[^\s]*\s+([A-Za-z0-9_]+)", RegexOptions.Compiled);

    public static async Task<FfmpegCapabilities> CapabilitiesAsync(string ffmpegPath, bool refresh, CancellationToken ct)
    {
        if (!refresh && CapabilityCache.TryGetValue(ffmpegPath, out var cached))
        {
            return cached;
        }

        FfmpegCapabilities result;
        try
        {
            var version = await RunAsync(ffmpegPath, new[] { "-hide_banner", "-version" }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            var hwaccels = await RunAsync(ffmpegPath, new[] { "-hide_banner", "-hwaccels" }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            var encoders = await RunAsync(ffmpegPath, new[] { "-hide_banner", "-encoders" }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            var filters = await RunAsync(ffmpegPath, new[] { "-hide_banner", "-filters" }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            result = new FfmpegCapabilities
            {
                Version = version.Output.FirstOrDefault(l => l.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))?.Trim() ?? version.Output.FirstOrDefault()?.Trim() ?? string.Empty,
                Hwaccels = ParseHwaccels(hwaccels.Output),
                H264Encoders = ParseH264Encoders(encoders.Output),
                HasZscale = filters.Output.Any(l => Regex.IsMatch(l, @"^\s*\S+\s+zscale\s")),
                HasTonemap = filters.Output.Any(l => Regex.IsMatch(l, @"^\s*\S+\s+tonemap\s"))
            };
        }
        catch (Exception ex)
        {
            result = new FfmpegCapabilities { Error = ex.Message };
        }

        CapabilityCache[ffmpegPath] = result;
        return result;
    }

    public static IReadOnlyList<string> ParseHwaccels(IEnumerable<string> lines)
    {
        var list = new List<string>();
        var started = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!started)
            {
                started = line.StartsWith("Hardware acceleration methods", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line.Length > 0 && !line.Contains(' ', StringComparison.Ordinal))
            {
                list.Add(line);
            }
        }

        return list;
    }

    public static IReadOnlyList<string> ParseH264Encoders(IEnumerable<string> lines)
    {
        var list = new List<string>();
        foreach (var line in lines)
        {
            var m = EncoderLine.Match(line);
            if (m.Success && (m.Groups[1].Value.StartsWith("h264", StringComparison.OrdinalIgnoreCase) || m.Groups[1].Value.Equals("libx264", StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(m.Groups[1].Value);
            }
        }

        return list;
    }

    /// <summary>Transcodes a few seconds of the file the way a program segment would be, and reads ffmpeg's account of it.</summary>
    public static async Task<HardwareTestResult> TestAsync(string ffmpegPath, EncodingProfile profile, string path, string? hdrTransfer, TimeSpan seek, int seconds, CancellationToken ct)
    {
        var args = FfmpegArgsBuilder.ForProbe(path, hdrTransfer, profile, seconds, seek);
        var requested = profile.HardwareDecode ?? string.Empty;
        var sw = Stopwatch.StartNew();
        RunResult run;
        try
        {
            run = await RunAsync(ffmpegPath, args, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new HardwareTestResult
            {
                RequestedDecoder = requested,
                Device = profile.HardwareDevice,
                VideoEncoder = profile.VideoEncoder,
                File = path,
                Decoder = "failed",
                Encoder = "failed",
                ExitCode = -1,
                Seconds = sw.Elapsed.TotalSeconds,
                Command = FfmpegArgsBuilder.ToLogString(args),
                Problems = new[] { "Could not run ffmpeg: " + ex.Message }
            };
        }

        return Interpret(requested, profile, path, args, run.Output, run.ExitCode, sw.Elapsed.TotalSeconds);
    }

    /// <summary>Pure: turns ffmpeg's verbose log and exit code into a verdict.</summary>
    public static HardwareTestResult Interpret(string requested, EncodingProfile profile, string path, IReadOnlyList<string> args, IReadOnlyList<string> log, int exitCode, double elapsedSeconds)
    {
        string? inUse = null;
        string? fallback = null;
        string? source = null;
        double? speed = null;
        foreach (var line in log)
        {
            inUse ??= FfmpegStderr.HardwareDecodeInUse(line);
            if (fallback is null && FfmpegStderr.IsHardwareDecodeFallback(line))
            {
                fallback = line.Trim();
            }

            if (source is null)
            {
                var m = StreamVideo.Match(line);
                if (m.Success)
                {
                    source = m.Groups[1].Value.Trim();
                }
            }

            var sp = SpeedStat.Match(line);
            if (sp.Success && double.TryParse(sp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                speed = v;
            }
        }

        var problems = new List<string>();
        string decoder;
        if (requested.Length == 0)
        {
            decoder = "not-requested";
        }
        else if (exitCode != 0 && inUse is null)
        {
            decoder = "failed";
            problems.Add(requested + " decoding could not be set up (ffmpeg exit " + exitCode + ")" + (fallback is not null ? ": " + fallback : LastInteresting(log)));
        }
        else if (fallback is not null)
        {
            decoder = "fallback";
            problems.Add("ffmpeg fell back to software decoding: " + fallback);
        }
        else if (inUse is not null)
        {
            decoder = "hardware";
        }
        else
        {
            decoder = "software";
            problems.Add("ffmpeg never reported attaching the " + requested + " decoder; the file was decoded in software.");
        }

        var encoder = exitCode == 0 ? "ok" : "failed";
        if (exitCode != 0 && decoder != "failed")
        {
            problems.Add("Encoding with " + profile.VideoEncoder + " failed (ffmpeg exit " + exitCode + ")" + LastInteresting(log));
        }

        if (exitCode == 0 && speed is { } s && s < 1.0)
        {
            problems.Add("The machine ran this at " + s.ToString("0.00", CultureInfo.InvariantCulture) + "x real time: it cannot keep up with this file at this profile.");
        }
        else if (exitCode == 0 && speed is { } s2 && s2 < 1.5)
        {
            problems.Add("Only " + s2.ToString("0.00", CultureInfo.InvariantCulture) + "x real time: this file leaves little headroom for a second station or the logo overlay.");
        }

        return new HardwareTestResult
        {
            RequestedDecoder = requested,
            Device = profile.HardwareDevice,
            VideoEncoder = profile.VideoEncoder,
            File = path,
            SourceVideo = source,
            Decoder = decoder,
            DecoderInUse = inUse,
            DecoderFallback = fallback,
            Encoder = encoder,
            ExitCode = exitCode,
            Speed = speed,
            Seconds = elapsedSeconds,
            Command = FfmpegArgsBuilder.ToLogString(args),
            Log = log.Count > MaxLogLines ? log.Skip(log.Count - MaxLogLines).ToList() : log,
            Problems = problems
        };
    }

    private static string LastInteresting(IReadOnlyList<string> log)
    {
        for (var i = log.Count - 1; i >= 0; i--)
        {
            var line = log[i].Trim();
            if (line.Length > 0 && !line.StartsWith("frame=", StringComparison.Ordinal) && !line.StartsWith("Exiting normally", StringComparison.Ordinal))
            {
                return ": " + line;
            }
        }

        return string.Empty;
    }

    private sealed record RunResult(int ExitCode, IReadOnlyList<string> Output);

    private static async Task<RunResult> RunAsync(string ffmpegPath, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var lines = new ConcurrentQueue<string>();
        using var proc = ProcessRunner.Start(ffmpegPath, args, line =>
        {
            lines.Enqueue(line);
            while (lines.Count > MaxLogLines * 2 && lines.TryDequeue(out _))
            {
            }
        });

        // Capability listings print to stdout, the transcode's account to stderr; read both.
        var stdout = Task.Run(async () =>
        {
            using var reader = new StreamReader(proc.Stdout);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                lines.Enqueue(line);
            }
        }, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            proc.Kill();
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            lines.Enqueue("TvStation: ffmpeg did not finish within " + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s and was stopped.");
        }

        try
        {
            await stdout.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Pipe closed with the process.
        }

        return new RunResult(proc.ExitCode, lines.ToList());
    }
}
