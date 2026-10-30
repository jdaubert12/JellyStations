namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>Recognises the image formats a logo may be stored as, from the file's first bytes.</summary>
public static class ImageSniff
{
    /// <summary>The file extension (with the dot) for a PNG, JPEG, WebP or GIF image, or null for anything else.</summary>
    public static string? Extension(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ".png";
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return ".jpg";
        }

        if (head.Length >= 6 && head[..6].SequenceEqual("GIF87a"u8) || head.Length >= 6 && head[..6].SequenceEqual("GIF89a"u8))
        {
            return ".gif";
        }

        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        return null;
    }
}
