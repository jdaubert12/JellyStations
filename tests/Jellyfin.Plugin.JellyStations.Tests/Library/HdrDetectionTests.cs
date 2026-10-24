using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class HdrDetectionTests
{
    private static MediaStream Video(string? transfer, string? primaries = null, int? bitDepth = null) => new()
    {
        Type = MediaStreamType.Video,
        Codec = "hevc",
        ColorTransfer = transfer,
        ColorPrimaries = primaries,
        BitDepth = bitDepth
    };

    [Theory]
    [InlineData("smpte2084", "smpte2084")]
    [InlineData("arib-std-b67", "arib-std-b67")]
    [InlineData("SMPTE2084", "smpte2084")]
    public void PqAndHlg_AreHdr(string transfer, string expected)
    {
        Assert.Equal(expected, LibraryContentResolver.HdrTransferOf(Video(transfer, "bt2020", 10)));
    }

    [Theory]
    [InlineData("bt709")]
    [InlineData("smpte170m")]
    [InlineData("bt470bg")]
    public void SdrTransfers_AreNotHdr(string transfer)
    {
        Assert.Null(LibraryContentResolver.HdrTransferOf(Video(transfer, "bt709", 8)));
    }

    [Fact]
    public void UnknownTransfer_IsTreatedAsSdr()
    {
        Assert.Null(LibraryContentResolver.HdrTransferOf(Video(null)));
        Assert.Null(LibraryContentResolver.HdrTransferOf(Video(string.Empty)));
    }

    [Fact]
    public void TenBitBt2020WithoutPqTransfer_IsStillSdr()
    {
        // Wide gamut alone is not HDR; only the transfer curve decides whether tone mapping is needed.
        Assert.Null(LibraryContentResolver.HdrTransferOf(Video("bt709", "bt2020", 10)));
    }

    [Fact]
    public void DolbyVisionWithoutTransferTag_IsTreatedAsPq()
    {
        var stream = Video(null, "bt2020", 10);
        stream.DvProfile = 5;
        stream.RpuPresentFlag = 1;
        stream.BlPresentFlag = 1;
        stream.DvBlSignalCompatibilityId = 0;
        Assert.Equal(VideoRange.HDR, stream.VideoRange);
        Assert.Equal("smpte2084", LibraryContentResolver.HdrTransferOf(stream));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("qsv")]
    [InlineData("VAAPI")]
    [InlineData("cuda")]
    public void ValidateEncoding_AcceptsKnownHardwareDecoders(string kind)
    {
        Assert.Empty(StationService.ValidateEncoding(new EncodingProfile { HardwareDecode = kind }));
    }

    [Fact]
    public void ValidateEncoding_RejectsUnknownHardwareDecoder()
    {
        var errors = StationService.ValidateEncoding(new EncodingProfile { HardwareDecode = "opencl" });
        Assert.Single(errors);
        Assert.Contains("hardware decoding", errors[0], StringComparison.Ordinal);
    }
}
