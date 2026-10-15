namespace Jellyfin.Plugin.TvStation.Models;

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

    /// <summary>Extra args inserted before the input (e.g. hwaccel flags).</summary>
    public string ExtraInputArgs { get; set; } = string.Empty;

    /// <summary>Extra args appended after the output options.</summary>
    public string ExtraOutputArgs { get; set; } = string.Empty;

    /// <summary>Preferred audio language (ISO 639-2), used when compiling items.</summary>
    public string AudioLanguagePreference { get; set; } = "eng";

    public EncodingProfile Clone() => (EncodingProfile)MemberwiseClone();
}
