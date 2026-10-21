using System.Globalization;
using System.Text;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>
/// Builds ffmpeg argument lists for program and slate segments. Output is not paced by ffmpeg (-re);
/// the restreamer paces it from the TS PCR clock and back-pressures ffmpeg through the pipe.
/// Every segment is tagged BT.709 SDR; HDR sources are tone mapped so no PQ/HLG pixels or HDR
/// side data reach the stream (an HDR tag is what makes clients re-encode the channel).
/// </summary>
public static class FfmpegArgsBuilder
{
    /// <summary>The default DRM render node when a VA-API or QSV device is not configured anywhere.</summary>
    public const string DefaultDrmDevice = "/dev/dri/renderD128";

    /// <summary>A logo to draw over the video: image path plus placement.</summary>
    public sealed record LogoOverlayInput(string Path, LogoCorner Corner, int Opacity, int HeightPercent);

    /// <summary>Arguments to play an item's (first) file from an offset, normalized to the profile, as real-time MPEG-TS on stdout.</summary>
    public static List<string> ForFile(PlayableItem item, TimeSpan seek, TimeSpan? duration, TimeSpan tsOffset, EncodingProfile p, LogoOverlayInput? overlay = null)
        => ForSource(item.Path, item.HasAudio, item.AudioStreamIndex, item.HdrTransfer, seek, duration, tsOffset, p, overlay);

    /// <summary>Arguments to play one file (an item, or one part of a multi-part item) from an offset.</summary>
    /// <param name="hdrTransfer">The source's HDR transfer ("smpte2084" or "arib-std-b67") to tone map from; null or empty for SDR.</param>
    public static List<string> ForSource(string path, bool hasAudio, int? audioStreamIndex, string? hdrTransfer, TimeSpan seek, TimeSpan? duration, TimeSpan tsOffset, EncodingProfile p, LogoOverlayInput? overlay = null)
    {
        var args = new List<string>();
        Common(args);
        HardwareInputArgs(args, p, decode: true);
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
            args.Add(VideoFilter(p, hdrTransfer) + EncoderUploadSuffix(p));
        }
        else
        {
            args.Add("-filter_complex");
            args.Add(OverlayFilter(p, overlay, logoIndex, hdrTransfer));
            args.Add("-map");
            args.Add("[v]");
            args.Add("-map");
            args.Add(audioMap);
            if (!p.IsVaapiEncoder)
            {
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }
        }

        Output(args, p, tsOffset);
        return args;
    }

    /// <summary>Normalized video with the logo scaled, faded and pinned to a corner with a small margin.</summary>
    public static string OverlayFilter(EncodingProfile p, LogoOverlayInput overlay, int logoIndex, string? hdrTransfer = null)
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

        var graph = string.Create(
            CultureInfo.InvariantCulture,
            $"[0:v]{VideoFilter(p, hdrTransfer)}[base];[{logoIndex}:v]scale=-1:{height},format=rgba,colorchannelmixer=aa={alpha}[logo];[base][logo]overlay={position}:format=yuv420");

        // A VA-API encoder needs the composed frame on the GPU; everyone else takes it as is.
        return p.IsVaapiEncoder ? graph + "[ov];[ov]" + EncoderUploadSuffix(p).TrimStart(',') + "[v]" : graph + "[v]";
    }

    /// <summary>Arguments for a generated slate (color card with optional text and logo).</summary>
    public static List<string> ForSlate(TimeSpan duration, TimeSpan tsOffset, EncodingProfile p, string background, string? text, string? logoPath)
    {
        var args = new List<string>();
        Common(args);
        HardwareInputArgs(args, p, decode: false);

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
            filter.Append(CultureInfo.InvariantCulture, $";[{videoLabel}]format=yuv420p,{SdrTags}{EncoderUploadSuffix(p)}[v2]");
            videoLabel = "v2";
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
            args.Add("format=yuv420p," + SdrTags + EncoderUploadSuffix(p));
        }
        else if (!p.IsVaapiEncoder)
        {
            args.Add("-pix_fmt");
            args.Add("yuv420p");
        }

        Output(args, p, tsOffset);
        return args;
    }

    /// <summary>
    /// Hardware setup before the input: the decoder (<see cref="EncodingProfile.HardwareDecode"/>, already resolved
    /// from "auto") and, for a VA-API encoder, the device its frames are uploaded to. Decoded frames stay in system
    /// memory (no -hwaccel_output_format) so the software filter chain applies unchanged; ffmpeg itself falls back to
    /// software decoding when the accelerator cannot handle a file.
    /// </summary>
    public static void HardwareInputArgs(List<string> args, EncodingProfile p, bool decode)
    {
        var kind = decode ? (p.HardwareDecode ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
        var device = (p.HardwareDevice ?? string.Empty).Trim();
        var drm = device.Length > 0 ? device : DefaultDrmDevice;

        switch (kind)
        {
            case "qsv":
                if (device.Length > 0)
                {
                    args.Add("-qsv_device");
                    args.Add(device);
                }

                args.Add("-hwaccel");
                args.Add("qsv");
                break;
            case "vaapi":
                args.Add("-vaapi_device");
                args.Add(drm);
                args.Add("-hwaccel");
                args.Add("vaapi");
                break;
            case "cuda":
            case "d3d11va":
                args.Add("-hwaccel");
                args.Add(kind);
                if (device.Length > 0)
                {
                    args.Add("-hwaccel_device");
                    args.Add(device);
                }

                break;
            case "videotoolbox":
                args.Add("-hwaccel");
                args.Add("videotoolbox");
                break;
            default:
                break;
        }

        if (p.IsVaapiEncoder && kind != "vaapi")
        {
            args.Add("-vaapi_device");
            args.Add(drm);
        }
    }

    private static void Common(List<string> args)
    {
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-nostdin", "-nostats" });
    }

    /// <summary>Frame tags every segment leaves the graph with. ffmpeg 7+ encoders take color metadata from the frames, not from -color_* options.</summary>
    public const string SdrTags = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv";

    /// <summary>
    /// Scale (letterboxed) to the profile, constant frame rate, 8-bit 4:2:0, tagged BT.709. An HDR source is first
    /// tone mapped to BT.709 (the zscale/tonemap chain Jellyfin uses for software tone mapping) and stripped of its
    /// HDR side data. The HDR input properties are stated outright so zscale does not depend on the file's tags.
    /// </summary>
    public static string VideoFilter(EncodingProfile p, string? hdrTransfer = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"scale={p.Width}:{p.Height}:force_original_aspect_ratio=decrease:flags=bicubic");
        if (!string.IsNullOrEmpty(hdrTransfer))
        {
            sb.Append(CultureInfo.InvariantCulture, $",setparams=color_primaries=bt2020:color_trc={hdrTransfer}:colorspace=bt2020nc");
            sb.Append(",format=yuv420p10le,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0:peak=100,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,sidedata=mode=delete");
        }

        sb.Append(CultureInfo.InvariantCulture, $",pad={p.Width}:{p.Height}:(ow-iw)/2:(oh-ih)/2,fps={p.Fps},format=yuv420p,{SdrTags}");
        return sb.ToString();
    }

    /// <summary>Filters that hand the finished frame to the encoder: VA-API encoders only take frames already on the GPU.</summary>
    private static string EncoderUploadSuffix(EncodingProfile p) => p.IsVaapiEncoder ? ",format=nv12,hwupload" : string.Empty;

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

        // Always declare SDR BT.709: the filter chain delivers that, and a leaked BT.2020/PQ tag makes clients transcode.
        args.Add("-color_primaries");
        args.Add("bt709");
        args.Add("-color_trc");
        args.Add("bt709");
        args.Add("-colorspace");
        args.Add("bt709");
        args.Add("-color_range");
        args.Add("tv");

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
