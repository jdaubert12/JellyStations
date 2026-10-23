using System.Text.RegularExpressions;
using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Music;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Weather;

public class MusicPackTests
{
    private static readonly IReadOnlyList<MusicPack> Packs = MusicPackService.LoadCatalog(typeof(MusicPackService).Assembly);

    [Fact]
    public void Catalog_HasTheStarterPackAndTheOptionalOne()
    {
        Assert.Contains(Packs, p => p.Id == "lounge" && !p.Manual);
        Assert.Contains(Packs, p => p.Id == "lofi" && p.Manual);
        Assert.Equal(Packs.Count, Packs.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Packs.Count, Packs.Select(p => p.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AutomaticPacks_HaveFetchableCheckedTracks()
    {
        foreach (var pack in Packs.Where(p => !p.Manual))
        {
            Assert.NotEmpty(pack.Tracks);
            Assert.False(string.IsNullOrWhiteSpace(pack.Licence));
            Assert.StartsWith("https://", pack.LicenceUrl, StringComparison.Ordinal);
            Assert.Equal(pack.Tracks.Count, pack.Tracks.Select(t => t.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var t in pack.Tracks)
            {
                Assert.StartsWith("https://", t.Url, StringComparison.Ordinal);
                Assert.Matches(new Regex("^[0-9a-f]{64}$"), t.Sha256);
                Assert.True(t.Bytes > 100_000, t.Title);
                Assert.InRange(t.Seconds, 30, 20 * 60);
                Assert.False(string.IsNullOrWhiteSpace(t.Title));
                Assert.False(string.IsNullOrWhiteSpace(t.Artist));
                Assert.Equal(t.FileName, Path.GetFileName(t.FileName));
                Assert.True(MusicFolderScanner.IsAudioFile(t.FileName), t.FileName);
            }
        }
    }

    [Fact]
    public void StarterPack_IsAboutTwoHoursOfLounge()
    {
        var lounge = Packs.Single(p => p.Id == "lounge");
        Assert.Equal(30, lounge.Tracks.Count);
        Assert.InRange(lounge.TotalSeconds, 100 * 60, 130 * 60);
        Assert.Contains(lounge.Tracks, t => t.Title == "Local Forecast");
        Assert.Contains("Kevin MacLeod", lounge.Attribution);
    }

    [Fact]
    public void ManualPacks_HaveLinksAndNoUrls()
    {
        foreach (var pack in Packs.Where(p => p.Manual))
        {
            Assert.NotEmpty(pack.Links);
            Assert.All(pack.Links, l => Assert.StartsWith("https://", l.Url, StringComparison.Ordinal));
            Assert.All(pack.Tracks, t => Assert.Equal(string.Empty, t.Url));
        }
    }

    [Fact]
    public void Credits_ListEveryTrackWithItsSource()
    {
        var lounge = Packs.Single(p => p.Id == "lounge");
        var text = MusicPackService.Credits(lounge);
        Assert.Contains(lounge.Licence, text);
        foreach (var t in lounge.Tracks)
        {
            Assert.Contains(t.Title + " - " + t.Artist, text);
            Assert.Contains(t.SourcePage, text);
        }
    }

    [Fact]
    public void HashesMatch_IgnoresCase()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.True(MusicPackService.HashesMatch(bytes, Convert.ToHexString(bytes).ToLowerInvariant()));
        Assert.False(MusicPackService.HashesMatch(bytes, new string('0', 64)));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("lounge", 1)]
    [InlineData("lounge; lofi", 2)]
    [InlineData("lounge;lofi;", 2)]
    [InlineData("lounge\n/media/music/ambient\r\nLounge", 2)]
    public void Folders_SplitOnSemicolonsAndLines(string? setting, int count)
    {
        var folders = MusicResolver.Folders(setting);
        Assert.Equal(count, folders.Count);
        Assert.All(folders, f => Assert.Equal(f.Trim(), f));
    }
}
