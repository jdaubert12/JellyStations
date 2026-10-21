using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Streaming;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Streaming;

public class FfmpegArgsBuilderTests
{
    private static readonly EncodingProfile Profile = new() { HardwareDecode = string.Empty }; // 1280x720, software

    private const string SdrOptions = "-color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv";
    private const string SdrTags = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv";
    private const string PlainChain = "scale=1280:720:force_original_aspect_ratio=decrease:flags=bicubic,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=30,format=yuv420p," + SdrTags;
    private const string TonemapChain = "format=yuv420p10le,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0:peak=100,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,sidedata=mode=delete";

    private static PlayableItem Item(bool hasAudio = true, bool hdr = false, string transfer = "smpte2084") => new()
    {
        ItemId = Guid.NewGuid(),
        Path = "/media/show.mkv",
        RuntimeTicks = TimeSpan.FromMinutes(22).Ticks,
        Title = "Show",
        HasAudio = hasAudio,
        HdrTransfer = hdr ? transfer : null
    };

    private static string ValueAfter(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, flag + " not found");
        return args[i + 1];
    }

    private static string Joined(List<string> args) => string.Join(' ', args);

    private static int InputIndex(List<string> args) => args.IndexOf("-i");

    [Fact]
    public void WithoutOverlay_UsesPlainVideoFilter()
    {
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile);

        Assert.DoesNotContain("-filter_complex", args);
        Assert.Contains("-vf", args);
        Assert.Equal("0:v:0", ValueAfter(args, "-map"));
        Assert.Single(args, a => a == "-i");
        Assert.Equal(PlainChain, ValueAfter(args, "-vf"));
    }

    [Fact]
    public void EverySegment_IsTaggedSdrBt709()
    {
        // Frame tags in the graph (what ffmpeg 7+ encoders read) and the encoder options (older builds).
        var program = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile);
        var hdr = FfmpegArgsBuilder.ForFile(Item(hdr: true), TimeSpan.Zero, null, TimeSpan.Zero, Profile);
        var overlay = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile, new FfmpegArgsBuilder.LogoOverlayInput("/l.png", LogoCorner.TopRight, 70, 8));
        var slateText = FfmpegArgsBuilder.ForSlate(TimeSpan.FromSeconds(10), TimeSpan.Zero, Profile, "#000000", "Back soon", null);
        var slatePlain = FfmpegArgsBuilder.ForSlate(TimeSpan.FromSeconds(10), TimeSpan.Zero, Profile, "#000000", null, null);

        foreach (var args in new[] { program, hdr, overlay, slateText, slatePlain })
        {
            Assert.Contains(SdrOptions, Joined(args), StringComparison.Ordinal);
            var graph = args.Contains("-vf") ? ValueAfter(args, "-vf") : ValueAfter(args, "-filter_complex");
            Assert.Contains(SdrTags, graph, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HdrSource_IsToneMappedAfterScalingAndStrippedOfSideData()
    {
        var args = FfmpegArgsBuilder.ForFile(Item(hdr: true), TimeSpan.Zero, null, TimeSpan.Zero, Profile);
        var vf = ValueAfter(args, "-vf");

        Assert.Equal(
            "scale=1280:720:force_original_aspect_ratio=decrease:flags=bicubic,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc," + TonemapChain + ",pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=30,format=yuv420p," + SdrTags,
            vf);
    }

    [Fact]
    public void HlgSource_StatesItsOwnTransfer()
    {
        var vf = ValueAfter(FfmpegArgsBuilder.ForFile(Item(hdr: true, transfer: "arib-std-b67"), TimeSpan.Zero, null, TimeSpan.Zero, Profile), "-vf");
        Assert.Contains("setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc,format=yuv420p10le,zscale", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void SdrSource_HasNoTonemapFilters()
    {
        var vf = ValueAfter(FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile), "-vf");
        Assert.DoesNotContain("zscale", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("tonemap", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void HdrSource_WithOverlay_ToneMapsTheBaseLayer()
    {
        var overlay = new FfmpegArgsBuilder.LogoOverlayInput("/data/logo.png", LogoCorner.TopRight, 70, 8);
        var graph = ValueAfter(FfmpegArgsBuilder.ForFile(Item(hdr: true), TimeSpan.Zero, null, TimeSpan.Zero, Profile, overlay), "-filter_complex");

        Assert.Contains("[0:v]scale=1280:720:force_original_aspect_ratio=decrease:flags=bicubic,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc," + TonemapChain, graph, StringComparison.Ordinal);
        Assert.EndsWith("[base][logo]overlay=W-w-20:20:format=yuv420[v]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void WithOverlay_AddsLogoInputAndOverlayGraph()
    {
        var overlay = new FfmpegArgsBuilder.LogoOverlayInput("/data/logo.png", LogoCorner.TopRight, 70, 8);
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile, overlay);

        Assert.Equal(2, args.Count(a => a == "-i"));
        Assert.Equal("/data/logo.png", args[args.LastIndexOf("-i") + 1]);
        Assert.DoesNotContain("-vf", args);
        Assert.Equal("[v]", ValueAfter(args, "-map"));
        Assert.Equal("yuv420p", ValueAfter(args, "-pix_fmt"));

        var graph = ValueAfter(args, "-filter_complex");
        Assert.StartsWith("[0:v]scale=1280:720", graph, StringComparison.Ordinal);
        Assert.Contains("[1:v]scale=-1:57,format=rgba,colorchannelmixer=aa=0.70[logo]", graph, StringComparison.Ordinal);
        Assert.EndsWith("[base][logo]overlay=W-w-20:20:format=yuv420[v]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void WithOverlayAndSilentSource_LogoIsThirdInput()
    {
        var overlay = new FfmpegArgsBuilder.LogoOverlayInput("/data/logo.png", LogoCorner.BottomLeft, 100, 10);
        var args = FfmpegArgsBuilder.ForFile(Item(hasAudio: false), TimeSpan.Zero, null, TimeSpan.Zero, Profile, overlay);

        var graph = ValueAfter(args, "-filter_complex");
        Assert.Contains("[2:v]scale=-1:72,format=rgba,colorchannelmixer=aa=1.00[logo]", graph, StringComparison.Ordinal);
        Assert.Contains("overlay=20:H-h-20:format=yuv420[v]", graph, StringComparison.Ordinal);
        Assert.Contains("1:a:0", args);
        Assert.Contains("-shortest", args);
    }

    [Theory]
    [InlineData(LogoCorner.TopLeft, "overlay=20:20:")]
    [InlineData(LogoCorner.BottomRight, "overlay=W-w-20:H-h-20:")]
    public void Corners_MapToPositions(LogoCorner corner, string expected)
    {
        var graph = FfmpegArgsBuilder.OverlayFilter(Profile, new FfmpegArgsBuilder.LogoOverlayInput("x.png", corner, 50, 8), 1);
        Assert.Contains(expected, graph, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayValues_AreClamped()
    {
        var graph = FfmpegArgsBuilder.OverlayFilter(Profile, new FfmpegArgsBuilder.LogoOverlayInput("x.png", LogoCorner.TopRight, 500, 90), 1);
        Assert.Contains("scale=-1:180,", graph, StringComparison.Ordinal);
        Assert.Contains("aa=1.00", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void SoftwareDecode_AddsNoHardwareArgs()
    {
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile);
        Assert.DoesNotContain("-hwaccel", args);
        Assert.DoesNotContain("-vaapi_device", args);
        Assert.DoesNotContain("-qsv_device", args);
    }

    [Fact]
    public void UnresolvedAuto_IsTreatedAsSoftware()
    {
        // "auto" is resolved to a concrete kind by the session manager; the builder never guesses.
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, new EncodingProfile());
        Assert.Equal(EncodingProfile.HardwareDecodeAuto, new EncodingProfile().HardwareDecode);
        Assert.DoesNotContain("-hwaccel", args);
    }

    [Theory]
    [InlineData("qsv", "", "-hwaccel qsv")]
    [InlineData("qsv", "/dev/dri/renderD129", "-qsv_device /dev/dri/renderD129 -hwaccel qsv")]
    [InlineData("vaapi", "", "-vaapi_device /dev/dri/renderD128 -hwaccel vaapi")]
    [InlineData("vaapi", "/dev/dri/renderD129", "-vaapi_device /dev/dri/renderD129 -hwaccel vaapi")]
    [InlineData("cuda", "", "-hwaccel cuda")]
    [InlineData("cuda", "1", "-hwaccel cuda -hwaccel_device 1")]
    [InlineData("d3d11va", "0", "-hwaccel d3d11va -hwaccel_device 0")]
    [InlineData("videotoolbox", "", "-hwaccel videotoolbox")]
    public void HardwareDecode_EmitsAcceleratorBeforeTheInput(string kind, string device, string expected)
    {
        var profile = new EncodingProfile { HardwareDecode = kind, HardwareDevice = device };
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, profile);

        var before = string.Join(' ', args.Take(InputIndex(args)));
        Assert.Contains(expected, before, StringComparison.Ordinal);
        // Frames come back to system memory: no hardware output format, software filters unchanged.
        Assert.DoesNotContain("-hwaccel_output_format", args);
        Assert.Contains("-vf", args);
    }

    [Fact]
    public void HardwareDecode_ComesBeforeExtraInputArgs()
    {
        var profile = new EncodingProfile { HardwareDecode = "cuda", ExtraInputArgs = "-threads 2" };
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, profile);
        Assert.True(args.IndexOf("-hwaccel") < args.IndexOf("-threads"));
        Assert.True(args.IndexOf("-threads") < InputIndex(args));
    }

    [Fact]
    public void SlateNeverDecodes_SoNoHwaccel()
    {
        var profile = new EncodingProfile { HardwareDecode = "qsv" };
        var args = FfmpegArgsBuilder.ForSlate(TimeSpan.FromSeconds(10), TimeSpan.Zero, profile, "#000000", null, null);
        Assert.DoesNotContain("-hwaccel", args);
    }

    [Fact]
    public void VaapiEncoder_GetsDeviceAndUploadsFrames()
    {
        var profile = new EncodingProfile { VideoEncoder = "h264_vaapi", HardwareDecode = string.Empty, HardwareDevice = "/dev/dri/renderD129" };
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, profile);

        Assert.Equal("/dev/dri/renderD129", ValueAfter(args, "-vaapi_device"));
        Assert.True(args.IndexOf("-vaapi_device") < InputIndex(args));
        Assert.EndsWith("format=yuv420p," + SdrTags + ",format=nv12,hwupload", ValueAfter(args, "-vf"), StringComparison.Ordinal);
        Assert.DoesNotContain("-pix_fmt", args);
        Assert.DoesNotContain("-preset", args);
    }

    [Fact]
    public void VaapiEncoder_WithVaapiDecode_DeclaresTheDeviceOnce()
    {
        var profile = new EncodingProfile { VideoEncoder = "h264_vaapi", HardwareDecode = "vaapi" };
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, profile);

        Assert.Single(args, a => a == "-vaapi_device");
        Assert.Equal(FfmpegArgsBuilder.DefaultDrmDevice, ValueAfter(args, "-vaapi_device"));
        Assert.Contains("-hwaccel", args);
    }

    [Fact]
    public void VaapiEncoder_WithOverlay_UploadsTheComposedFrame()
    {
        var profile = new EncodingProfile { VideoEncoder = "h264_vaapi", HardwareDecode = string.Empty };
        var overlay = new FfmpegArgsBuilder.LogoOverlayInput("/data/logo.png", LogoCorner.TopRight, 70, 8);
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, profile, overlay);

        var graph = ValueAfter(args, "-filter_complex");
        Assert.EndsWith("overlay=W-w-20:20:format=yuv420[ov];[ov]format=nv12,hwupload[v]", graph, StringComparison.Ordinal);
        Assert.Equal("[v]", ValueAfter(args, "-map"));
        Assert.DoesNotContain("-pix_fmt", args);
    }

    [Fact]
    public void VaapiEncoder_Slate_UploadsFrames()
    {
        var profile = new EncodingProfile { VideoEncoder = "h264_vaapi", HardwareDecode = string.Empty };

        var plain = FfmpegArgsBuilder.ForSlate(TimeSpan.FromSeconds(10), TimeSpan.Zero, profile, "#000000", null, null);
        Assert.Equal("format=yuv420p," + SdrTags + ",format=nv12,hwupload", ValueAfter(plain, "-vf"));
        Assert.Contains("-vaapi_device", plain);

        var text = FfmpegArgsBuilder.ForSlate(TimeSpan.FromSeconds(10), TimeSpan.Zero, profile, "#000000", "Back soon", null);
        var graph = ValueAfter(text, "-filter_complex");
        Assert.EndsWith("[v1];[v1]format=yuv420p," + SdrTags + ",format=nv12,hwupload[v2]", graph, StringComparison.Ordinal);
        Assert.Equal("[v2]", ValueAfter(text, "-map"));
        Assert.DoesNotContain("-pix_fmt", text);
    }

    [Fact]
    public void SoftwareEncoder_Slate_TagsTheTextCard()
    {
        var args = FfmpegArgsBuilder.ForSlate(TimeSpan.FromSeconds(10), TimeSpan.Zero, Profile, "#000000", "Back soon", null);
        var graph = ValueAfter(args, "-filter_complex");
        Assert.Contains("drawtext=text='Back soon'", graph, StringComparison.Ordinal);
        Assert.EndsWith("[v1];[v1]format=yuv420p," + SdrTags + "[v2]", graph, StringComparison.Ordinal);
        Assert.Equal("[v2]", ValueAfter(args, "-map"));
        Assert.Equal("yuv420p", ValueAfter(args, "-pix_fmt"));
        Assert.DoesNotContain("-vaapi_device", args);
        Assert.DoesNotContain("hwupload", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiPartItem_SegmentCarriesHdrTransfer()
    {
        var item = Item(hdr: true, transfer: "arib-std-b67");
        Assert.True(item.SegmentAt(TimeSpan.Zero).Part.IsHdr);
        Assert.Equal("arib-std-b67", item.SegmentAt(TimeSpan.Zero).Part.HdrTransfer);
        Assert.False(Item().SegmentAt(TimeSpan.Zero).Part.IsHdr);
    }
}
