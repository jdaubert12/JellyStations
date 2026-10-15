using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;

namespace Jellyfin.Plugin.TvStation.Tests.Fixtures;

internal static class Items
{
    public static readonly Guid StationA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly DateTime Anchor = new(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc); // a Monday 00:00 America/New_York

    public static PlayableItem Episode(string series, int season, int ep, int minutes, Guid? seriesId = null)
    {
        var sid = seriesId ?? DeterministicGuid(series);
        return new PlayableItem
        {
            ItemId = DeterministicGuid($"{series}-S{season}E{ep}"),
            Path = $"/media/{series}/S{season:00}E{ep:00}.mkv",
            RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
            Title = series,
            EpisodeTitle = $"Episode {ep}",
            SeriesId = sid,
            SeasonNumber = season,
            EpisodeNumber = ep,
            IsSeries = true
        };
    }

    public static PlayableItem Movie(string title, int minutes)
    {
        return new PlayableItem
        {
            ItemId = DeterministicGuid(title),
            Path = $"/media/movies/{title}.mkv",
            RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
            Title = title,
            IsMovie = true
        };
    }

    public static List<PlayableItem> SeriesRun(string series, int count, int minutes)
    {
        return Enumerable.Range(1, count).Select(i => Episode(series, 1, i, minutes)).ToList();
    }

    public static Station PoolStation(IEnumerable<PlayableItem> _, PlayOrder order = PlayOrder.Shuffle, int grid = 0, int revision = 1, bool avoid = true, bool byLength = false)
    {
        return new Station
        {
            Id = StationA,
            Name = "Test",
            ChannelNumber = "100",
            AnchorUtc = Anchor,
            Revision = revision,
            PaddingGridMinutes = grid,
            PaddingByLength = byLength,
            Mode = ScheduleMode.Pool,
            Pool = new PoolConfig { Order = order, AvoidSameSeriesBackToBack = avoid },
            TimeZoneId = "America/New_York"
        };
    }

    public static ScheduleInput PoolInput(List<PlayableItem> pool, PlayOrder order = PlayOrder.Shuffle, int grid = 0, int revision = 1, bool avoid = true, bool byLength = false)
    {
        var station = PoolStation(pool, order, grid, revision, avoid, byLength);
        var compiled = new CompiledStation { StationId = station.Id, Revision = revision, Pool = pool };
        return ScheduleInput.From(station, compiled);
    }

    public static ScheduleInput LineupInput(List<PlayableItem> lineup, int grid = 0)
    {
        var station = PoolStation(lineup, PlayOrder.Sequential, grid);
        station.Mode = ScheduleMode.Lineup;
        var compiled = new CompiledStation { StationId = station.Id, Revision = 1, Lineup = lineup };
        return ScheduleInput.From(station, compiled);
    }

    public static ScheduleInput SlotsInput(List<SlotBlock> blocks, Dictionary<Guid, List<PlayableItem>> blockItems, List<PlayableItem> filler, PlayOrder fillerOrder = PlayOrder.Sequential, string tz = "America/New_York", int grid = 0)
    {
        var station = new Station
        {
            Id = StationA,
            Name = "Slots",
            ChannelNumber = "101",
            AnchorUtc = Anchor,
            Revision = 1,
            PaddingGridMinutes = grid,
            Mode = ScheduleMode.Slots,
            TimeZoneId = tz,
            Slots = new SlotsConfig { Blocks = blocks, Filler = new FillerConfig { Order = fillerOrder } }
        };
        var compiled = new CompiledStation { StationId = station.Id, Revision = 1, Filler = filler, Blocks = blockItems };
        return ScheduleInput.From(station, compiled);
    }

    public static ScheduleInput PoolWithBlocksInput(List<PlayableItem> pool, List<SlotBlock> blocks, Dictionary<Guid, List<PlayableItem>> blockItems, PlayOrder order = PlayOrder.Shuffle, bool avoid = true, int marathonMin = 1, int marathonMax = 1, List<DaypartRule>? rules = null, int grid = 0)
    {
        var station = PoolStation(pool, order, grid, 1, avoid);
        station.Pool.MarathonMin = marathonMin;
        station.Pool.MarathonMax = marathonMax;
        station.Pool.DaypartRules = rules ?? new List<DaypartRule>();
        station.Slots = new SlotsConfig { Blocks = blocks };
        var compiled = new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool, Blocks = blockItems };
        return ScheduleInput.From(station, compiled);
    }

    public static Guid DeterministicGuid(string s)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return new Guid(bytes);
    }
}
