using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Scheduling;

/// <summary>Per-pool cooldown: a series or movie rests N hours before it may air again (soft rule).</summary>
public class CooldownTests
{
    private static List<PlayableItem> Films(int count, int minutes = 100, string rating = "PG")
    {
        return Enumerable.Range(1, count).Select(i =>
        {
            var m = Items.Movie("Film " + i, minutes);
            m.OfficialRating = rating;
            m.ProductionYear = 2000 + i;
            return m;
        }).ToList();
    }

    private static ScheduleInput Input(List<PlayableItem> pool, int cooldownHours, bool byLength = true, List<DaypartRule>? rules = null)
    {
        var station = Items.PoolStation(pool, PlayOrder.Shuffle, grid: 0, avoid: true, byLength: byLength);
        station.Pool.CooldownHours = cooldownHours;
        if (rules is not null)
        {
            station.Pool.DaypartRules = rules;
        }

        return ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool });
    }

    private static List<ScheduleEntry> Programs(ScheduleInput input, int days)
        => ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(days)).Where(e => e.Kind == EntryKind.Program).ToList();

    /// <summary>Smallest gap between the end of one airing and the start of the next airing of the same item.</summary>
    private static TimeSpan SmallestGap(IEnumerable<ScheduleEntry> programs)
    {
        var lastEnd = new Dictionary<Guid, DateTime>();
        var smallest = TimeSpan.MaxValue;
        foreach (var p in programs)
        {
            var id = p.Item!.ItemId;
            if (lastEnd.TryGetValue(id, out var end) && p.StartUtc - end < smallest)
            {
                smallest = p.StartUtc - end;
            }

            lastEnd[id] = p.EndUtc;
        }

        return smallest;
    }

    [Fact]
    public void Cooldown_KeepsAFilmOffTheAir_ForTheWindow_AcrossRounds()
    {
        // Eight films in two-hour slots: a 16 h round, so without the rule a film from the end of one round can
        // come back within an hour or two at the start of the next.
        var pool = Films(8);

        var without = SmallestGap(Programs(Input(pool, 0), 30));
        var with = SmallestGap(Programs(Input(pool, 6), 30));

        Assert.True(without < TimeSpan.FromHours(6), "the test pool should repeat quickly without a cooldown; smallest gap was " + without);
        Assert.True(with >= TimeSpan.FromHours(6), "smallest gap with a 6 h cooldown was " + with);
    }

    [Fact]
    public void Cooldown_HoldsWeightedRepeatsApart_InsideARound()
    {
        var pool = Films(7);
        pool.Add(pool[0]); // weight 2: the same item twice in one round
        pool.Add(pool[1]);

        var programs = Programs(Input(pool, 4), 20);

        Assert.True(SmallestGap(programs) >= TimeSpan.FromHours(4), "smallest gap was " + SmallestGap(programs));
    }

    [Fact]
    public void Cooldown_IsSoft_WhenThePoolIsTooSmall()
    {
        // Three films, 6 h round, 10 h cooldown: nothing can satisfy it; the schedule still plays every film.
        var pool = Films(3);
        var input = Input(pool, 10);

        var programs = Programs(input, 10);
        var again = Programs(input, 10);

        Assert.Equal(Programs(Input(pool, 0), 10).Count, programs.Count);
        Assert.Equal(programs.Select(p => p.ProgramId), again.Select(p => p.ProgramId));
        Assert.Equal(3, programs.Select(p => p.Item!.ItemId).Distinct().Count());
    }

    [Fact]
    public void Cooldown_LeavesTheDaypartGateDecisionsAlone()
    {
        var pool = Films(5, rating: "PG");
        pool.AddRange(Films(4, rating: "R").Select(m => { m.Title += " (R)"; m.ItemId = Guid.NewGuid(); return m; }));
        var rules = new List<DaypartRule> { new() { StartLocal = "06:00", EndLocal = "20:00", MaxRating = "PG-13", AllowUnrated = true } };

        var plain = ScheduleResolver.EnumerateInput(Input(pool, 0, rules: rules), Items.Anchor, Items.Anchor.AddDays(30));
        var cooled = ScheduleResolver.EnumerateInput(Input(pool, 5, rules: rules), Items.Anchor, Items.Anchor.AddDays(30));

        TimeSpan Slates(IEnumerable<ScheduleEntry> entries) => TimeSpan.FromTicks(entries.Where(e => e.Kind == EntryKind.Slate).Sum(e => e.Duration.Ticks));
        Assert.Equal(Slates(plain), Slates(cooled));
        Assert.True(SmallestGap(cooled.Where(e => e.Kind == EntryKind.Program)) >= SmallestGap(plain.Where(e => e.Kind == EntryKind.Program)));
    }

    [Fact]
    public void CooldownZero_IsTheOldSchedule()
    {
        var pool = Films(6);
        var station = Items.PoolStation(pool, PlayOrder.Shuffle, grid: 15, avoid: true);
        var baseline = ScheduleResolver.EnumerateInput(ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool }), Items.Anchor, Items.Anchor.AddDays(7));

        station.Pool.CooldownHours = 0;
        var same = ScheduleResolver.EnumerateInput(ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool }), Items.Anchor, Items.Anchor.AddDays(7));

        Assert.Equal(baseline.Select(e => e.ProgramId + e.Title), same.Select(e => e.ProgramId + e.Title));
    }
}
