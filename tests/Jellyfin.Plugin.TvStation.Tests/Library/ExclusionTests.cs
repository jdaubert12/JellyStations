using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Services;
using Jellyfin.Plugin.TvStation.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Library;

public class ExclusionTests
{
    [Fact]
    public void ApplyExclusions_DropsListedItems_RecordsEachOnce_AndKeepsSnapshot()
    {
        var a = Items.Movie("A", 60);
        var b = Items.Movie("B", 60);
        var c = Items.Movie("C", 60);
        var compiled = new CompiledStation();
        var excluded = new HashSet<Guid> { b.ItemId };

        var pool = StationCompiler.ApplyExclusions(new List<PlayableItem> { a, b, c, b }, excluded, compiled);
        var filler = StationCompiler.ApplyExclusions(new List<PlayableItem> { b, c }, excluded, compiled);

        Assert.Equal(new[] { "A", "C" }, pool.Select(i => i.Title));
        Assert.Equal(new[] { "C" }, filler.Select(i => i.Title));
        Assert.Single(compiled.UserExcluded);
        Assert.Equal("B", compiled.UserExcluded[0].Title);
        Assert.Single(compiled.Excluded);
        Assert.Equal(ExclusionReason.UserExcluded, compiled.Excluded[0].Reason);
    }

    [Fact]
    public void ApplyExclusions_NoList_IsIdentity()
    {
        var items = new List<PlayableItem> { Items.Movie("A", 60) };
        var compiled = new CompiledStation();
        Assert.Same(items, StationCompiler.ApplyExclusions(items, new HashSet<Guid>(), compiled));
        Assert.Empty(compiled.Excluded);
    }

    [Fact]
    public void ToContentList_FlattensSources_CountsUniqueHours_AndListsExcludedLast()
    {
        var ep = Items.Episode("Show", 1, 1, 30);
        var movie = Items.Movie("Film", 120);
        var gone = Items.Movie("Gone", 90);
        var station = Items.PoolStation(Array.Empty<PlayableItem>());
        var compiled = new CompiledStation { Pool = new List<PlayableItem> { ep, movie, movie }, UserExcluded = new List<PlayableItem> { gone } };

        var list = StationService.ToContentList(station, compiled, 0, 100);
        Assert.Equal(3, list.Total);
        Assert.Equal(new[] { "Show", "Film", "Gone" }, list.Items.Select(i => i.Title));
        Assert.Equal(new[] { false, false, true }, list.Items.Select(i => i.Excluded));
        Assert.Equal(2.5, list.UniqueHours);
        Assert.Equal(4.5, list.CycleHours);
        Assert.Equal(1, list.ExcludedCount);
        Assert.Equal(2, list.BySource["Pool"]);
        Assert.Equal("Show", list.Items[0].Title);
        Assert.Equal(1, list.Items[0].EpisodeNumber);

        var page = StationService.ToContentList(station, compiled, 1, 1);
        Assert.Single(page.Items);
        Assert.Equal("Film", page.Items[0].Title);
    }
}
