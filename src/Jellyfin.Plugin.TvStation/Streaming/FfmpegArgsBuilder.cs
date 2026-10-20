using System.Globalization;
using System.Text;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>
/// Builds ffmpeg argument lists for program and slate segments. Output is not paced by ffmpeg (-re);
/// the restreamer paces it from the TS PCR clock and back-pressures ffmpeg through the pipe.
/// </summary>
public static class FfmpegArgsBuilder
{
    /// <summary>A logo to draw over the video: image path plus placement.</summary>
    public sealed record LogoOverlayInput(string Path, LogoCorner Corner, int Opacity, int HeightPercent);

    /// <summary>Arguments to play an item's (first) file from an offset, normalized to the profile, as real-time MPEG-TS on stdout.</summary>
    public static List<string> ForFile(PlayableItem item, TimeSpan seek, TimeSpan? duration, TimeSpan tsOffset, EncodingProfile p, LogoOverlayInput? overlay = null)
        => ForSource(item.Path, item.HasAudio, item.AudioStreamIndex, seek, duration, tsOffset, p, overlay);

    /// <summary>Arguments to play one file (an item, or one part of a multi-part item) from an offset.</summary>
    public static List<string> ForSource(string path, bool hasAudio, int? audioStreamIndex, TimeSpan seek, TimeSpan? duration, TimeSpan tsOffset, EncodingProfile p, LogoOverlayInput? overlay = null)
    {
        var args = new List<string>();
        Common(args);
        AddSplit(args, p.ExtraInputArgs);

        if (seek > TimeSpan.FromMilliseconds(500))
        {
            args.Add("-ss");
            args.Add(Sec(seek));
        }

        args.Add("-i");
        args.Add(path);

        var needSilence = !hasAudio;
        if (needSilence)
        {
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add("anullsrc=r=48000:cl=stereo");
        }

        var logoIndex = -1;
        if (overlay is not null)
        {
            // A single-frame image; the overlay filter repeats its last frame for the whole program.
            logoIndex = needSilence ? 2 : 1;
            args.Add("-i");
            args.Add(overlay.Path);
        }

        if (duration.HasValue && duration.Value > TimeSpan.Zero)
        {
            args.Add("-t");
            args.Add(Sec(duration.Value));
        }

        if (needSilence)
        {
            args.Add("-shortest");
        }

        var audioMap = needSilence ? "1:a:0" : "0:a:" + (audioStreamIndex ?? 0).ToString(CultureInfo.InvariantCulture) + "?";
        if (overlay is null)
        {
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add(audioMap);
            args.Add("-vf");
            args.Add(VideoFilter(p));
        }
        else
        {
            args.Add("-filter_complex");
            args.Add(OverlayFilter(p, overlay, logoIndex));
            args.Add("-map");
            args.Add("[v]");
            args.Add("-map");
            args.Add(audioMap);
            args.Add("-pix_fmt");
            args.Add("yuv420p");
        }

        Output(args, p, tsOffset);
        return args;
    }

    /// <summary>Normalized video with the logo scaled, faded and pinned to a corner with a small margin.</summary>
    public static string OverlayFilter(EncodingProfile p, LogoOverlayInput overlay, int logoIndex)
    {
        var height = Math.Max(8, p.Height * Math.Clamp(overlay.HeightPercent, 3, 25) / 100);
        var margin = Math.Max(4, p.Height / 36);
        var alpha = (Math.Clamp(overlay.Opacity, 10, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
        var position = overlay.Corner switch
        {
            LogoCorner.TopLeft => $"{margin}:{margin}",
            LogoCorner.BottomLeft => $"{margin}:H-h-{margin}",
            LogoCorner.BottomRight => $"W-w-{margin}:H-h-{margin}",
            _ => $"W-w-{margin}:{margin}"
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[0:v]{VideoFilter(p)}[base];[{logoIndex}:v]scale=-1:{height},format=rgba,colorchannelmixer=aa={alpha}[logo];[base][logo]overlay={position}:format=yuv420[v]");
    }

    /// <summary>Arguments for a generated slate (color card with optional text and logo).</summary>
    public static List<string> ForSlate(TimeSpan duration, TimeSpan tsOffset, EncodingProfile p, string background, string? text, string? logoPath)
    {
        var args = new List<string>();
        Common(args);

        args.Add("-f");
        args.Add("lavfi");
        args.Add("-i");
        args.Add($"color=c={NormalizeColor(background)}:s={p.Width}x{p.Height}:r={p.Fps}");
        args.Add("-f");
        args.Add("lavfi");
        args.Add("-i");
        args.Add("anullsrc=r=48000:cl=stereo");

        var hasLogo = !string.IsNullOrEmpty(logoPath) && File.Exists(logoPath);
        if (hasLogo)
        {
            args.Add("-loop");
            args.Add("1");
            args.Add("-i");
            args.Add(logoPath!);
        }

        args.Add("-t");
        args.Add(Sec(duration));

        var filter = new StringBuilder();
        var videoLabel = "0:v";
        if (hasLogo)
        {
            filter.Append(CultureInfo.InvariantCulture, $"[2:v]scale=-1:{p.Height / 4}[logo];[0:v][logo]overlay=(W-w)/2:(H-h)/2-{p.Height / 10}[v0]");
            videoLabel = "v0";
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            if (filter.Length > 0)
            {
                filter.Append(';');
            }

            var y = hasLogo ? "(h*0.62)" : "(h-text_h)/2";
            filter.Append(CultureInfo.InvariantCulture, $"[{videoLabel}]drawtext=text='{EscapeDrawText(text)}':fontcolor=white:fontsize={Math.Max(16, p.Height / 18)}:x=(w-text_w)/2:y={y}[v1]");
            videoLabel = "v1";
        }

        if (filter.Length > 0)
        {
            args.Add("-filter_complex");
            args.Add(filter.ToString());
            args.Add("-map");
            args.Add("[" + videoLabel + "]");
        }
        else
        {
            args.Add("-map");
            args.Add("0:v");
        }

        args.Add("-map");
        args.Add("1:a");

        if (filter.Length == 0)
        {
            args.Add("-vf");
            args.Add("format=yuv420p");
        }
        else
        {
            args.Add("-pix_fmt");
            args.Add("yuv420p");
        }

        Output(args, p, tsOffset);
        return args;
    }

    private static void Common(List<string> args)
    {
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-nostdin", "-nostats" });
    }

    private static string VideoFilter(EncodingProfile p)
    {
        return $"scale={p.Width}:{p.Height}:force_original_aspect_ratio=decrease:flags=bicubic,pad={p.Width}:{p.Height}:(ow-iw)/2:(oh-ih)/2,fps={p.Fps},format=yuv420p";
    }

    private static void Output(List<string> args, EncodingProfile p, TimeSpan tsOffset)
    {
        var gop = Math.Max(1, p.Fps * Math.Max(1, p.GopSeconds));
        args.Add("-c:v");
        args.Add(p.VideoEncoder);
        if (string.Equals(p.VideoEncoder, "libx264", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-preset");
            args.Add(p.Preset);
            args.Add("-profile:v");
            args.Add(p.H264Profile);
            args.Add("-level");
            args.Add(p.H264Level);
            args.Add("-x264-params");
            args.Add("scenecut=0:open-gop=0");
        }

        args.Add("-b:v");
        args.Add(p.VideoBitrateKbps + "k");
        args.Add("-maxrate");
        args.Add((int)(p.VideoBitrateKbps * 1.1) + "k");
        args.Add("-bufsize");
        args.Add(p.VideoBitrateKbps * 2 + "k");
        args.Add("-g");
        args.Add(gop.ToString(CultureInfo.InvariantCulture));
        args.Add("-keyint_min");
        args.Add(gop.ToString(CultureInfo.InvariantCulture));
        args.Add("-sc_threshold");
        args.Add("0");
        args.Add("-force_key_frames");
        args.Add("expr:gte(t,n_forced*" + Math.Max(1, p.GopSeconds).ToString(CultureInfo.InvariantCulture) + ")");

        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add(p.AudioBitrateKbps + "k");
        args.Add("-ar");
        args.Add("48000");
        args.Add("-ac");
        args.Add("2");
        args.Add("-af");
        args.Add("aresample=async=1:first_pts=0");

        args.Add("-sn");
        args.Add("-dn");
        args.Add("-map_metadata");
        args.Add("-1");
        args.Add("-map_chapters");
        args.Add("-1");

        args.Add("-output_ts_offset");
        args.Add(Sec(tsOffset));
        args.Add("-muxdelay");
        args.Add("0");
        args.Add("-muxpreload");
        args.Add("0");
        args.Add("-mpegts_flags");
        args.Add("+initial_discontinuity");
        args.Add("-streamid");
        args.Add("0:256");
        args.Add("-streamid");
        args.Add("1:257");
        args.Add("-f");
        args.Add("mpegts");

        AddSplit(args, p.ExtraOutputArgs);
        args.Add("pipe:1");
    }

    private static void AddSplit(List<string> args, string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            return;
        }

        args.AddRange(SplitArgs(extra));
    }

    /// <summary>Splits a command-line fragment honoring single and double quotes.</summary>
    public static IEnumerable<string> SplitArgs(string input)
    {
        var current = new StringBuilder();
        char? quote = null;
        foreach (var ch in input)
        {
            if (quote.HasValue)
            {
                if (ch == quote.Value)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"' || ch == '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

    private static string NormalizeColor(string c)
    {
        if (string.IsNullOrWhiteSpace(c))
        {
            return "0x101010";
        }

        return c.StartsWith('#') ? "0x" + c[1..] : c;
    }

    private static string EscapeDrawText(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "’", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal);
    }

    /// <summary>Quotes args for logging only.</summary>
    public static string ToLogString(IEnumerable<string> args)
    {
        return string.Join(' ', args.Select(a => a.Any(char.IsWhiteSpace) || a.Contains('\'') ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : a));
    }
}
