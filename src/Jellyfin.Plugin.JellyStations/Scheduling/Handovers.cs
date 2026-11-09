using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>
/// Where a new schedule takes over from the one on the air. Before 1.35 a new revision was laid out from the
/// station's anchor and took over wherever that layout happened to be, usually mid-program, so an edit or a refresh
/// jumped the stream into the middle of another program and the seeding had to patch the seam. Now the program on
/// the air finishes, its break plays out, and the new timeline begins at the next program boundary.
/// </summary>
public static class Handovers
{
    /// <summary>How far ahead of now the previous timeline is read when planning a handover.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromHours(25);

    /// <summary>How long the old schedule's card may be waited out for the sake of its next slot boundary.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Plans the handover from <paramref name="previous"/>, the timeline on the air enumerated from
    /// <paramref name="nowUtc"/>: the program (or weather block) on the air plays out, and so do the clips and
    /// weather of the break after it; the new schedule begins where the old one's next program or weather block
    /// would have started, which is a slot boundary on the old schedule's own grid phase. When that is further off
    /// than the grid (or <see cref="Grace"/>) because the old schedule shows its card, the break's end is used
    /// instead, moved up to the old phase when there is a grid. Null when nothing is on the air, so the new
    /// timeline starts from the anchor as it always did.
    /// </summary>
    public static Handover? Plan(IReadOnlyList<ScheduleEntry> previous, DateTime nowUtc, TimeSpan grid, bool byLength)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var live = previous.Where(e => e.EndUtc > nowUtc).OrderBy(e => e.StartUtc).ToList();
        if (live.Count == 0 || live[0].StartUtc > nowUtc)
        {
            return null;
        }

        var onAir = live[0];
        var breakEnd = nowUtc;
        if (onAir.Kind != EntryKind.Slate)
        {
            // The entry on the air plays out, and so do the clips and weather of the break after it. A card on the
            // air costs nothing to leave.
            breakEnd = onAir.EndUtc;
            for (var i = 1; i < live.Count && IsBreakPiece(live[i]); i++)
            {
                breakEnd = live[i].EndUtc;
            }
        }

        var nextContent = live.Skip(1).FirstOrDefault(IsContent)?.StartUtc;
        var step = byLength ? TimeSpan.FromMinutes(30) : grid;
        var grace = step > Grace ? step : Grace;
        DateTime until;
        if (nextContent is { } slot && slot - breakEnd <= grace)
        {
            until = slot;
        }
        else if (step > TimeSpan.Zero)
        {
            until = nextContent is { } phase ? AlignUp(breakEnd, phase, step) : CeilToGrid(breakEnd, step);
        }
        else
        {
            until = breakEnd;
        }

        if (until < nowUtc)
        {
            until = nowUtc;
        }

        var entries = live
            .Where(e => e.StartUtc < until)
            .Select(e => e.EndUtc > until ? e with { EndUtc = until } : e)
            .ToList();
        var covered = entries.Count > 0 ? entries[^1].EndUtc : nowUtc;
        if (covered < until)
        {
            // The grid pushed the boundary past what the old timeline had (never, since that timeline is contiguous): the card fills the rest.
            entries.Add(new ScheduleEntry(onAir.StationId, onAir.Revision, EntryKind.Slate, null, covered, until, false, null));
        }

        return new Handover { FromUtc = nowUtc, UntilUtc = until, Entries = entries };
    }

    /// <summary>A program or a weather block: something whose start is a boundary the guide shows.</summary>
    public static bool IsContent(ScheduleEntry e) => e.Kind == EntryKind.Program || (e.Kind == EntryKind.Weather && !e.InBreak);

    /// <summary>A clip or the weather in a break: plays out with the program before it.</summary>
    public static bool IsBreakPiece(ScheduleEntry e) => e.Kind == EntryKind.Bumper || (e.Kind == EntryKind.Weather && e.InBreak);

    private static DateTime CeilToGrid(DateTime utc, TimeSpan grid)
    {
        var floor = Padding.FloorToGrid(utc, grid);
        return floor < utc ? floor + grid : floor;
    }

    /// <summary>The earliest instant at or after <paramref name="utc"/> that is a whole number of steps before <paramref name="phase"/>.</summary>
    private static DateTime AlignUp(DateTime utc, DateTime phase, TimeSpan step)
        => phase - TimeSpan.FromTicks(Padding.FloorDiv((phase - utc).Ticks, step.Ticks) * step.Ticks);
}
