using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Weather;

public class WeatherSchedulerTests
{
    private static Station Station(int entryMinutes = 60, string? title = null)
    {
        var s = new Station
        {
            Id = Items.StationA,
            Name = "Weather",
            ChannelNumber = "8",
            Mode = ScheduleMode.Weather,
            TimeZoneId = "America/New_York",
            AnchorUtc = Items.Anchor,
            Revision = 2
        };
        s.Weather.GuideEntryMinutes = entryMinutes;
        s.Weather.GuideTitle = title;
        s.Weather.Locations.Add(new WeatherLocation { Name = "Boston", Latitude = 42.36, Longitude = -71.06 });
        s.Weather.Locations.Add(new WeatherLocation { Name = "Denver", Latitude = 39.74, Longitude = -104.99 });
        s.Weather.Locations.Add(new WeatherLocation { Name = "Nowhere" }); // invalid: no coordinates
        return s;
    }

    private static IStationScheduler Scheduler(Station s)
    {
        var resolver = new ScheduleResolver(_ => ScheduleInput.From(s, new CompiledStation { StationId = s.Id, Revision = s.Revision }));
        return new Wrapper(resolver, s.Id);
    }

    private sealed class Wrapper : IStationScheduler
    {
        private readonly ScheduleResolver _r;
        private readonly Guid _id;

        public Wrapper(ScheduleResolver r, Guid id)
        {
            _r = r;
            _id = id;
        }

        public ResolvedProgram? Resolve(DateTime utc) => _r.Resolve(_id, utc);

        public IEnumerable<ScheduleEntry> Enumerate(DateTime fromUtc, DateTime toUtc) => _r.Enumerate(_id, fromUtc, toUtc);
    }

    [Fact]
    public void Entries_AreHourly_OnTheStationsLocalClock()
    {
        var s = Scheduler(Station());
        // 14:37 UTC = 10:37 EDT; the entry runs 10:00-11:00 EDT = 14:00-15:00 UTC.
        var at = new DateTime(2026, 10, 23, 14, 37, 0, DateTimeKind.Utc);
        var resolved = s.Resolve(at)!;

        Assert.Equal(EntryKind.Weather, resolved.Entry.Kind);
        Assert.Null(resolved.Entry.Item);
        Assert.Equal(new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc), resolved.Entry.StartUtc);
        Assert.Equal(new DateTime(2026, 10, 23, 15, 0, 0, DateTimeKind.Utc), resolved.Entry.EndUtc);
        Assert.Equal(TimeSpan.FromMinutes(37), resolved.Offset);
        Assert.Equal("Local weather: Boston, Denver", resolved.Entry.Title);
        Assert.Equal(Items.StationA, resolved.Entry.StationId);
        Assert.Equal(2, resolved.Entry.Revision);
    }

    [Fact]
    public void Enumerate_IsContiguous_AndCoversTheWindow()
    {
        var s = Scheduler(Station(30));
        var from = new DateTime(2026, 10, 23, 14, 10, 0, DateTimeKind.Utc);
        var to = from.AddHours(3);
        var entries = s.Enumerate(from, to).ToList();

        Assert.Equal(7, entries.Count); // 14:00, 14:30, ..., 17:00
        Assert.True(entries[0].StartUtc <= from);
        Assert.True(entries[^1].EndUtc >= to);
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
            Assert.Equal(TimeSpan.FromMinutes(30), entries[i].EndUtc - entries[i].StartUtc);
        }

        Assert.Equal(entries.Count, entries.Select(e => e.ProgramId).Distinct().Count());
    }

    [Fact]
    public void Title_UsesTheTemplate()
    {
        var s = Scheduler(Station(60, "Weather on the 8s – {places}"));
        Assert.Equal("Weather on the 8s – Boston, Denver", s.Resolve(DateTime.UtcNow)!.Entry.Title);

        var plain = Scheduler(Station(60, "Local on the 8s"));
        Assert.Equal("Local on the 8s", plain.Resolve(DateTime.UtcNow)!.Entry.Title);
    }

    [Fact]
    public void DaylightSavingEnd_KeepsEntriesOnTheLocalGrid()
    {
        // New York leaves DST on 2026-11-01 at 02:00 EDT (06:00 UTC); 01:00 happens twice.
        var s = Scheduler(Station());
        var entries = s.Enumerate(new DateTime(2026, 11, 1, 4, 30, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 8, 30, 0, DateTimeKind.Utc)).ToList();
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }

        // After the change entries still start on the local hour (which is now UTC-5: on the UTC hour too).
        Assert.Contains(entries, e => e.StartUtc == new DateTime(2026, 11, 1, 7, 0, 0, DateTimeKind.Utc));
    }
}
