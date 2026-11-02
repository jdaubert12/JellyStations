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

    public EncodingProfile Clone() => (EncodingProfile)MemberwiseClone();
}
