using Jellyfin.Plugin.TvStation.Streaming;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Streaming;

public class EncoderTelemetryTests
{
    [Theory]
    [InlineData("[hevc @ 0x55d] Failed setup for format qsv: hwaccel initialisation returned error.")]
    [InlineData("Device creation failed: -542398533.")]
    [InlineData("[AVHWDeviceContext @ 0x1] Failed to initialise VAAPI connection: -1 (unknown libva error).")]
    [InlineData("[AVHWDeviceContext @ 0x1] Error creating a QSV device")]
    [InlineData("No device available for decoder: device type qsv needed for codec hevc.")]
    [InlineData("Cannot load libcuda.so.1")]
    public void FallbackLines_AreRecognized(string line)
    {
        Assert.True(FfmpegStderr.IsHardwareDecodeFallback(line));
    }

    [Theory]
    [InlineData("[mpegts @ 0x1] frame size not set")]
    [InlineData("Past duration 0.999992 too large")]
    [InlineData("[libx264 @ 0x2] using SAR=1/1")]
    [InlineData("Using auto hwaccel type qsv with new default device.")]
    public void OrdinaryWarnings_AreNotFallbacks(string line)
    {
        Assert.False(FfmpegStderr.IsHardwareDecodeFallback(line));
    }

    [Theory]
    [InlineData("[hevc @ 0x1] Using auto hwaccel type qsv with new default device.", "qsv")]
    [InlineData("Using auto hwaccel type vaapi with new default device.", "vaapi")]
    [InlineData("[h264 @ 0x3] Using auto hwaccel type cuda with existing device cuda0.", "cuda")]
    [InlineData("[hevc @ 0x1] Stream #0:0 -> #0:0 (hevc (native) -> h264 (libx264))", null)]
    [InlineData("  Stream #0:0 -> #0:0 (hevc (hevc_qsv) -> h264 (h264_qsv))", "qsv")]
    [InlineData("  Stream #0:0 -> #0:0 (h264 (h264_qsv) -> h264 (libx264))", "qsv")]
    [InlineData("  Stream #0:0 -> #0:0 (h264 (h264_cuvid) -> h264 (h264_nvenc))", "cuda")]
    [InlineData("  Stream #0:0 -> #0:0 (h264 (native) -> h264 (h264_qsv))", null)]
    public void HardwareDecodeInUse_NamesTheKind(string line, string? expected)
    {
        Assert.Equal(expected, FfmpegStderr.HardwareDecodeInUse(line));
    }

    [Fact]
    public void Observe_CountsWarningsAndKeepsTheFirstFallback()
    {
        var t = new SegmentTelemetry("Show", isSlate: false, "qsv", "libx264", toneMapping: false, sourceIsHdr: false, overlay: false);
        t.Observe("Past duration 0.9 too large");
        t.Observe("[hevc @ 0x1] Failed setup for format qsv: hwaccel initialisation returned error.");
        t.Observe("Device creation failed: -1.");
        Assert.Equal(3, t.Warnings);
        Assert.Equal("Device creation failed: -1.", t.LastWarning);
        Assert.StartsWith("[hevc @ 0x1] Failed setup", t.DecoderFallback, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_IgnoresFallbackLinesWhenNoDecoderWasRequested()
    {
        var t = new SegmentTelemetry("Show", isSlate: false, string.Empty, "libx264", toneMapping: false, sourceIsHdr: false, overlay: false);
        t.Observe("Device creation failed: -1.");
        Assert.Null(t.DecoderFallback);
        Assert.Equal(1, t.Warnings);
    }

    [Theory]
    [InlineData("/dev/dri/renderD128", true)]
    [InlineData("/dev/dri/card0", true)]
    [InlineData("/dev/nvidia0", true)]
    [InlineData("/dev/nvidiactl", true)]
    [InlineData("/media/show.mkv", false)]
    [InlineData("pipe:[12345]", false)]
    [InlineData(null, false)]
    public void GpuDevicePaths_AreRecognized(string? path, bool expected)
    {
        Assert.Equal(expected, ProcessInspector.IsGpuDevice(path));
    }
}
