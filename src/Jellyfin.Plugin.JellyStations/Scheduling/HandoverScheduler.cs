using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>
/// Serves the previous schedule's last stretch (see <see cref="Handover"/>) and hands over to this revision's own
/// timeline at <see cref="Handover.UntilUtc"/>. Before the stretch the station shows its card by the hour: that
/// time belongs to schedules that are gone.
/// </summary>
internal sealed class HandoverScheduler : IStationScheduler
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly ScheduleInput _input;
    private readonly IStationScheduler _inner;
    private readonly List<ScheduleEntry> _entries;
    private readonly DateTime _begin;
    private readonly DateTime _until;

    public HandoverScheduler(ScheduleInput input, Handover handover, IStationScheduler inner)
    {
        _input = input;
        _inner = inner;
        _until = DateTime.SpecifyKind(handover.UntilUtc, DateTimeKind.Utc);
        _entries = handover.Entries
            .Select(e => e with { StartUtc = Utc(e.StartUtc), EndUtc = Utc(e.EndUtc), FromPrevious = true })
            .Where(e => e.StartUtc < _until)
            .Select(e => e.EndUtc > _until ? e with { EndUtc = _until } : e)
            .OrderBy(e => e.StartUtc)
            .ToList();
        _begin = _entries.Count > 0 ? _entries[0].StartUtc : _until;
    }

    public ResolvedProgram? Resolve(DateTime utc)
    {
        if (utc >= _until)
        {
            return _inner.Resolve(utc);
        }

        if (utc >= _begin)
        {
            var next = _until;
            var start = _begin;
            foreach (var e in _entries)
            {
                if (utc >= e.StartUtc && utc < e.EndUtc)
                {
                    return new ResolvedProgram(e, utc - e.StartUtc);
                }

                if (e.EndUtc <= utc)
                {
                    start = e.EndUtc;
                }
                else
                {
                    next = e.StartUtc;
                    break;
                }
            }

            // A gap inside the stretch (never, by construction): the card until the next entry.
            var gap = ScheduleEntry.Slate(_input, start, next);
            return new ResolvedProgram(gap, utc - start);
        }

        var hour = Padding.FloorToGrid(utc, Hour);
        var end = hour + Hour < _begin ? hour + Hour : _begin;
        var slate = ScheduleEntry.Slate(_input, hour, end);
        return new ResolvedProgram(slate, utc - hour);
    }

    public IEnumerable<ScheduleEntry> Enumerate(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
        {
            yield break;
        }

        var t = fromUtc;
        while (t < _begin && t < toUtc)
        {
            var hour = Padding.FloorToGrid(t, Hour);
            var end = hour + Hour < _begin ? hour + Hour : _begin;
            yield return ScheduleEntry.Slate(_input, hour, end);
            t = end;
        }

        foreach (var e in _entries)
        {
            if (e.EndUtc > fromUtc && e.StartUtc < toUtc)
            {
                yield return e;
            }
        }

        if (toUtc > _until)
        {
            foreach (var e in _inner.Enumerate(fromUtc > _until ? fromUtc : _until, toUtc))
            {
                yield return e;
            }
        }
    }

    private static DateTime Utc(DateTime dt) => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc);
}
