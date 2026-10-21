using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>
/// Which source files a hardware decoder is handed. The <c>*_qsv</c>, <c>*_cuvid</c> and friends do not fall back to
/// software on their own: a file the GPU cannot decode (10-bit H.264, MPEG-4 ASP, a codec the user unticked in
/// Jellyfin) makes ffmpeg exit with nothing written. So, like Jellyfin, the plugin decides per file from the codec,
/// profile and bit depth recorded at compile time, and plays everything else in software.
/// </summary>
public sealed class HardwareDecodePolicy
{
    /// <summary>The codec names Jellyfin lists under "Enable hardware decoding for".</summary>
    public static readonly string[] AllCodecs = { "h264", "hevc", "mpeg2video", "vc1", "vp8", "vp9", "av1" };

    public HardwareDecodePolicy(string kind, IEnumerable<string> codecs, bool hevc10 = true, bool vp9Ten = true, bool hevcRext10 = false, bool hevcRext12 = false, bool fromJellyfin = false)
    {
        Kind = kind;
        Codecs = new HashSet<string>(codecs.Select(NormalizeCodec), StringComparer.Ordinal);
        Hevc10 = hevc10;
        Vp9Ten = vp9Ten;
        HevcRext10 = hevcRext10;
        HevcRext12 = hevcRext12;
        FromJellyfin = fromJellyfin;
    }

    /// <summary>Every codec a GPU decoder can take, with 10-bit HEVC and VP9 but no RExt: for a decoder chosen by hand.</summary>
    public static HardwareDecodePolicy Permissive(string kind) => new(kind, AllCodecs);

    /// <summary>Nothing goes to the hardware decoder.</summary>
    public static HardwareDecodePolicy Software { get; } = new(string.Empty, Array.Empty<string>());

    public string Kind { get; }

    public IReadOnlySet<string> Codecs { get; }

    public bool Hevc10 { get; }

    public bool Vp9Ten { get; }

    public bool HevcRext10 { get; }

    public bool HevcRext12 { get; }

    /// <summary>True when the codec list came from Jellyfin's transcoding settings rather than the built-in default.</summary>
    public bool FromJellyfin { get; }

    public static string NormalizeCodec(string? codec)
    {
        var c = (codec ?? string.Empty).Trim().ToLowerInvariant();
        return c switch
        {
            "avc" or "h.264" => "h264",
            "h265" or "h.265" => "hevc",
            "mpeg2" => "mpeg2video",
            "wvc1" or "wmv3" => "vc1",
            _ => c
        };
    }

    /// <summary>Null when the hardware decoder should take this file; otherwise a short reason to decode it in software.</summary>
    public string? WhyNot(PlayablePart part) => WhyNot(part.VideoCodec, part.VideoProfile, part.VideoBitDepth, part.PixelFormat);

    public string? WhyNot(string? codec, string? profile, int? bitDepth, string? pixelFormat)
    {
        if (Kind.Length == 0)
        {
            return "no hardware decoder";
        }

        var c = NormalizeCodec(codec);
        if (c.Length == 0)
        {
            // Unknown codec (compiled before this was recorded, or no probe): let ffmpeg try.
            return null;
        }

        var depth = bitDepth ?? DepthFromPixelFormat(pixelFormat) ?? 8;
        var chroma = ChromaFromPixelFormat(pixelFormat);
        var p = (profile ?? string.Empty).ToLowerInvariant();
        var rext = c == "hevc" && (p.Contains("rext", StringComparison.Ordinal) || chroma is "422" or "444");
        var label = Describe(c, profile, depth, chroma);

        if (!AllCodecs.Contains(c, StringComparer.Ordinal))
        {
            return label + " has no hardware decoder";
        }

        if (!Codecs.Contains(c))
        {
            return label + (FromJellyfin ? " is not ticked for hardware decoding in Jellyfin's transcoding settings" : " is not hardware decoded");
        }

        switch (c)
        {
            case "h264":
                if (depth > 8 || chroma is "422" or "444" || p.Contains("high 10", StringComparison.Ordinal) || p.Contains("high 4", StringComparison.Ordinal))
                {
                    return label + " is beyond what GPU decoders handle (8-bit 4:2:0 only)";
                }

                break;
            case "hevc":
                if (depth > 12)
                {
                    return label + " is beyond what GPU decoders handle";
                }

                if (rext)
                {
                    if (depth > 10 && !HevcRext12)
                    {
                        return label + " needs \"12-bit HEVC RExt\" hardware decoding, which is off";
                    }

                    if (depth <= 10 && !HevcRext10)
                    {
                        return label + " needs \"10-bit HEVC RExt\" hardware decoding, which is off";
                    }
                }
                else if (depth > 8 && !Hevc10)
                {
                    return label + " needs \"10-bit HEVC\" hardware decoding, which is off";
                }

                break;
            case "vp9":
                if (depth > 10)
                {
                    return label + " is beyond what GPU decoders handle";
                }

                if (depth > 8 && !Vp9Ten)
                {
                    return label + " needs \"10-bit VP9\" hardware decoding, which is off";
                }

                break;
            case "av1":
                if (depth > 10)
                {
                    return label + " is beyond what GPU decoders handle";
                }

                break;
            default:
                // mpeg2video, vc1, vp8: 8-bit 4:2:0 formats.
                if (depth > 8)
                {
                    return label + " is beyond what GPU decoders handle";
                }

                break;
        }

        return null;
    }

    public static string Describe(string codec, string? profile, int depth, string? chroma)
    {
        var name = codec switch
        {
            "h264" => "H.264",
            "hevc" => "HEVC",
            "mpeg2video" => "MPEG-2",
            "vc1" => "VC-1",
            "vp8" => "VP8",
            "vp9" => "VP9",
            "av1" => "AV1",
            "mpeg4" => "MPEG-4 ASP (DivX/Xvid)",
            "msmpeg4v3" => "MS-MPEG-4 v3 (DivX 3)",
            "wmv1" or "wmv2" => "WMV",
            _ => codec
        };
        var parts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(profile))
        {
            parts.Add(profile.Trim());
        }

        parts.Add(depth + "-bit");
        if (chroma is not null && chroma != "420")
        {
            parts.Add("4:" + chroma[1] + ":" + chroma[2]);
        }

        return string.Join(' ', parts);
    }

    /// <summary>"yuv420p10le" gives 10; "yuv420p" gives 8; null when the format says nothing.</summary>
    public static int? DepthFromPixelFormat(string? pixelFormat)
    {
        if (string.IsNullOrWhiteSpace(pixelFormat))
        {
            return null;
        }

        var f = pixelFormat.Trim().ToLowerInvariant();
        var i = f.IndexOf('p', StringComparison.Ordinal);
        while (i >= 0 && i + 1 < f.Length)
        {
            if (char.IsDigit(f[i + 1]))
            {
                var j = i + 1;
                while (j < f.Length && char.IsDigit(f[j]))
                {
                    j++;
                }

                return int.Parse(f[(i + 1)..j], System.Globalization.CultureInfo.InvariantCulture);
            }

            i = f.IndexOf('p', i + 1);
        }

        return f.StartsWith("yuv", StringComparison.Ordinal) || f.StartsWith("nv12", StringComparison.Ordinal) ? 8 : null;
    }

    /// <summary>"420", "422" or "444" from a pixel format, or null.</summary>
    public static string? ChromaFromPixelFormat(string? pixelFormat)
    {
        if (string.IsNullOrWhiteSpace(pixelFormat))
        {
            return null;
        }

        var f = pixelFormat.Trim().ToLowerInvariant();
        foreach (var chroma in new[] { "420", "422", "444" })
        {
            if (f.Contains(chroma, StringComparison.Ordinal))
            {
                return chroma;
            }
        }

        return f.StartsWith("nv12", StringComparison.Ordinal) || f.StartsWith("p010", StringComparison.Ordinal) ? "420" : null;
    }
}
