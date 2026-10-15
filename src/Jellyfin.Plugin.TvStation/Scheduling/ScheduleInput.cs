using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>Everything a scheduler needs, flattened from a Station and its CompiledStation.</summary>
public sealed class ScheduleInput
{
    public Guid StationId { get; init; }

    public int Revision { get; init; }

    public DateTime AnchorUtc { get; init; }

    public TimeSpan PaddingGrid { get; init; }

    public ScheduleMode Mode { get; init; }

    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>Pad each program to a half hour (up to 60 min) or whole hours (longer) instead of the fixed grid.</summary>
    public bool PaddingByLength { get; init; }

    public IReadOnlyList<PlayableItem> Pool { get; init; } = Array.Empty<PlayableItem>();

    public PlayOrder PoolOrder { get; init; }

    public bool AvoidSameSeriesBackToBack { get; init; }

    public int MarathonMin { get; init; } = 1;

    public int MarathonMax { get; init; } = 1;

    public IReadOnlyList<DaypartRuleInput> DaypartRules { get; init; } = Array.Empty<DaypartRuleInput>();

    /// <summary>Clips that may fill gaps; empty = station card only.</summary>
    public IReadOnlyList<PlayableItem> Bumpers { get; init; } = Array.Empty<PlayableItem>();

    /// <summary>Reserved break after every program (zero when bumpers are off).</summary>
    public TimeSpan Break { get; init; }

    public IReadOnlyList<PlayableItem> Lineup { get; init; } = Array.Empty<PlayableItem>();

    public IReadOnlyList<SlotBlockInput> Blocks { get; init; } = Array.Empty<SlotBlockInput>();

    /// <summary>True when the schedule is a weekly template of blocks: Slots mode, or Pool mode with at least one block.</summary>
    public bool HasBlocks => Mode == ScheduleMode.Slots || (Mode == ScheduleMode.Pool && Blocks.Count > 0);

    public IReadOnlyList<PlayableItem> Filler { get; init; } = Array.Empty<PlayableItem>();

    public PlayOrder FillerOrder { get; init; }

    public static ScheduleInput From(Station station, CompiledStation compiled)
    {
        var blocks = station.Slots.Blocks
            .Select(b => new SlotBlockInput
            {
                Id = b.Id,
                Name = string.IsNullOrWhiteSpace(b.Name) ? "Block" : b.Name,
                Days = b.Days.Distinct().ToList(),
                StartLocal = ParseTime(b.StartLocal),
                Duration = TimeSpan.FromMinutes(Math.Clamp(b.DurationMinutes, 1, 24 * 60)),
                Order = b.Order,
                Overflow = b.Overflow,
                RemainderMayOverrun = b.RemainderMayOverrun,
                MaxStartDelay = TimeSpan.FromMinutes(Math.Clamp(b.MaxStartDelayMinutes, 0, 240)),
                Items = compiled.Blocks.TryGetValue(b.Id, out var items) ? items : new List<PlayableItem>()
            })
            .ToList();

        return new ScheduleInput
        {
            StationId = station.Id,
            Revision = station.Revision,
            AnchorUtc = DateTime.SpecifyKind(station.AnchorUtc, DateTimeKind.Utc),
            PaddingGrid = TimeSpan.FromMinutes(Math.Max(0, station.PaddingGridMinutes)),
            PaddingByLength = station.PaddingByLength,
            Mode = station.Mode,
            TimeZoneId = string.IsNullOrWhiteSpace(station.TimeZoneId) ? "UTC" : station.TimeZoneId,
            Pool = compiled.Pool,
            PoolOrder = station.Pool.Order,
            AvoidSameSeriesBackToBack = station.Pool.AvoidSameSeriesBackToBack,
            MarathonMin = station.Pool.MarathonMin,
            MarathonMax = station.Pool.MarathonMax,
            DaypartRules = (station.Pool.DaypartRules ?? new List<DaypartRule>())
                .Select(r => new DaypartRuleInput
                {
                    Start = ParseTime(r.StartLocal),
                    End = ParseTime(r.EndLocal),
                    MaxRank = Library.RatingLadder.Rank(r.MaxRating) ?? int.MaxValue,
                    AllowUnrated = r.AllowUnrated
                })
                .Where(r => r.MaxRank != int.MaxValue)
                .ToList(),
            Lineup = compiled.Lineup,
            Blocks = blocks,
            Filler = compiled.Filler,
            FillerOrder = station.Slots.Filler.Order,
            Bumpers = station.Bumpers?.Enabled == true ? compiled.Bumpers : Array.Empty<PlayableItem>(),
            Break = station.Bumpers?.Enabled == true ? TimeSpan.FromMinutes(Math.Clamp(station.Bumpers.BreakMinutes, 0, 30)) : TimeSpan.Zero
        };
    }

    public static TimeSpan ParseTime(string? hhmm)
    {
        if (TimeSpan.TryParseExact(hhmm ?? string.Empty, @"hh\:mm", null, out var t))
        {
            return t;
        }

        if (TimeSpan.TryParse(hhmm, out t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
        {
            return t;
        }

        return TimeSpan.FromHours(20);
    }
}

/// <summary>A parsed daypart rule: a local time window and the highest allowed ladder rank inside it.</summary>
public sealed class DaypartRuleInput
{
    public TimeSpan Start { get; init; }

    public TimeSpan End { get; init; }

    public int MaxRank { get; init; }

    public bool AllowUnrated { get; init; }
}

public sealed class SlotBlockInput
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<DayOfWeek> Days { get; init; } = Array.Empty<DayOfWeek>();

    public TimeSpan StartLocal { get; init; }

    public TimeSpan Duration { get; init; }

    public PlayOrder Order { get; init; }

    public SlotOverflow Overflow { get; init; }

    public bool RemainderMayOverrun { get; init; }

    /// <summary>How late the block may start so the previous program can finish. Zero = start on time.</summary>
    public TimeSpan MaxStartDelay { get; init; }

    public IReadOnlyList<PlayableItem> Items { get; init; } = Array.Empty<PlayableItem>();
}
