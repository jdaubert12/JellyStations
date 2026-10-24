using Jellyfin.Plugin.JellyStations.Streaming;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

public class StreamHealthTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static EncoderSnapshot Running(Action<EncoderSnapshotBuilder>? configure = null)
    {
        var b = new EncoderSnapshotBuilder();
        configure?.Invoke(b);
        return b.Build();
    }

    [Fact]
    public void SoftwareProfile_IsHealthyAndSaysSoftware()
    {
        var v = StreamHealth.Assess(Running(b => b.Requested = string.Empty), Now);
        Assert.Equal(StreamHealth.DecoderSoftware, v.Decoder);
        Assert.Equal("ok", v.State);
        Assert.Empty(v.Problems);
    }

    [Fact]
    public void OpenGpuDevice_ConfirmsHardwareDecoding()
    {
        var v = StreamHealth.Assess(Running(b => b.GpuDevice = "/dev/dri/renderD128"), Now);
        Assert.Equal(StreamHealth.DecoderHardware, v.Decoder);
        Assert.Equal("ok", v.State);
    }

    [Fact]
    public void FallbackLine_IsBad()
    {
        var v = StreamHealth.Assess(Running(b => b.Fallback = "[hevc @ 0x1] Failed setup for format qsv: hwaccel initialisation returned error."), Now);
        Assert.Equal(StreamHealth.DecoderFallback, v.Decoder);
        Assert.Equal("bad", v.State);
        Assert.Contains(v.Problems, p => p.Contains("decoding in software", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionLevelFailure_IsDisabled()
    {
        var v = StreamHealth.Assess(Running(b => { b.Disabled = true; b.Active = string.Empty; }), Now);
        Assert.Equal(StreamHealth.DecoderDisabled, v.Decoder);
        Assert.Equal("bad", v.State);
    }

    [Fact]
    public void LinuxWithoutDevice_WarnsThatDecoderIsNotInUse()
    {
        var v = StreamHealth.Assess(Running(b => b.CanInspect = true), Now);
        Assert.Equal(StreamHealth.DecoderUnverified, v.Decoder);
        Assert.Equal("warn", v.State);
    }

    [Fact]
    public void HostThatCannotInspect_IsUnverifiedWithoutComplaint()
    {
        var v = StreamHealth.Assess(Running(b => b.CanInspect = false), Now);
        Assert.Equal(StreamHealth.DecoderUnverified, v.Decoder);
        Assert.Equal("ok", v.State);
    }

    [Fact]
    public void Slate_IsNeverJudgedOnDecoding()
    {
        var v = StreamHealth.Assess(Running(b => { b.Slate = true; b.CanInspect = true; }), Now);
        Assert.Equal(StreamHealth.DecoderSoftware, v.Decoder);
        Assert.Equal("ok", v.State);
    }

    [Fact]
    public void BehindRealTime_IsBad_AfterWarmUp()
    {
        var v = StreamHealth.Assess(Running(b => { b.GpuDevice = "/dev/dri/renderD128"; b.Ahead = -0.7; b.StartedSecondsAgo = 30; }), Now);
        Assert.Equal("bad", v.State);
        Assert.Contains(v.Problems, p => p.Contains("0.7 s behind", StringComparison.Ordinal));
    }

    [Fact]
    public void SmallMarginDuringWarmUp_IsNormal()
    {
        var v = StreamHealth.Assess(Running(b => { b.GpuDevice = "/dev/dri/renderD128"; b.Ahead = 0.1; b.StartedSecondsAgo = 2; }), Now);
        Assert.Equal("ok", v.State);
    }

    [Fact]
    public void TightMargin_Warns()
    {
        var v = StreamHealth.Assess(Running(b => { b.GpuDevice = "/dev/dri/renderD128"; b.Ahead = 0.2; b.StartedSecondsAgo = 30; }), Now);
        Assert.Equal("warn", v.State);
    }

    [Fact]
    public void EarlierDip_WarnsEvenWhenCaughtUp()
    {
        var v = StreamHealth.Assess(Running(b => { b.GpuDevice = "/dev/dri/renderD128"; b.Ahead = 1.5; b.MinAhead = -2.0; b.StartedSecondsAgo = 300; }), Now);
        Assert.Equal("warn", v.State);
        Assert.Contains(v.Problems, p => p.Contains("earlier in this program", StringComparison.Ordinal));
    }

    [Fact]
    public void Stall_IsBad()
    {
        var v = StreamHealth.Assess(Running(b => { b.GpuDevice = "/dev/dri/renderD128"; b.SinceOutput = 12; }), Now);
        Assert.Equal("bad", v.State);
        Assert.Contains(v.Problems, p => p.StartsWith("No output from ffmpeg", StringComparison.Ordinal));
    }

    [Fact]
    public void HeavyCpuWithoutHardware_PointsAtSoftwareDecoding()
    {
        var v = StreamHealth.Assess(Running(b => { b.Requested = string.Empty; b.Cpu = 4.8; }), Now);
        Assert.Equal("warn", v.State);
        Assert.Contains(v.Problems, p => p.Contains("decoded in software", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("libx264", false)]
    [InlineData("h264_qsv", true)]
    [InlineData("h264_vaapi", true)]
    [InlineData("h264_nvenc", true)]
    [InlineData("h264_videotoolbox", true)]
    public void HardwareEncoders_AreRecognizedBySuffix(string encoder, bool hardware)
    {
        Assert.Equal(hardware, EncoderSnapshot.IsHardwareEncoder(encoder));
    }

    private sealed class EncoderSnapshotBuilder
    {
        public string Requested = "qsv";
        public string? Active;
        public bool Disabled;
        public string? Fallback;
        public string? GpuDevice;
        public bool CanInspect = true;
        public bool Slate;
        public double? Ahead = 1.5;
        public double? MinAhead;
        public double? SinceOutput = 0.2;
        public double? Cpu = 0.8;
        public double StartedSecondsAgo = 60;

        public EncoderSnapshot Build() => new()
        {
            ProcessRunning = true,
            Title = "Show",
            IsSlate = Slate,
            SegmentStartedUtc = Now.AddSeconds(-StartedSecondsAgo),
            RequestedDecoder = Requested,
            ActiveDecoder = Active ?? Requested,
            DecoderDisabled = Disabled,
            DecoderFallback = Fallback,
            GpuDevice = GpuDevice,
            CanInspectDevices = CanInspect,
            VideoEncoder = "libx264",
            CpuCores = Cpu,
            AheadSeconds = Ahead,
            MinAheadSeconds = MinAhead ?? Ahead,
            SecondsSinceOutput = SinceOutput
        };
    }
}
