using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Streaming;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

public class EncoderPresetTests
{
    private static PlayableItem Item() => new()
    {
        ItemId = Guid.NewGuid(),
        Path = "/media/show.mkv",
        RuntimeTicks = TimeSpan.FromMinutes(22).Ticks,
        Title = "Show",
        HasAudio = true
    };

    private static List<string> Args(string encoder, string preset, string range = EncodingProfile.OutputSdr)
        => FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, new EncodingProfile { HardwareDecode = string.Empty, VideoEncoder = encoder, Preset = preset, OutputRange = range });

    private static string? PresetOf(List<string> args)
    {
        var i = args.IndexOf("-preset");
        return i < 0 ? null : args[i + 1];
    }

    [Theory]
    [InlineData("libx264", "veryfast", "veryfast")]
    [InlineData("libx264", "Medium", "Medium")]
    [InlineData("libx265", "ultrafast", "ultrafast")]
    public void SoftwareEncoders_GetThePresetAsTyped(string encoder, string preset, string expected)
    {
        Assert.Equal(expected, PresetOf(Args(encoder, preset)));
    }

    [Theory]
    [InlineData("hevc_qsv", "veryfast", "veryfast")]
    [InlineData("h264_qsv", "VeryFast", "veryfast")]
    [InlineData("hevc_qsv", "ultrafast", "veryfast")]
    [InlineData("hevc_qsv", "superfast", "veryfast")]
    [InlineData("hevc_qsv", "placebo", "veryslow")]
    [InlineData("hevc_qsv", "medium", "medium")]
    [InlineData("hevc_qsv", "7", "7")]
    public void Qsv_GetsThePresetInItsOwnVocabulary(string encoder, string preset, string expected)
    {
        // The whole point: hevc_qsv at its default (medium) ran an HDR10 chain at 2.0x on an i5-8500T, veryfast at 3.5x.
        Assert.Equal(expected, PresetOf(Args(encoder, preset, EncodingProfile.OutputHdr10)));
    }

    [Theory]
    [InlineData("hevc_nvenc", "p4", "p4")]
    [InlineData("h264_nvenc", "P5", "p5")]
    [InlineData("h264_nvenc", "ultrafast", "p1")]
    [InlineData("h264_nvenc", "veryfast", "p2")]
    [InlineData("h264_nvenc", "faster", "p3")]
    [InlineData("h264_nvenc", "fast", "fast")]
    [InlineData("h264_nvenc", "medium", "medium")]
    [InlineData("h264_nvenc", "slow", "slow")]
    [InlineData("h264_nvenc", "slower", "p6")]
    [InlineData("h264_nvenc", "veryslow", "p7")]
    [InlineData("h264_nvenc", "llhq", "llhq")]
    public void Nvenc_GetsThePresetInItsOwnVocabulary(string encoder, string preset, string expected)
    {
        Assert.Equal(expected, PresetOf(Args(encoder, preset)));
    }

    [Theory]
    [InlineData("h264_vaapi")]
    [InlineData("hevc_vaapi")]
    [InlineData("h264_amf")]
    [InlineData("hevc_videotoolbox")]
    public void EncodersWithoutAPresetOption_GetNone(string encoder)
    {
        Assert.Null(PresetOf(Args(encoder, "veryfast")));
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("hevc_qsv")]
    [InlineData("h264_nvenc")]
    public void BlankPreset_LeavesTheEncoderDefault(string encoder)
    {
        Assert.Null(PresetOf(Args(encoder, " ")));
    }

    [Fact]
    public void UnknownWord_IsPassedThroughForFfmpegToJudge()
    {
        Assert.Equal("turbo", PresetOf(Args("hevc_qsv", "turbo")));
        Assert.Equal("turbo", PresetOf(Args("h264_nvenc", "turbo")));
    }

    [Fact]
    public void ThePresetComesRightAfterTheEncoder()
    {
        var args = Args("hevc_qsv", "veryfast", EncodingProfile.OutputHdr10);
        var i = args.IndexOf("-c:v");
        Assert.Equal(new[] { "-c:v", "hevc_qsv", "-preset", "veryfast", "-profile:v", "main10" }, args.Skip(i).Take(6));
    }
}
