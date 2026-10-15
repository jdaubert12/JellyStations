namespace Jellyfin.Plugin.TvStation.Models;

/// <summary>Corner of the picture a logo overlay sits in.</summary>
public enum LogoCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// Station logo drawn over the video (a "bug"), like a broadcast channel. Off by default because the overlay
/// filter costs encoder CPU. Applies to programs and bumpers; station cards already carry the logo.
/// </summary>
public sealed class LogoOverlayConfig
{
    public const int DefaultOpacity = 70;
    public const int DefaultHeightPercent = 8;

    public bool Enabled { get; set; }

    public LogoCorner Corner { get; set; } = LogoCorner.TopRight;

    /// <summary>Opacity in percent, 10-100.</summary>
    public int Opacity { get; set; } = DefaultOpacity;

    /// <summary>Logo height as a percentage of the picture height, 3-25. Width follows the image's aspect ratio.</summary>
    public int HeightPercent { get; set; } = DefaultHeightPercent;

    public void Normalize()
    {
        Opacity = Math.Clamp(Opacity, 10, 100);
        HeightPercent = Math.Clamp(HeightPercent, 3, 25);
        if (!Enum.IsDefined(Corner))
        {
            Corner = LogoCorner.TopRight;
        }
    }
}
