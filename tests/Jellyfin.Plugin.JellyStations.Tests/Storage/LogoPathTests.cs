using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Storage;

public class LogoPathTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "jellystations-logos-test");

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.png")]
    [InlineData("logo.png")]
    [InlineData("a b.webp")]
    public void BareFileNamesStayInsideTheFolder(string name)
    {
        var path = JellyStationsPaths.ChildFile(Dir, name);
        Assert.NotNull(path);
        Assert.Equal(Path.Combine(Path.GetFullPath(Dir), name), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../settings.json")]
    [InlineData("..\\settings.json")]
    [InlineData("sub/logo.png")]
    [InlineData("sub\\logo.png")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:win.ini")]
    [InlineData("\\\\server\\share\\x.png")]
    public void AnythingElseIsRejected(string? name)
    {
        Assert.Null(JellyStationsPaths.ChildFile(Dir, name));
    }

    [Fact]
    public void BreakMatchesTheSchedulerRule()
    {
        var off = new Station { Bumpers = new BumperConfig { Enabled = false, BreakMinutes = 5 } };
        var on = new Station { Bumpers = new BumperConfig { Enabled = true, BreakMinutes = 5 } };
        var tooLong = new Station { Bumpers = new BumperConfig { Enabled = true, BreakMinutes = 99 } };

        Assert.Equal(TimeSpan.Zero, ScheduleInput.BreakFor(new Station()));
        Assert.Equal(TimeSpan.Zero, ScheduleInput.BreakFor(off));
        Assert.Equal(TimeSpan.FromMinutes(5), ScheduleInput.BreakFor(on));
        Assert.Equal(TimeSpan.FromMinutes(30), ScheduleInput.BreakFor(tooLong));
    }
}
