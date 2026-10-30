using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Streaming;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

public class HardwareProbeTests
{
    private static readonly string[] HwaccelsOutput =
    {
        "Hardware acceleration methods:",
        "vdpau",
        "cuda",
        "vaapi",
        "qsv",
        "drm",
        "opencl",
        "vulkan",
        string.Empty
    };

    private static readonly string[] EncodersOutput =
    {
        "Encoders:",
        " V..... = Video",
        " ------",
        " V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)",
        " V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)",
        " V....D h264_qsv             H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (Intel Quick Sync Video acceleration) (codec h264)",
        " V....D h264_vaapi           H.264/AVC (VAAPI) (codec h264)",
        " V....D hevc_qsv             HEVC (Intel Quick Sync Video acceleration) (codec hevc)",
        " A....D aac                  AAC (Advanced Audio Coding)"
    };

    private static readonly EncodingProfile Qsv = new() { HardwareDecode = "qsv", HardwareDevice = "/dev/dri/renderD128" };

    private static readonly string[] Args = { "ffmpeg", "-i", "x" };

    [Fact]
    public void ParseHwaccels_TakesTheNamesAfterTheHeader()
    {
        Assert.Equal(new[] { "vdpau", "cuda", "vaapi", "qsv", "drm", "opencl", "vulkan" }, HardwareProbe.ParseHwaccels(HwaccelsOutput));
    }

    [Fact]
    public void ParseH264Encoders_KeepsVideoH264EncodersOnly()
    {
        Assert.Equal(new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_vaapi" }, HardwareProbe.ParseH264Encoders(EncodersOutput));
    }

    [Fact]
    public void Interpret_HardwareDecodeConfirmedBySelfReport()
    {
        var log = new[]
        {
            "Input #0, matroska,webm, from '/media/film.mkv':",
            "  Stream #0:0(eng): Video: hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/smpte2084, progressive), 3840x2160 [SAR 1:1 DAR 16:9], 23.98 fps",
            "[hevc @ 0x55] Using auto hwaccel type qsv with new default device.",
            "Stream mapping:",
            "frame=  120 fps= 60 q=-0.0 Lsize=N/A time=00:00:05.00 bitrate=N/A speed=2.51x"
        };
        var r = HardwareProbe.Interpret("qsv", Qsv, "/media/film.mkv", Args, log, 0, 2.0);
        Assert.Equal("hardware", r.Decoder);
        Assert.Equal("qsv", r.DecoderInUse);
        Assert.Equal("ok", r.Encoder);
        Assert.Equal(2.51, r.Speed);
        Assert.StartsWith("hevc (Main 10), yuv420p10le", r.SourceVideo, StringComparison.Ordinal);
        Assert.Empty(r.Problems);
        Assert.True(r.Healthy);
    }

    [Fact]
    public void Interpret_FallbackLine_WinsOverSelfReport()
    {
        var log = new[]
        {
            "[hevc @ 0x55] Using auto hwaccel type qsv with new default device.",
            "[hevc @ 0x55] Failed setup for format qsv: hwaccel initialisation returned error.",
            "frame=  120 fps= 12 q=-0.0 Lsize=N/A time=00:00:05.00 bitrate=N/A speed=0.48x"
        };
        var r = HardwareProbe.Interpret("qsv", Qsv, "/media/film.mkv", Args, log, 0, 10.0);
        Assert.Equal("fallback", r.Decoder);
        Assert.Contains(r.Problems, p => p.Contains("fell back to software", StringComparison.Ordinal));
        Assert.Contains(r.Problems, p => p.Contains("0.48x real time", StringComparison.Ordinal));
    }

    [Fact]
    public void WithDecoderSkipped_KeepsTheRun_DropsTheDecoderReport()
    {
        var log = new[]
        {
            "[hevc @ 0x55] Using auto hwaccel type qsv with new default device.",
            "[hevc @ 0x55] Failed setup for format qsv: hwaccel initialisation returned error.",
            "frame=  120 fps= 12 q=-0.0 Lsize=N/A time=00:00:05.00 bitrate=N/A speed=0.48x"
        };
        var r = HardwareProbe.Interpret("qsv", Qsv, "/media/film.mkv", Args, log, 0, 10.0);
        var skipped = r.WithDecoderSkipped("qsv", "10-bit H.264 is beyond what GPU decoders handle");

        Assert.Equal("skipped", skipped.Decoder);
        Assert.Equal("10-bit H.264 is beyond what GPU decoders handle", skipped.DecoderSkipReason);
        Assert.Null(skipped.DecoderInUse);
        Assert.Null(skipped.DecoderFallback);
        Assert.Equal(r.Speed, skipped.Speed);
        Assert.Equal(r.Command, skipped.Command);
        Assert.Same(r.Problems, skipped.Problems);
        Assert.Same(r.Log, skipped.Log);
    }

    [Fact]
    public void Interpret_QsvDecoderInTheStreamMapping_IsHardware()
    {
        // The explicit *_qsv decoder never says "hwaccel"; the stream mapping names it.
        var log = new[]
        {
            "  Stream #0:0: Video: hevc (Main), 1 reference frame, yuv420p(tv, smpte170m, left), 720x476 [SAR 32:27 DAR 640:357], 29.97 fps",
            "Stream mapping:",
            "  Stream #0:0 -> #0:0 (hevc (hevc_qsv) -> h264 (h264_qsv))",
            "[hevc_qsv @ 0x70] Decoder: output is system memory surface",
            "frame=  150 fps=120 q=-0.0 Lsize=N/A time=00:00:05.00 bitrate=N/A speed=4.1x"
        };
        var r = HardwareProbe.Interpret("qsv", new EncodingProfile { HardwareDecode = "qsv", VideoEncoder = "h264_qsv" }, "/media/film.mkv", Args, log, 0, 1.3);
        Assert.Equal("hardware", r.Decoder);
        Assert.Equal("qsv", r.DecoderInUse);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Interpret_GpuNodeOpenWithSoftwareEncoder_IsHardware()
    {
        // An explicit hwaccel on a native decoder prints nothing on success; the open device node is the evidence.
        var log = new[] { "  Stream #0:0 -> #0:0 (hevc (native) -> h264 (libx264))", "frame=  120 fps= 50 q=-0.0 Lsize=N/A time=00:00:05.00 bitrate=N/A speed=2.2x" };
        var r = HardwareProbe.Interpret("vaapi", new EncodingProfile { HardwareDecode = "vaapi" }, "/media/film.mkv", Args, log, 0, 4.0, "/dev/dri/renderD128", canInspect: true);
        Assert.Equal("hardware", r.Decoder);
        Assert.Contains("/dev/dri/renderD128", r.DecoderInUse, StringComparison.Ordinal);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Interpret_NoGpuNodeWithSoftwareEncoder_IsSoftware()
    {
        var log = new[] { "Stream mapping:", "frame=  120 fps= 50 q=-0.0 Lsize=N/A time=00:00:05.00 bitrate=N/A speed=1.2x" };
        var r = HardwareProbe.Interpret("vaapi", new EncodingProfile { HardwareDecode = "vaapi" }, "/media/film.mkv", Args, log, 0, 4.0, null, canInspect: true);
        Assert.Equal("software", r.Decoder);
        Assert.Contains(r.Problems, p => p.Contains("held no GPU device open", StringComparison.Ordinal));
        Assert.Contains(r.Problems, p => p.Contains("little headroom", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("libx264", false)]
    [InlineData("h264_vaapi", true)]
    public void Interpret_NoEvidenceEitherWay_IsUnverified(string encoder, bool canInspect)
    {
        // Windows cannot look at device nodes; a hardware encoder holds the node whether or not the decoder does.
        var log = new[] { "Stream mapping:", "frame=  120 fps= 50 q=-0.0 Lsize=N/A time=00:00:05.00 bitrate=N/A speed=3.2x" };
        var r = HardwareProbe.Interpret("vaapi", new EncodingProfile { HardwareDecode = "vaapi", VideoEncoder = encoder }, "/media/film.mkv", Args, log, 0, 4.0, canInspect ? "/dev/dri/renderD128" : null, canInspect);
        Assert.Equal("unverified", r.Decoder);
        Assert.Null(r.DecoderInUse);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Interpret_NonZeroExitWithoutDecoder_IsFailed()
    {
        var log = new[] { "[AVHWDeviceContext @ 0x1] Failed to initialise VAAPI connection: -1 (unknown libva error).", "Device creation failed: -5.", "Error parsing global options: Input/output error" };
        var r = HardwareProbe.Interpret("vaapi", new EncodingProfile { HardwareDecode = "vaapi" }, "/media/film.mkv", Args, log, 1, 0.2);
        Assert.Equal("failed", r.Decoder);
        Assert.Equal("failed", r.Encoder);
        Assert.Single(r.Problems);
        Assert.Contains("Failed to initialise VAAPI connection", r.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Interpret_SoftwareProfile_ReportsNotRequested()
    {
        var log = new[] { "frame=  120 fps= 90 q=-0.0 Lsize=N/A time=00:00:05.00 bitrate=N/A speed=3.7x" };
        var r = HardwareProbe.Interpret(string.Empty, new EncodingProfile { HardwareDecode = string.Empty }, "/media/film.mkv", Args, log, 0, 1.5);
        Assert.Equal("not-requested", r.Decoder);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Interpret_EncoderFailure_IsReportedSeparately()
    {
        var log = new[] { "[hevc @ 0x55] Using auto hwaccel type qsv with new default device.", "[h264_qsv @ 0x2] Error initializing an internal MFX session: unsupported (-3)", "Error while opening encoder for output stream #0:0" };
        var r = HardwareProbe.Interpret("qsv", new EncodingProfile { HardwareDecode = "qsv", VideoEncoder = "h264_qsv" }, "/media/film.mkv", Args, log, 1, 0.5);
        Assert.Equal("failed", r.Encoder);
        Assert.Contains(r.Problems, p => p.StartsWith("Encoding with h264_qsv failed", StringComparison.Ordinal));
    }
}
