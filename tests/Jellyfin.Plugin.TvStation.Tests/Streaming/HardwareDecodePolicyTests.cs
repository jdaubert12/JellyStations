using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Streaming;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Streaming;

public class HardwareDecodePolicyTests
{
    private static readonly HardwareDecodePolicy Qsv = HardwareDecodePolicy.Permissive("qsv");

    [Theory]
    [InlineData("h264", "High", 8, "yuv420p")]
    [InlineData("hevc", "Main", 8, "yuv420p")]
    [InlineData("hevc", "Main 10", 10, "yuv420p10le")]
    [InlineData("mpeg2video", "Main", 8, "yuv420p")]
    [InlineData("vc1", "Advanced", 8, "yuv420p")]
    [InlineData("vp9", "Profile 2", 10, "yuv420p10le")]
    [InlineData("av1", "Main", 10, "yuv420p10le")]
    public void CommonFormats_GoToTheGpu(string codec, string profile, int depth, string format)
    {
        Assert.Null(Qsv.WhyNot(codec, profile, depth, format));
    }

    [Fact]
    public void TenBitH264_StaysInSoftware()
    {
        var why = Qsv.WhyNot("h264", "High 10", 10, "yuv420p10le");
        Assert.NotNull(why);
        Assert.Contains("H.264 High 10 10-bit", why, StringComparison.Ordinal);
        Assert.Contains("8-bit 4:2:0 only", why, StringComparison.Ordinal);
    }

    [Fact]
    public void H264_422_StaysInSoftware()
    {
        Assert.NotNull(Qsv.WhyNot("h264", "High 4:2:2", 8, "yuv422p"));
    }

    [Theory]
    [InlineData("mpeg4")]
    [InlineData("msmpeg4v3")]
    [InlineData("wmv2")]
    [InlineData("theora")]
    public void CodecsNoGpuDecodes_StayInSoftware(string codec)
    {
        var why = Qsv.WhyNot(codec, null, null, "yuv420p");
        Assert.NotNull(why);
        Assert.EndsWith("has no hardware decoder", why, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCodec_IsLeftToFfmpeg()
    {
        Assert.Null(Qsv.WhyNot(null, null, null, null));
        Assert.Null(Qsv.WhyNot(string.Empty, null, null, null));
    }

    [Fact]
    public void SoftwarePolicy_NeverUsesHardware()
    {
        Assert.NotNull(HardwareDecodePolicy.Software.WhyNot("h264", "High", 8, "yuv420p"));
    }

    [Fact]
    public void JellyfinCheckboxes_AreHonored()
    {
        var jellyfin = new HardwareDecodePolicy("qsv", new[] { "h264", "hevc" }, hevc10: false, vp9Ten: false, fromJellyfin: true);
        Assert.Null(jellyfin.WhyNot("h264", "High", 8, "yuv420p"));
        Assert.Contains("not ticked", jellyfin.WhyNot("mpeg2video", "Main", 8, "yuv420p"), StringComparison.Ordinal);
        Assert.Contains("\"10-bit HEVC\"", jellyfin.WhyNot("hevc", "Main 10", 10, "yuv420p10le"), StringComparison.Ordinal);
        Assert.Contains("not ticked", jellyfin.WhyNot("vp9", "Profile 2", 10, "yuv420p10le"), StringComparison.Ordinal);
    }

    [Fact]
    public void HevcRext_NeedsItsOwnToggle()
    {
        Assert.Contains("10-bit HEVC RExt", Qsv.WhyNot("hevc", "Rext", 10, "yuv422p10le"), StringComparison.Ordinal);
        var rext = new HardwareDecodePolicy("qsv", HardwareDecodePolicy.AllCodecs, hevcRext10: true);
        Assert.Null(rext.WhyNot("hevc", "Rext", 10, "yuv422p10le"));
        Assert.Contains("12-bit HEVC RExt", rext.WhyNot("hevc", "Rext", 12, "yuv422p12le"), StringComparison.Ordinal);
    }

    [Fact]
    public void BitDepth_FallsBackToThePixelFormat()
    {
        Assert.NotNull(Qsv.WhyNot("h264", "High 10", null, "yuv420p10le"));
        Assert.Null(Qsv.WhyNot("h264", "High", null, "yuv420p"));
        Assert.Equal(10, HardwareDecodePolicy.DepthFromPixelFormat("yuv420p10le"));
        Assert.Equal(12, HardwareDecodePolicy.DepthFromPixelFormat("yuv422p12le"));
        Assert.Equal(8, HardwareDecodePolicy.DepthFromPixelFormat("yuv420p"));
        Assert.Equal(8, HardwareDecodePolicy.DepthFromPixelFormat("nv12"));
        Assert.Null(HardwareDecodePolicy.DepthFromPixelFormat(null));
        Assert.Equal("422", HardwareDecodePolicy.ChromaFromPixelFormat("yuv422p10le"));
        Assert.Equal("420", HardwareDecodePolicy.ChromaFromPixelFormat("p010le"));
    }

    [Theory]
    [InlineData("AVC", "h264")]
    [InlineData("h265", "hevc")]
    [InlineData("HEVC", "hevc")]
    [InlineData("mpeg2", "mpeg2video")]
    [InlineData("wvc1", "vc1")]
    public void CodecNames_AreNormalized(string input, string expected)
    {
        Assert.Equal(expected, HardwareDecodePolicy.NormalizeCodec(input));
    }

    [Fact]
    public void FollowJellyfin_UsesJellyfinsCheckboxes_WhenTheDecoderMatches()
    {
        var jellyfin = new StreamSessionManager.JellyfinHardware(HardwareAccelerationType.qsv, "/dev/dri/renderD128", null, new[] { "h264" }, true, true, false, false);
        var resolved = HardwareDecodeResolver.Resolve(new EncodingProfile(), jellyfin.Type, jellyfin.QsvDevice, jellyfin.VaapiDevice, false);
        var policy = StreamSessionManager.DecodePolicy(resolved, jellyfin, false);
        Assert.True(policy.FromJellyfin);
        Assert.Null(policy.WhyNot("h264", "High", 8, "yuv420p"));
        Assert.NotNull(policy.WhyNot("hevc", "Main", 8, "yuv420p"));
    }

    [Fact]
    public void HandPickedDecoder_JellyfinIsNotUsing_GetsThePermissiveList()
    {
        var jellyfin = new StreamSessionManager.JellyfinHardware(HardwareAccelerationType.none, null, null, Array.Empty<string>(), true, true, false, false);
        var policy = StreamSessionManager.DecodePolicy(new EncodingProfile { HardwareDecode = "vaapi" }, jellyfin, false);
        Assert.False(policy.FromJellyfin);
        Assert.Null(policy.WhyNot("hevc", "Main 10", 10, "yuv420p10le"));
        Assert.NotNull(policy.WhyNot("h264", "High 10", 10, "yuv420p10le"));
    }

    [Fact]
    public void SoftwareProfile_GetsTheSoftwarePolicy()
    {
        var jellyfin = new StreamSessionManager.JellyfinHardware(HardwareAccelerationType.qsv, null, null, HardwareDecodePolicy.AllCodecs, true, true, false, false);
        Assert.Same(HardwareDecodePolicy.Software, StreamSessionManager.DecodePolicy(new EncodingProfile { HardwareDecode = string.Empty }, jellyfin, false));
    }

    [Fact]
    public void SegmentAt_CarriesTheVideoFacts()
    {
        var item = new PlayableItem { Path = "/m.mkv", RuntimeTicks = TimeSpan.FromMinutes(10).Ticks, VideoCodec = "h264", VideoProfile = "High 10", VideoBitDepth = 10, PixelFormat = "yuv420p10le" };
        var part = item.SegmentAt(TimeSpan.Zero).Part;
        Assert.Equal("h264", part.VideoCodec);
        Assert.Equal("High 10", part.VideoProfile);
        Assert.Equal(10, part.VideoBitDepth);
        Assert.Equal("yuv420p10le", part.PixelFormat);
        Assert.NotNull(Qsv.WhyNot(part));
    }
}
