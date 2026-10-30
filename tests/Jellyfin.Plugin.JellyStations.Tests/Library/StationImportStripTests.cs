using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class StationImportStripTests
{
    [Fact]
    public void NothingToStripLeavesTheProfileAlone()
    {
        var station = new Station { Encoding = new EncodingProfile { VideoBitrateKbps = 4000 } };

        var stripped = StationExportDocument.StripEncoderArguments(station);

        Assert.Empty(stripped);
        Assert.Equal(4000, station.Encoding.VideoBitrateKbps);
    }

    [Fact]
    public void NoProfileIsFine()
    {
        Assert.Empty(StationExportDocument.StripEncoderArguments(new Station { Encoding = null }));
    }

    [Fact]
    public void ExtraArgumentsAndDeviceAreClearedAndReported()
    {
        var station = new Station
        {
            Encoding = new EncodingProfile
            {
                VideoBitrateKbps = 4000,
                ExtraInputArgs = "-i /etc/passwd",
                ExtraOutputArgs = "-f mpegts /config/plugins/x.dll",
                HardwareDevice = "/dev/dri/renderD128"
            }
        };

        var stripped = StationExportDocument.StripEncoderArguments(station);

        Assert.Equal(3, stripped.Count);
        Assert.Contains(stripped, s => s.Contains("/etc/passwd", StringComparison.Ordinal));
        Assert.Contains(stripped, s => s.Contains("x.dll", StringComparison.Ordinal));
        Assert.Contains(stripped, s => s.Contains("renderD128", StringComparison.Ordinal));
        Assert.Equal(string.Empty, station.Encoding.ExtraInputArgs);
        Assert.Equal(string.Empty, station.Encoding.ExtraOutputArgs);
        Assert.Equal(string.Empty, station.Encoding.HardwareDevice);
        Assert.Equal(4000, station.Encoding.VideoBitrateKbps);
    }
}
