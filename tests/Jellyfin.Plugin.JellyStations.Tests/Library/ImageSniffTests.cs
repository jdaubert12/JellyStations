using System.Text;
using Jellyfin.Plugin.JellyStations.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class ImageSniffTests
{
    [Fact]
    public void RecognisesTheFourLogoFormats()
    {
        Assert.Equal(".png", ImageSniff.Extension(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }));
        Assert.Equal(".jpg", ImageSniff.Extension(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16 }));
        Assert.Equal(".gif", ImageSniff.Extension(Encoding.ASCII.GetBytes("GIF89a\u0001\u0000")));
        Assert.Equal(".gif", ImageSniff.Extension(Encoding.ASCII.GetBytes("GIF87a\u0001\u0000")));
        Assert.Equal(".webp", ImageSniff.Extension(Encoding.ASCII.GetBytes("RIFF\u0000\u0000\u0000\u0000WEBPVP8 ")));
    }

    [Fact]
    public void RejectsAnythingElse()
    {
        Assert.Null(ImageSniff.Extension(Array.Empty<byte>()));
        Assert.Null(ImageSniff.Extension(new byte[] { 0x89, 0x50 }));
        Assert.Null(ImageSniff.Extension(Encoding.ASCII.GetBytes("<html><script>alert(1)</script>")));
        Assert.Null(ImageSniff.Extension(Encoding.ASCII.GetBytes("RIFF\u0000\u0000\u0000\u0000WAVEfmt ")));
        Assert.Null(ImageSniff.Extension(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }));
    }
}
