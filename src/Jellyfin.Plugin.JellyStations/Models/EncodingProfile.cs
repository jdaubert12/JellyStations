namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>Fixed output profile every program on a station is normalized to.</summary>
public sealed class EncodingProfile
{
    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    public int Fps { get; set; } = 30;

    public int VideoBitrateKbps { get; set; } = 3500;

    public int AudioBitrateKbps { get; set; } = 160;

    /// <summary>ffmpeg video encoder name, e.g. libx264, h264_nvenc, h264_qsv, h264_vaapi.</summary>
    public string VideoEncoder { get; set; } = "libx264";

    public string Preset { get; set; } = "veryfast";

    public string H264Profile { get; set; } = "high";

    public string H264Level { get; set; } = "4.1";

    public int GopSeconds { get; set; } = 2;

    /// <summary>
    /// Hardware decoder for the source files: <see cref="HardwareDecodeAuto"/> follows Jellyfin's transcoding
    /// settings, empty means software, or one of <see cref="HardwareDecodeKinds"/>. Decoded frames come back to
    /// system memory so the scale, tonemap and logo filters still run in software; the encoder is chosen separately.
    /// </summary>
    public string HardwareDecode { get; set; } = HardwareDecodeAuto;

    /// <summary>
    /// Device for hardware decoding and VA-API encoding: a DRM render node (/dev/dri/renderD128), a CUDA or
    /// D3D11 adapter index, or empty for Jellyfin's configured device (or the driver default).
    /// </summary>
    public string HardwareDevice { get; set; } = string.Empty;

    /// <summary>Extra args inserted before the input (e.g. hwaccel flags).</summary>
    public string ExtraInputArgs { get; set; } = string.Empty;

    /// <summary>Extra args appended after the output options.</summary>
    public string ExtraOutputArgs { get; set; } = string.Empty;

    /// <summary>Preferred audio language (ISO 639-2), used when compiling items.</summary>
    public string AudioLanguagePreference { get; set; } = "eng";

    /// <summary>
    /// What the channel is encoded and tagged as: <see cref="OutputSdr"/> (8-bit BT.709) or <see cref="OutputHdr10"/>
    /// (10-bit BT.2020 PQ, HEVC Main 10). A live stream cannot change format between programs, so an HDR10 channel
    /// plays its SDR sources, slates, cards and weather screens converted into the PQ container; HDR sources keep
    /// their range (HLG is converted to PQ). Needs an HEVC encoder.
    /// </summary>
    public string OutputRange { get; set; } = OutputSdr;

    /// <summary>
    /// When a source is deinterlaced before scaling: <see cref="DeinterlaceAuto"/> (the files Jellyfin's probe reports
    /// as interlaced), <see cref="DeinterlaceAlways"/> (every file, for libraries whose field-order flags are missing)
    /// or <see cref="DeinterlaceOff"/>. Done in software by bwdif, one frame per field pair, or one frame per field on
    /// a 50 or 60 fps profile.
    /// </summary>
    public string Deinterlace { get; set; } = DeinterlaceAuto;

    public const string DeinterlaceAuto = "auto";

    public const string DeinterlaceAlways = "always";

    public const string DeinterlaceOff = "off";

    public static readonly string[] DeinterlaceModes = { DeinterlaceAuto, DeinterlaceAlways, DeinterlaceOff };

    /// <summary>True when a file is to be deinterlaced under this profile; blank or unknown modes count as auto.</summary>
    public bool Deinterlaces(bool fileInterlaced) => (Deinterlace ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        DeinterlaceAlways => true,
        DeinterlaceOff => false,
        _ => fileInterlaced
    };

    /// <summary>
    /// How a program is resized to the channel's frame, up or down: <see cref="ScalerBicubic"/> (the default),
    /// <see cref="ScalerLanczos"/> (sharper, close to twice the scaling CPU, can ring on hard edges),
    /// <see cref="ScalerSpline"/> (smooth, the costliest) or <see cref="ScalerBilinear"/> (softest). ffmpeg's
    /// software scaler in every case; the cost is small beside the encode.
    /// </summary>
    public string Scaler { get; set; } = ScalerBicubic;

    public const string ScalerBicubic = "bicubic";

    public const string ScalerLanczos = "lanczos";

    public const string ScalerSpline = "spline";

    public const string ScalerBilinear = "bilinear";

    public static readonly string[] Scalers = { ScalerBicubic, ScalerLanczos, ScalerSpline, ScalerBilinear };

    /// <summary>
    /// The scale filter's flags value for this profile. Only a listed name gets into ffmpeg's filter text; blank or
    /// anything else (a profile from before the setting, a hand-edited file) is bicubic.
    /// </summary>
    public string ScalerFlags()
    {
        var name = (Scaler ?? string.Empty).Trim().ToLowerInvariant();
        return Array.IndexOf(Scalers, name) >= 0 ? name : ScalerBicubic;
    }

    public const string OutputSdr = "sdr";

    public const string OutputHdr10 = "hdr10";

    public static readonly string[] OutputRanges = { OutputSdr, OutputHdr10 };

    public const string HardwareDecodeAuto = "auto";

    /// <summary>ffmpeg -hwaccel names the plugin knows how to set up.</summary>
    public static readonly string[] HardwareDecodeKinds = { "qsv", "vaapi", "cuda", "videotoolbox", "d3d11va" };

    /// <summary>True when the encoder is a VA-API encoder, which only accepts frames already uploaded to the GPU.</summary>
    public bool IsVaapiEncoder => VideoEncoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for libx264 and the other lib* encoders, which never touch a GPU device.</summary>
    public bool IsSoftwareEncoder => VideoEncoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for libx265 and the hevc_* encoders.</summary>
    public bool IsHevcEncoder => VideoEncoder.StartsWith("hevc", StringComparison.OrdinalIgnoreCase) || VideoEncoder.Contains("265", StringComparison.Ordinal);

    /// <summary>True when the channel is encoded as HDR10.</summary>
    public bool IsHdrOutput => string.Equals(OutputRange, OutputHdr10, StringComparison.OrdinalIgnoreCase);

    /// <summary>The video codec the channel carries, as Jellyfin names it: hevc or h264.</summary>
    public string OutputCodec => IsHevcEncoder ? "hevc" : "h264";

    /// <summary>H.264 levels from 3 up: level, largest frame in macroblocks, most macroblocks per second (table A-1).</summary>
    private static readonly (double Level, int MaxFrame, int MaxRate)[] H264Levels =
    {
        (3.0, 1620, 40500), (3.1, 3600, 108000), (3.2, 5120, 216000),
        (4.0, 8192, 245760), (4.1, 8192, 245760), (4.2, 8704, 522240),
        (5.0, 22080, 589824), (5.1, 36864, 983040), (5.2, 36864, 2073600),
        (6.0, 139264, 4177920), (6.1, 139264, 8355840), (6.2, 139264, 16711680)
    };

    /// <summary>
    /// The H.264 level the channel is encoded and declared at: <see cref="H264Level"/>, raised to the lowest level
    /// that covers the frame size and rate when the configured one falls short (4.1 stops at 1080p30; 1080p60 needs
    /// 4.2 and 4K at 30 fps needs 5.1). libx264 writes a too-low level into the stream as asked, and a client that
    /// trusts it sets up a decoder the frames do not fit. A level that is not a number is passed through as it is.
    /// </summary>
    public string H264LevelForFrame()
    {
        if (!double.TryParse(H264Level, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var configured))
        {
            return H264Level;
        }

        // x264 reads "41" as 4.1.
        var asked = configured >= 7 ? configured / 10 : configured;
        var frame = ((Width + 15) / 16) * (long)((Height + 15) / 16);
        var rate = frame * Math.Max(1, Fps);
        foreach (var (level, maxFrame, maxRate) in H264Levels)
        {
            if (frame <= maxFrame && rate <= maxRate)
            {
                return asked >= level ? H264Level : level.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return H264Level;
    }

    /// <summary>
    /// The bitrate guide by picture size (the row whose pixel count the frame exceeds), in kbps at up to 30 fps:
    /// below Low the picture is visibly starved, around Suits it looks right. H.264 figures assume the plugin's
    /// fast presets; HEVC needs about two thirds as much. The 4K row's H.264 figures only keep an H.264 4K channel
    /// (warned about on its own) from being called wasteful.
    /// </summary>
    private static readonly (long MoreThanPixels, int LowH264, int SuitsH264, int LowHevc, int SuitsHevc)[] BitrateGuide =
    {
        (2560L * 1440, 10000, 30000, 10000, 20000),
        (1920L * 1080, 6000, 12000, 4000, 8000),
        (1280L * 720, 3000, 8000, 2000, 5000),
        (854L * 480, 1500, 3500, 1000, 2500),
        (0, 600, 1500, 400, 1000)
    };

    /// <summary>From this frame rate up, the guide's figures are multiplied by <see cref="HighFrameRateFactor"/>.</summary>
    public const int HighFrameRate = 50;

    public const double HighFrameRateFactor = 1.5;

    /// <summary>At this many times the figure that suits the picture, the bitrate only costs bandwidth.</summary>
    public const double ExcessBitrateFactor = 2.5;

    /// <summary>Below this AAC stereo sounds thin.</summary>
    public const int LowAudioBitrateKbps = 96;

    /// <summary>True when the frame is larger than 2560x1440: 4K and its neighbours.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUltraHd => Width * (long)Height > 2560L * 1440;

    /// <summary>The video bitrates the guide gives this profile's size, frame rate and codec, in kbps.</summary>
    public (int Low, int Suits, int Excess) BitrateGuideFor()
    {
        var pixels = Width * (long)Height;
        var row = BitrateGuide.First(r => pixels > r.MoreThanPixels);
        var factor = Fps >= HighFrameRate ? HighFrameRateFactor : 1.0;
        var low = (int)Math.Round((IsHevcEncoder ? row.LowHevc : row.LowH264) * factor);
        var suits = (int)Math.Round((IsHevcEncoder ? row.SuitsHevc : row.SuitsH264) * factor);
        return (low, suits, (int)Math.Round(suits * ExcessBitrateFactor));
    }

    /// <summary>
    /// What is likely to disappoint on this profile, as sentences for the user: 4K from an H.264 encoder (few players
    /// take it, so most viewers get a Jellyfin transcode), a video bitrate too low for the picture size and frame
    /// rate (left where a smaller channel had it), one so high it only costs bandwidth (left where a 4K channel had
    /// it), and an audio bitrate that sounds thin. Empty for a sound profile, the shipped default among them. The
    /// page shows the same sentences at the top of the encoding form (encodingConcerns in jellystations.js).
    /// </summary>
    public List<string> Concerns()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var concerns = new List<string>();
        var label = (IsUltraHd ? "4K" : Height.ToString(inv) + "p") + (Fps >= HighFrameRate ? Fps.ToString(inv) : string.Empty);
        if (IsUltraHd && !IsHevcEncoder && !string.IsNullOrWhiteSpace(VideoEncoder))
        {
            concerns.Add("4K with " + VideoEncoder + " is H.264, which few players take at this size, so most viewers get a Jellyfin transcode. Pick an HEVC encoder.");
        }

        var (low, suits, excess) = BitrateGuideFor();
        if (IsUltraHd && !IsHevcEncoder)
        {
            // An H.264 4K channel is told to move to HEVC, so the figure to aim for is HEVC's.
            suits = (int)Math.Round(suits * 2 / 3.0);
        }

        var target = "Around " + suits.ToString(inv) + " kbps suits " + label + (IsUltraHd || IsHevcEncoder ? " HEVC" : string.Empty) + ".";
        if (VideoBitrateKbps < low)
        {
            concerns.Add(label + " at " + VideoBitrateKbps.ToString(inv) + " kbps will look soft and blocky. " + target);
        }
        else if (VideoBitrateKbps >= excess)
        {
            concerns.Add(label + " at " + VideoBitrateKbps.ToString(inv) + " kbps is more than the picture can use; it only makes every client's download and Jellyfin's transcodes larger. " + target);
        }

        if (AudioBitrateKbps < LowAudioBitrateKbps)
        {
            concerns.Add("Audio at " + AudioBitrateKbps.ToString(inv) + " kbps will sound thin. 128 kbps or more suits AAC stereo.");
        }

        return concerns;
    }

    public EncodingProfile Clone() => (EncodingProfile)MemberwiseClone();
}
