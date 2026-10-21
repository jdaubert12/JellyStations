using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Streaming;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Streaming;

public class FfmpegProbeArgsTests
{
    private static string ValueAfter(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, flag + " not found");
        return args[i + 1];
    }

    [Fact]
    public void Probe_IsVerboseFastAndSilent()
    {
        var p = new EncodingProfile { HardwareDecode = "qsv", HardwareDevice = "/dev/dri/renderD128" };
        var args = FfmpegArgsBuilder.ForProbe("/media/film.mkv", null, p, 5, TimeSpan.FromSeconds(60), "hevc");

        Assert.Equal("verbose", ValueAfter(args, "-loglevel"));
        var input = args.Take(args.IndexOf("-i")).ToList();
        Assert.Equal("hevc_qsv", ValueAfter(input, "-c:v"));
        Assert.Equal("/dev/dri/renderD128", ValueAfter(input, "-qsv_device"));
        Assert.DoesNotContain("-hwaccel", args);
        Assert.Equal("60.000", ValueAfter(args, "-ss"));
        Assert.Equal("5", ValueAfter(args, "-t"));
        Assert.Contains("-an", args);
        Assert.Equal("libx264", ValueAfter(args.Skip(args.IndexOf("-i")).ToList(), "-c:v"));
        Assert.Equal("null", ValueAfter(args, "-f"));
        Assert.Equal("-", args[^1]);
        Assert.DoesNotContain("mpegts", args);
        Assert.DoesNotContain("-output_ts_offset", args);
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void Probe_UsesTheSameVideoChainAsAProgram()
    {
        var p = new EncodingProfile { HardwareDecode = string.Empty };
        var probe = FfmpegArgsBuilder.ForProbe("/media/film.mkv", "smpte2084", p, 5, TimeSpan.Zero);
        var program = FfmpegArgsBuilder.ForSource("/media/film.mkv", true, 0, "smpte2084", TimeSpan.Zero, null, TimeSpan.Zero, p);

        Assert.Equal(ValueAfter(program, "-vf"), ValueAfter(probe, "-vf"));
        Assert.Contains("tonemap=", ValueAfter(probe, "-vf"), StringComparison.Ordinal);
        Assert.Equal(ValueAfter(program, "-b:v"), ValueAfter(probe, "-b:v"));
        Assert.Equal(ValueAfter(program, "-preset"), ValueAfter(probe, "-preset"));
    }

    [Fact]
    public void Probe_VaapiEncoder_UploadsFrames()
    {
        var p = new EncodingProfile { HardwareDecode = "vaapi", VideoEncoder = "h264_vaapi" };
        var args = FfmpegArgsBuilder.ForProbe("/media/film.mkv", null, p, 3, TimeSpan.Zero);
        Assert.EndsWith(",format=nv12,hwupload", ValueAfter(args, "-vf"), StringComparison.Ordinal);
        Assert.Equal(FfmpegArgsBuilder.DefaultDrmDevice, ValueAfter(args, "-vaapi_device"));
    }
}
