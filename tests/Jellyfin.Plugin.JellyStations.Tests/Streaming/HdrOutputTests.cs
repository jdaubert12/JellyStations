using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Streaming;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

/// <summary>An HDR10 profile: every graph leaves as 10-bit BT.2020 PQ and the channel is declared as HEVC Main 10.</summary>
public class HdrOutputTests
{
    private const string Scale = "scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic";
    private const string PadFps = "pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=30";
    private const string HdrTags = "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv";
    private const string SdrToPq = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,format=yuv420p10le,zscale=t=smpte2084:p=bt2020:m=bt2020nc:r=tv:npl=100";
    private const string StillToPq = "format=gbrap10le,zscale=tin=iec61966-2-1:pin=bt709:t=smpte2084:p=bt2020:m=bt2020nc:r=tv:npl=100,format=yuva444p10le";

    private static EncodingProfile Hdr(string encoder = "libx265") => new() { HardwareDecode = string.Empty, VideoEncoder = encoder, OutputRange = EncodingProfile.OutputHdr10 };

    private static PlayableItem Item(string? transfer) => new()
    {
        ItemId = Guid.NewGuid(),
        Path = "/media/film.mkv",
        RuntimeTicks = TimeSpan.FromMinutes(90).Ticks,
        Title = "Film",
        HasAudio = true,
        HdrTransfer = transfer
    };

    private static string ValueAfter(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, flag + " not found");
        return args[i + 1];
    }

    private static List<string> Program(EncodingProfile p, string? transfer, FfmpegArgsBuilder.LogoOverlayInput? logo = null, FfmpegArgsBuilder.UpNextCardInput? card = null)
        => FfmpegArgsBuilder.ForFile(Item(transfer), TimeSpan.Zero, null, TimeSpan.Zero, p, logo, card);

    [Fact]
    public void SdrSource_IsConvertedIntoPq_NotToneMapped()
    {
        var vf = ValueAfter(Program(Hdr(), null), "-vf");

        Assert.Equal(Scale + "," + SdrToPq + "," + PadFps + ",format=yuv420p10le," + HdrTags, vf);
        Assert.DoesNotContain("tonemap=", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void PqSource_KeepsItsRangeAndMasteringMetadata()
    {
        var vf = ValueAfter(Program(Hdr(), "smpte2084"), "-vf");

        Assert.Equal(Scale + ",setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,format=yuv420p10le," + PadFps + ",format=yuv420p10le," + HdrTags, vf);
        Assert.DoesNotContain("zscale", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("sidedata=mode=delete", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void HlgSource_IsConvertedToPq()
    {
        var vf = ValueAfter(Program(Hdr(), "arib-std-b67"), "-vf");

        Assert.Contains(",setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc,format=yuv420p10le,zscale=t=smpte2084:p=bt2020:m=bt2020nc:r=tv," + PadFps, vf, StringComparison.Ordinal);
        Assert.EndsWith(",format=yuv420p10le," + HdrTags, vf, StringComparison.Ordinal);
    }

    [Fact]
    public void Encoder_IsMain10_WithHdrColourOptions()
    {
        var args = Program(Hdr(), null);
        var output = args.Skip(args.IndexOf("-i")).ToList();

        Assert.Equal("libx265", ValueAfter(output, "-c:v"));
        Assert.Equal("main10", ValueAfter(output, "-profile:v"));
        Assert.Equal("veryfast", ValueAfter(output, "-preset"));
        Assert.Contains("repeat-headers=1", ValueAfter(output, "-x265-params"), StringComparison.Ordinal);
        Assert.Contains("scenecut=0:open-gop=0", ValueAfter(output, "-x265-params"), StringComparison.Ordinal);
        Assert.Equal("bt2020", ValueAfter(output, "-color_primaries"));
        Assert.Equal("smpte2084", ValueAfter(output, "-color_trc"));
        Assert.Equal("bt2020nc", ValueAfter(output, "-colorspace"));
        Assert.Equal("tv", ValueAfter(output, "-color_range"));
        Assert.DoesNotContain("-x264-params", output);
        Assert.DoesNotContain("-level", output);
        Assert.Contains("-force_key_frames", output); // libx265's forced key frames are IDR frames
    }

    [Theory]
    [InlineData("hevc_qsv", "p010le")]
    [InlineData("hevc_nvenc", "p010le")]
    [InlineData("libx265", "yuv420p10le")]
    public void Encoder_GetsTheTenBitLayoutItTakes(string encoder, string pixelFormat)
    {
        var plain = Program(Hdr(encoder), null);
        Assert.EndsWith(",format=" + pixelFormat + "," + HdrTags, ValueAfter(plain, "-vf"), StringComparison.Ordinal);

        var overlay = Program(Hdr(encoder), null, new FfmpegArgsBuilder.LogoOverlayInput("/l.png", LogoCorner.TopRight, 70, 8));
        Assert.Equal(pixelFormat, ValueAfter(overlay, "-pix_fmt"));
        Assert.Equal("main10", ValueAfter(overlay, "-profile:v"));
    }

    [Fact]
    public void VaapiEncoder_UploadsTenBitFrames()
    {
        var p = new EncodingProfile { HardwareDecode = string.Empty, VideoEncoder = "hevc_vaapi", OutputRange = "hdr10" };
        var args = Program(p, null);

        Assert.EndsWith(",format=yuv420p10le," + HdrTags + ",format=p010le,hwupload", ValueAfter(args, "-vf"), StringComparison.Ordinal);
        Assert.DoesNotContain("-pix_fmt", args);
    }

    [Fact]
    public void LogoAndCard_AreConvertedToPq_BeforeTheOverlay()
    {
        var logo = new FfmpegArgsBuilder.LogoOverlayInput("/l.png", LogoCorner.TopRight, 70, 8);
        var card = new FfmpegArgsBuilder.UpNextCardInput("/c.png", LogoCorner.BottomLeft, 10, 20) { Animation = UpNextCardAnimation.Fade };
        var graph = ValueAfter(Program(Hdr(), "smpte2084", logo, card), "-filter_complex");

        Assert.Contains("colorchannelmixer=aa=0.70," + StillToPq + "[logo];[base][logo]overlay=W-w-20:20:format=yuv420p10[ov1]", graph, StringComparison.Ordinal);
        Assert.Contains("colorchannelmixer@fade=aa=0," + StillToPq + "[card];[ov1][card]overlay=20:H-h-20:format=yuv420p10:enable=", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("format=yuv420[", graph, StringComparison.Ordinal);
        Assert.DoesNotContain(":format=yuv420:", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticCard_IsConvertedToo()
    {
        var card = new FfmpegArgsBuilder.UpNextCardInput("/c.png", LogoCorner.TopLeft, 10, 20);
        var graph = ValueAfter(Program(Hdr(), null, null, card), "-filter_complex");

        Assert.Contains(":v]format=rgba," + StillToPq + "[card]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Slates_EndInPq()
    {
        var text = FfmpegArgsBuilder.ForSlate(TimeSpan.FromSeconds(10), TimeSpan.Zero, Hdr(), "#000000", "Back soon", null);
        var plain = FfmpegArgsBuilder.ForSlate(TimeSpan.FromSeconds(10), TimeSpan.Zero, Hdr("hevc_qsv"), "#000000", null, null);

        Assert.EndsWith("format=yuv420p," + SdrToPq + ",format=yuv420p10le," + HdrTags + "[v2]", ValueAfter(text, "-filter_complex"), StringComparison.Ordinal);
        Assert.Equal("yuv420p10le", ValueAfter(text, "-pix_fmt"));
        Assert.Equal("format=yuv420p," + SdrToPq + ",format=p010le," + HdrTags, ValueAfter(plain, "-vf"));
        Assert.Equal("main10", ValueAfter(plain, "-profile:v"));
    }

    [Fact]
    public void SdrProfile_IsUntouched_EvenWithAnHevcEncoder()
    {
        var p = new EncodingProfile { HardwareDecode = string.Empty, VideoEncoder = "libx265" };
        var args = Program(p, null);
        var output = args.Skip(args.IndexOf("-i")).ToList();

        Assert.EndsWith(",format=yuv420p," + FfmpegArgsBuilder.SdrTags, ValueAfter(args, "-vf"), StringComparison.Ordinal);
        Assert.DoesNotContain("-profile:v", output);
        Assert.Equal("bt709", ValueAfter(output, "-color_trc"));
        Assert.Contains("repeat-headers=1", ValueAfter(output, "-x265-params"), StringComparison.Ordinal);
        Assert.False(p.IsHdrOutput);
        Assert.Equal("hevc", p.OutputCodec);
    }

    [Fact]
    public void Probe_RunsTheHdrChain()
    {
        var probe = FfmpegArgsBuilder.ForProbe("/media/film.mkv", "smpte2084", Hdr(), 5, TimeSpan.Zero);
        var program = FfmpegArgsBuilder.ForSource("/media/film.mkv", true, 0, "smpte2084", TimeSpan.Zero, null, TimeSpan.Zero, Hdr());

        Assert.Equal(ValueAfter(program, "-vf"), ValueAfter(probe, "-vf"));
        Assert.Equal("main10", ValueAfter(probe, "-profile:v"));
    }

    [Fact]
    public void Validation_RequiresAnHevcEncoderForHdr()
    {
        Assert.Contains(StationService.ValidateEncoding(Hdr("libx264")), e => e.Contains("HEVC", StringComparison.Ordinal) && e.Contains("libx264", StringComparison.Ordinal));
        Assert.Contains(StationService.ValidateEncoding(Hdr("h264_qsv")), e => e.Contains("HEVC", StringComparison.Ordinal));
        Assert.Empty(StationService.ValidateEncoding(Hdr("hevc_qsv")));
        Assert.Empty(StationService.ValidateEncoding(Hdr("libx265")));
        Assert.Empty(StationService.ValidateEncoding(new EncodingProfile()));

        var odd = new EncodingProfile { OutputRange = "dolby" };
        Assert.Contains(StationService.ValidateEncoding(odd), e => e.StartsWith("output must be", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("libx264", false, "h264")]
    [InlineData("h264_qsv", false, "h264")]
    [InlineData("libx265", true, "hevc")]
    [InlineData("hevc_qsv", true, "hevc")]
    [InlineData("hevc_nvenc", true, "hevc")]
    [InlineData("HEVC_VAAPI", true, "hevc")]
    public void Profile_KnowsItsCodec(string encoder, bool hevc, string codec)
    {
        var p = new EncodingProfile { VideoEncoder = encoder };
        Assert.Equal(hevc, p.IsHevcEncoder);
        Assert.Equal(codec, p.OutputCodec);
    }

    [Fact]
    public void MediaSource_DeclaresHevcMain10Pq_SoJellyfinSeesHdr10()
    {
        var station = new Station { Id = Guid.NewGuid(), Name = "HDR" };
        var source = StreamSessionManager.BuildMediaSource(station, Hdr("hevc_qsv"));
        var video = source.MediaStreams.Single(s => s.Type == MediaStreamType.Video);

        Assert.Equal("hevc", video.Codec);
        Assert.Equal("Main 10", video.Profile);
        Assert.Equal(10, video.BitDepth);
        Assert.Equal("yuv420p10le", video.PixelFormat);
        Assert.Equal("smpte2084", video.ColorTransfer);
        Assert.Equal("bt2020", video.ColorPrimaries);
        Assert.Equal("HDR", video.VideoRange.ToString());
        Assert.Equal("HDR10", video.VideoRangeType.ToString());
        Assert.Equal(123, video.Level);
        Assert.False(source.SupportsProbing); // Jellyfin would cache the first probe per channel and keep announcing the old codec

        var sdr = StreamSessionManager.BuildMediaSource(station, new EncodingProfile()).MediaStreams.Single(s => s.Type == MediaStreamType.Video);
        Assert.Equal("h264", sdr.Codec);
        Assert.Equal("high", sdr.Profile);
        Assert.Equal(8, sdr.BitDepth);
        Assert.Equal("SDR", sdr.VideoRange.ToString());
        Assert.Equal(41, sdr.Level);

        var hevcSdr = StreamSessionManager.BuildMediaSource(station, new EncodingProfile { VideoEncoder = "libx265" }).MediaStreams.Single(s => s.Type == MediaStreamType.Video);
        Assert.Equal("hevc", hevcSdr.Codec);
        Assert.Equal("Main", hevcSdr.Profile);
        Assert.Equal("bt709", hevcSdr.ColorTransfer);
    }

    [Fact]
    public void ParseHevcEncoders_KeepsHevcOnly()
    {
        var lines = new[]
        {
            "Encoders:",
            " V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)",
            " V....D libx265              libx265 H.265 / HEVC (codec hevc)",
            " V....D h264_qsv             H.264 / AVC (Intel Quick Sync Video acceleration) (codec h264)",
            " V..... hevc_qsv             HEVC (Intel Quick Sync Video acceleration) (codec hevc)",
            " V....D hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)",
            " A....D aac                  AAC (Advanced Audio Coding)"
        };

        Assert.Equal(new[] { "libx265", "hevc_qsv", "hevc_nvenc" }, HardwareProbe.ParseHevcEncoders(lines));
        Assert.Equal(new[] { "libx264", "h264_qsv" }, HardwareProbe.ParseH264Encoders(lines));
    }
}
