using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class StationHealthTests
{
    private static Station Station() => new() { Id = Items.StationA, Name = "Test", ChannelNumber = "5", Enabled = true };

    private static CompiledStation Compiled(params PlayableItem[] pool) => new()
    {
        StationId = Items.StationA,
        Revision = 1,
        CompiledUtc = new DateTime(2026, 10, 19, 4, 0, 0, DateTimeKind.Utc),
        Pool = pool.ToList()
    };

    [Fact]
    public void NotCompiled_WarnsOnce()
    {
        var h = StationHealth.Build(Station(), null);

        Assert.False(h.Healthy);
        Assert.Single(h.Warnings);
        Assert.Contains("Not compiled", h.Warnings[0]);
        Assert.Null(h.CompiledUtc);
    }

    [Fact]
    public void HealthyStation_HasNoWarnings()
    {
        var compiled = Compiled(Items.SeriesRun("Show", 30, 60).ToArray());
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "Series \"Show\"", Kind = SourceKind.Series, Found = 30, Kept = 30 });

        var h = StationHealth.Build(Station(), compiled);

        Assert.True(h.Healthy);
        Assert.Equal(30, h.UniqueHours);
        Assert.Empty(h.EmptySources);
        Assert.Equal(compiled.CompiledUtc, h.CompiledUtc);
    }

    [Fact]
    public void EmptySources_AreListed_WithTheRightReason()
    {
        var compiled = Compiled(Items.SeriesRun("Show", 30, 60).ToArray());
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "Genre \"Western\"", Kind = SourceKind.Genre, Found = 0, Kept = 0 });
        compiled.Sources.Add(new SourceExpansion { Section = "Movie Night", Label = "BoxSet \"Trilogy\"", Kind = SourceKind.BoxSet, Found = 3, Kept = 0 });
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "Series \"Show\"", Kind = SourceKind.Series, Found = 30, Kept = 30 });
        // A hand-picked movie that an earlier source already contributed is on the station, so it is not "empty".
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "Movie \"Elf\"", Kind = SourceKind.Movie, Found = 1, Kept = 0, AlreadyListed = 1 });
        // But a box set where one item was already listed and the other two were unusable still is.
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "BoxSet \"Mixed\"", Kind = SourceKind.BoxSet, Found = 3, Kept = 0, AlreadyListed = 1 });

        var h = StationHealth.Build(Station(), compiled);

        Assert.Equal(new[] { "Pool: Genre \"Western\"", "Movie Night: BoxSet \"Trilogy\"", "Pool: BoxSet \"Mixed\"" }, h.EmptySources);
        Assert.Equal(3, h.Warnings.Count);
        Assert.Contains("matched nothing", h.Warnings[0]);
        Assert.Contains("found 3 item(s) but none were usable", h.Warnings[1]);
        Assert.DoesNotContain(h.Warnings, w => w.Contains("Elf"));
    }

    [Fact]
    public void UnderADayOfUniqueContent_Warns()
    {
        var h = StationHealth.Build(Station(), Compiled(Items.Movie("A", 90), Items.Movie("B", 120)));

        Assert.Equal(3.5, h.UniqueHours);
        var warning = Assert.Single(h.Warnings);
        Assert.Contains("3.5 h of unique content", warning);
        Assert.Contains("under a day", warning);
    }

    [Fact]
    public void NoItems_WarnsAboutTheCard_NotAboutHours()
    {
        var h = StationHealth.Build(Station(), Compiled());

        var warning = Assert.Single(h.Warnings);
        Assert.Contains("No playable items", warning);
    }

    [Fact]
    public void UniqueHours_CountEachItemOnce_AcrossSections_IgnoringBumpers()
    {
        var a = Items.Movie("A", 60);
        var b = Items.Movie("B", 60);
        var compiled = Compiled(a, a, b);
        compiled.Blocks[Guid.NewGuid()] = new List<PlayableItem> { a, Items.Movie("C", 60) };
        compiled.Filler = new List<PlayableItem> { b };
        compiled.Bumpers = new List<PlayableItem> { Items.Movie("Ident", 1) };

        Assert.Equal(3, StationHealth.UniqueHours(compiled));
    }

    [Fact]
    public void SkippedItems_Warn_ButUserChoicesDoNot()
    {
        var compiled = Compiled(Items.SeriesRun("Show", 30, 60).ToArray());
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "x", Reason = ExclusionReason.NoRuntime });
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "y", Reason = ExclusionReason.NoRuntime });
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "z", Reason = ExclusionReason.NotFound });
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "w", Reason = ExclusionReason.UserExcluded });
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "v", Reason = ExclusionReason.TooShort });

        var h = StationHealth.Build(Station(), compiled);

        Assert.Equal(2, h.ExcludedByReason["NoRuntime"]);
        Assert.Equal(1, h.ExcludedByReason["UserExcluded"]);
        Assert.Equal(1, h.ExcludedByReason["TooShort"]);
        Assert.Equal(2, h.Warnings.Count);
        Assert.Contains("2 items skipped: no runtime", h.Warnings[0]);
        Assert.Contains("1 item skipped: no longer in the library", h.Warnings[1]);
    }
}
