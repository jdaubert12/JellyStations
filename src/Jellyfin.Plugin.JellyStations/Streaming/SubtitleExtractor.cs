using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>
/// Pulls an embedded text subtitle track out of a video file into an .ass file, once per file and track, so the
/// segment's ffmpeg can burn it in. ffmpeg's subtitles filter would otherwise read the whole video file to find
/// the track every time the program starts, which on a large file over a slow disk stalls the channel for seconds.
/// </summary>
public sealed class SubtitleExtractor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private readonly string _ffmpegPath;
    private readonly string _cacheDir;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, string> _failed = new();

    public SubtitleExtractor(string ffmpegPath, string cacheDir, ILogger logger)
    {
        _ffmpegPath = ffmpegPath;
        _cacheDir = cacheDir;
        _logger = logger;
    }

    /// <summary>Where the track of this file would be cached; the name changes when the file does.</summary>
    /// <summary>The ffmpeg command that converts one embedded text track to ASS. The temp file has no ".ass"
    /// extension, so the muxer is named explicitly: without "-f ass" ffmpeg refuses to open the output.</summary>
    public static List<string> Arguments(string videoPath, int streamIndex, string output) => new()
    {
        "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-y",
        "-i", videoPath,
        "-map", "0:s:" + streamIndex.ToString(CultureInfo.InvariantCulture),
        "-c:s", "ass", "-f", "ass",
        output
    };

    public string CachePath(string videoPath, int streamIndex)
    {
        string stamp;
        try
        {
            var info = new FileInfo(videoPath);
            stamp = info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "missing";
        }
        catch (Exception)
        {
            stamp = "unknown";
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoPath + "|" + stamp + "|" + streamIndex.ToString(CultureInfo.InvariantCulture))))[..24].ToLowerInvariant();
        return Path.Combine(_cacheDir, key + ".ass");
    }

    /// <summary>
    /// The .ass file holding the track, extracting it first when it is not cached yet. Null when ffmpeg could not
    /// extract it; that is remembered so a broken track does not delay every program start.
    /// </summary>
    public async Task<string?> EnsureExtractedAsync(string videoPath, int streamIndex, CancellationToken ct)
    {
        var target = CachePath(videoPath, streamIndex);
        if (_failed.ContainsKey(target))
        {
            return null;
        }

        if (IsUsable(target))
        {
            return target;
        }

        var gate = _locks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsUsable(target))
            {
                return target;
            }

            if (_failed.ContainsKey(target))
            {
                return null;
            }

            Directory.CreateDirectory(_cacheDir);
            var temp = target + ".tmp";
            var args = Arguments(videoPath, streamIndex, temp);
            string? lastError = null;
            var started = DateTime.UtcNow;
            using var proc = ProcessRunner.Start(_ffmpegPath, args, line => lastError = line.Trim());
            if (!await proc.WaitForExitAsync(Timeout, ct).ConfigureAwait(false))
            {
                lastError = "took longer than " + Timeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " minutes";
            }

            if (proc.ExitCode == 0 && IsUsable(temp))
            {
                File.Move(temp, target, overwrite: true);
                _logger.LogInformation("JellyStations: extracted subtitle track {Index} of {File} in {Seconds:0.0}s", streamIndex, Path.GetFileName(videoPath), (DateTime.UtcNow - started).TotalSeconds);
                return target;
            }

            TryDelete(temp);
            _failed[target] = lastError ?? "ffmpeg exit code " + proc.ExitCode.ToString(CultureInfo.InvariantCulture);
            _logger.LogWarning("JellyStations: could not extract subtitle track {Index} of {File}: {Error}", streamIndex, videoPath, _failed[target]);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsUsable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
