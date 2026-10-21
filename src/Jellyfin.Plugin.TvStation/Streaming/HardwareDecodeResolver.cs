using Jellyfin.Plugin.TvStation.Models;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>
/// Turns the profile's hardware settings into concrete ffmpeg values: "auto" becomes the accelerator matching
/// Jellyfin's own transcoding setting, and an empty device becomes the device Jellyfin is configured with.
/// </summary>
public static class HardwareDecodeResolver
{
    public static EncodingProfile Resolve(EncodingProfile profile, HardwareAccelerationType jellyfinType, string? qsvDevice, string? vaapiDevice, bool isWindows)
    {
        var resolved = profile.Clone();
        var kind = (profile.HardwareDecode ?? string.Empty).Trim().ToLowerInvariant();
        var device = (profile.HardwareDevice ?? string.Empty).Trim();

        if (kind == EncodingProfile.HardwareDecodeAuto)
        {
            kind = jellyfinType switch
            {
                HardwareAccelerationType.qsv => "qsv",
                HardwareAccelerationType.vaapi => "vaapi",
                HardwareAccelerationType.nvenc => "cuda",
                HardwareAccelerationType.videotoolbox => "videotoolbox",
                HardwareAccelerationType.amf => isWindows ? "d3d11va" : "vaapi",
                _ => string.Empty
            };
        }

        if (!EncodingProfile.HardwareDecodeKinds.Contains(kind, StringComparer.Ordinal))
        {
            kind = string.Empty;
        }

        if (device.Length == 0)
        {
            device = kind switch
            {
                "qsv" => FirstNonEmpty(qsvDevice, vaapiDevice),
                "vaapi" => FirstNonEmpty(vaapiDevice, qsvDevice),
                _ => string.Empty
            };
        }

        if (device.Length == 0 && profile.IsVaapiEncoder)
        {
            // Both Jellyfin devices are DRM render nodes on Linux; either one serves a VA-API encoder.
            device = FirstNonEmpty(vaapiDevice, qsvDevice);
        }

        resolved.HardwareDecode = kind;
        resolved.HardwareDevice = device;
        return resolved;
    }

    private static string FirstNonEmpty(string? a, string? b)
    {
        if (!string.IsNullOrWhiteSpace(a))
        {
            return a.Trim();
        }

        return string.IsNullOrWhiteSpace(b) ? string.Empty : b.Trim();
    }
}
