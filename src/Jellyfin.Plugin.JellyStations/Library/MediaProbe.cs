using System.Diagnostics;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Streaming;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// Finds ffprobe and ffmpeg and runs them on a file the plugin reads without Jellyfin's library: the folder
/// scanners (<see cref="MusicFolderScanner"/>, <see cref="ClipFolderScanner"/>) ask it about each file once.
/// </summary>
public sealed class MediaProbe
{
    private const int TimeoutMs = 15000;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly SettingsRepository _settings;

    public MediaProbe(IMediaEncoder mediaEncoder, SettingsRepository settings)
    {
        _mediaEncoder = mediaEncoder;
        _settings = settings;
    }

    /// <summary>The ffmpeg in use: the settings override, else Jellyfin's.</summary>
    public string FfmpegPath() => StreamSessionManager.ResolveFfmpegPath(_settings.Get().FfmpegPathOverride, _mediaEncoder.EncoderPath);

    /// <summary>Jellyfin's ffprobe, else one beside the ffmpeg in use (an override may point at a build with its own); null when there is none.</summary>
    public string? ProbePath()
    {
        var probe = _mediaEncoder.ProbePath;
        if (!string.IsNullOrWhiteSpace(probe) && File.Exists(probe))
        {
            return probe;
        }

        var dir = Path.GetDirectoryName(FfmpegPath());
        if (dir is not null)
        {
            foreach (var name in new[] { "ffprobe", "ffprobe.exe" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Runs a tool to the end and returns what it wrote to standard output, or to standard error when <paramref name="stderr"/> is set.</summary>
    public static string Run(string exe, IReadOnlyList<string> args, bool stderr)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(TimeoutMs))
        {
            try
            {
                proc.Kill(true);
            }
            catch (Exception)
            {
                // Already gone.
            }

            throw new TimeoutException(exe + " did not finish within 15 s");
        }

        Task.WaitAll(outTask, errTask);
        return stderr ? errTask.Result : outTask.Result;
    }
}
