using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Streaming;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

public class HardwareDecodeResolverTests
{
    private static EncodingProfile Resolve(EncodingProfile p, HardwareAccelerationType type, string? qsv = null, string? vaapi = null, bool windows = false)
        => HardwareDecodeResolver.Resolve(p, type, qsv, vaapi, windows);

    [Theory]
    [InlineData(HardwareAccelerationType.none, "")]
    [InlineData(HardwareAccelerationType.qsv, "qsv")]
    [InlineData(HardwareAccelerationType.vaapi, "vaapi")]
    [InlineData(HardwareAccelerationType.nvenc, "cuda")]
    [InlineData(HardwareAccelerationType.videotoolbox, "videotoolbox")]
    [InlineData(HardwareAccelerationType.v4l2m2m, "")]
    [InlineData(HardwareAccelerationType.rkmpp, "")]
    public void Auto_FollowsJellyfinsAccelerator(HardwareAccelerationType type, string expected)
    {
        var r = Resolve(new EncodingProfile(), type);
        Assert.Equal(expected, r.HardwareDecode);
    }

    [Fact]
    public void Auto_Amf_IsD3d11vaOnWindowsAndVaapiElsewhere()
    {
        Assert.Equal("d3d11va", Resolve(new EncodingProfile(), HardwareAccelerationType.amf, windows: true).HardwareDecode);
        Assert.Equal("vaapi", Resolve(new EncodingProfile(), HardwareAccelerationType.amf, windows: false).HardwareDecode);
    }

    [Fact]
    public void Auto_TakesJellyfinsDeviceForItsKind()
    {
        Assert.Equal("/dev/dri/renderD128", Resolve(new EncodingProfile(), HardwareAccelerationType.qsv, qsv: "/dev/dri/renderD128").HardwareDevice);
        Assert.Equal("/dev/dri/card1", Resolve(new EncodingProfile(), HardwareAccelerationType.vaapi, vaapi: "/dev/dri/card1").HardwareDevice);
        // A render node configured for the other Intel path still serves.
        Assert.Equal("/dev/dri/renderD129", Resolve(new EncodingProfile(), HardwareAccelerationType.qsv, vaapi: "/dev/dri/renderD129").HardwareDevice);
        Assert.Equal(string.Empty, Resolve(new EncodingProfile(), HardwareAccelerationType.nvenc, qsv: "/dev/dri/renderD128").HardwareDevice);
    }

    [Fact]
    public void ExplicitKind_IgnoresJellyfin()
    {
        var r = Resolve(new EncodingProfile { HardwareDecode = "cuda", HardwareDevice = "1" }, HardwareAccelerationType.qsv, qsv: "/dev/dri/renderD128");
        Assert.Equal("cuda", r.HardwareDecode);
        Assert.Equal("1", r.HardwareDevice);
    }

    [Fact]
    public void ExplicitDevice_Wins()
    {
        var r = Resolve(new EncodingProfile { HardwareDecode = "vaapi", HardwareDevice = "/dev/dri/renderD129" }, HardwareAccelerationType.vaapi, vaapi: "/dev/dri/renderD128");
        Assert.Equal("/dev/dri/renderD129", r.HardwareDevice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("  ")]
    [InlineData("bogus")]
    public void OffOrUnknown_IsSoftware(string kind)
    {
        var r = Resolve(new EncodingProfile { HardwareDecode = kind }, HardwareAccelerationType.qsv, qsv: "/dev/dri/renderD128");
        Assert.Equal(string.Empty, r.HardwareDecode);
    }

    [Fact]
    public void Kind_IsCaseInsensitive()
    {
        Assert.Equal("qsv", Resolve(new EncodingProfile { HardwareDecode = " QSV " }, HardwareAccelerationType.none).HardwareDecode);
    }

    [Fact]
    public void VaapiEncoder_WithoutDevice_TakesJellyfinsRenderNode()
    {
        var p = new EncodingProfile { VideoEncoder = "h264_vaapi", HardwareDecode = string.Empty };
        Assert.Equal("/dev/dri/renderD128", Resolve(p, HardwareAccelerationType.none, vaapi: "/dev/dri/renderD128").HardwareDevice);
        Assert.Equal("/dev/dri/renderD129", Resolve(p, HardwareAccelerationType.none, qsv: "/dev/dri/renderD129").HardwareDevice);
        Assert.Equal(string.Empty, Resolve(p, HardwareAccelerationType.none).HardwareDevice);
    }

    [Fact]
    public void Resolve_DoesNotMutateTheInput()
    {
        var p = new EncodingProfile();
        Resolve(p, HardwareAccelerationType.qsv, qsv: "/dev/dri/renderD128");
        Assert.Equal(EncodingProfile.HardwareDecodeAuto, p.HardwareDecode);
        Assert.Equal(string.Empty, p.HardwareDevice);
    }
}
