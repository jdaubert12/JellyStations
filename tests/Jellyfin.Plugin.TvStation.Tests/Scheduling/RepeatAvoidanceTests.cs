using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Scheduling;

/// <summary>"Avoid the same series back-to-back" also keeps a movie from following itself, within a cycle and across the seam.</summary>
public class RepeatAvoidanceTests
{
    private static List<PlayableItem> Movies(params (string Title, int Minutes, string? Rating)[] films)
    {
        return films.Select(f =>
        {
            var m = Items.Movie(f.Title, f.Minutes);
            m.OfficialRating = f.Rating;
            m.ProductionYear = 1999;
            return m;
        }).ToList();
    }

    private static void AssertNoImmediateRepeat(IReadOnlyList<ScheduleEntry> entries)
    {
        var programs = entries.Where(e => e.Kind == EntryKind.Program).ToList();
        Assert.True(programs.Count > 20, "expected a long schedule");
        for (var i = 1; i < programs.Count; i++)
        {
            Assert.False(
                programs[i - 1].Item!.ItemId == programs[i].Item!.ItemId,
                $"{programs[i].Item!.Title} airs twice in a row at {programs[i].StartUtc:u}");
        }
    }

    [Fact]
    public void SmallMoviePool_NeverPlaysTheSameFilmTwiceInARow_AcrossCycles()
    {
        // Three films: without the seam check a third of all cycle boundaries would repeat the last film.
        var pool = Movies(("Dogma", 100, "R"), ("Clerks", 90, "R"), ("Mallrats", 95, "R"));
        var input = Items.PoolInput(pool, avoid: true, grid: 15);

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(20));

        AssertNoImmediateRepeat(entries);
    }

    [Fact]
    public void SameFilmFromTwoLibraryEntries_CountsAsOne()
    {
        // Two library items for one film (two editions) must not play back-to-back either.
        var pool = Movies(("Dogma", 100, "R"), ("Clerks", 90, "R"), ("Mallrats", 95, "R"), ("Chasing Amy", 110, "R"), ("Jay and Silent Bob", 100, "R"));
        var other = Items.Movie("Dogma", 128);
        other.ItemId = Guid.NewGuid();
        other.ProductionYear = 1999;
        pool.Add(other);
        var input = Items.PoolInput(pool, avoid: true);

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(20));

        var programs = entries.Where(e => e.Kind == EntryKind.Program).ToList();
        for (var i = 1; i < programs.Count; i++)
        {
            Assert.False(
                string.Equals(programs[i - 1].Item!.Title, programs[i].Item!.Title, StringComparison.OrdinalIgnoreCase),
                $"{programs[i].Item!.Title} airs twice in a row at {programs[i].StartUtc:u}");
        }
    }

    [Fact]
    public void WithDaypartRules_TheSeamIsReCheckedAfterTheGate()
    {
        // Like the real Comedy station: shuffled films, nothing above PG-13 between 06:00 and 20:00.
        var pool = Movies(
            ("Dogma", 100, "R"), ("Clerks", 90, "R"), ("Mallrats", 95, "R"), ("Chasing Amy", 110, "R"),
            ("Elf", 95, "PG"), ("Big", 100, "PG"), ("Airplane", 85, "PG"), ("Clue", 95, "PG"));
        var station = Items.PoolStation(pool, PlayOrder.Shuffle, grid: 15, avoid: true);
        station.Pool.DaypartRules = new List<DaypartRule>
        {
            new() { StartLocal = "06:00", EndLocal = "20:00", MaxRating = "PG-13", AllowUnrated = true }
        };
        var compiled = new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool };
        var input = ScheduleInput.From(station, compiled);

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(30));

        AssertNoImmediateRepeat(entries);
    }

    [Fact]
    public void MarathonPools_KeepMoviesApartToo()
    {
        var pool = Movies(("Dogma", 100, "R"), ("Clerks", 90, "R"), ("Mallrats", 95, "R"));
        pool.AddRange(Items.SeriesRun("Show", 6, 22));
        var station = Items.PoolStation(pool, PlayOrder.ShuffleEpisodesInOrder, avoid: true);
        station.Pool.MarathonMin = 2;
        station.Pool.MarathonMax = 3;
        var compiled = new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool };
        var input = ScheduleInput.From(station, compiled);

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(20));

        AssertNoImmediateRepeat(entries);
    }

    [Fact]
    public void KeyOf_SeriesByShow_MoviesByTitleAndYear()
    {
        var ep1 = Items.Episode("Show", 1, 1, 22);
        var ep2 = Items.Episode("Show", 2, 5, 22);
        var film = Items.Movie("Dogma", 100);
        film.ProductionYear = 1999;
        var remake = Items.Movie("dogma", 100);
        remake.ItemId = Guid.NewGuid();
        remake.ProductionYear = 2030;
        var copy = Items.Movie("DOGMA ", 128);
        copy.ItemId = Guid.NewGuid();
        copy.ProductionYear = 1999;

        Assert.Equal(PoolOrdering.KeyOf(ep1), PoolOrdering.KeyOf(ep2));
        Assert.Equal(PoolOrdering.KeyOf(film), PoolOrdering.KeyOf(copy));
        Assert.NotEqual(PoolOrdering.KeyOf(film), PoolOrdering.KeyOf(remake));
        Assert.NotEqual(PoolOrdering.KeyOf(film), PoolOrdering.KeyOf(ep1));
    }
}
