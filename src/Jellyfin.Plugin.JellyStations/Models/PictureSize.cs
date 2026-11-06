namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>
/// The picture-size tiers a source can ask for (<see cref="ContentSource.MinResolution"/>), judged the way Jellyfin
/// labels a file: by width or height, whichever reaches the tier, so a 3840x1600 scope film is 4K and a 1920x800
/// one is 1080p.
/// </summary>
public static class PictureSize
{
    public const string UltraHd = "4k";

    public const string FullHd = "1080p";

    public const string Hd = "720p";

    public static readonly string[] Tiers = { UltraHd, FullHd, Hd };

    /// <summary>The tier a spelling names ("4K", "UHD", "2160p", "1080", "720p"), or null for none or unknown.</summary>
    public static string? Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "4k" or "uhd" or "2160p" or "2160" => UltraHd,
        "1080p" or "1080" or "fhd" => FullHd,
        "720p" or "720" or "hd" => Hd,
        _ => null
    };

    /// <summary>
    /// True when a picture of this size reaches the tier. No tier admits everything; a file whose size is not known
    /// does not reach any.
    /// </summary>
    public static bool Meets(int? width, int? height, string? tier)
    {
        var (minWidth, minHeight) = Normalize(tier) switch
        {
            UltraHd => (3800, 2000),
            FullHd => (1900, 1000),
            Hd => (1260, 700),
            _ => (0, 0)
        };
        return minWidth == 0 || width >= minWidth || height >= minHeight;
    }

    /// <summary>"4K only", "1080p or better", "720p or better"; null without a tier.</summary>
    public static string? Describe(string? tier) => Normalize(tier) switch
    {
        UltraHd => "4K only",
        FullHd => "1080p or better",
        Hd => "720p or better",
        _ => null
    };
}
