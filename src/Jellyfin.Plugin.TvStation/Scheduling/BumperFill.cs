using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>
/// Fills a gap with bumper clips: a seeded pass over the clips places each one that still fits, in shuffled
/// order, at most once per gap; whatever time is left stays a station card. Deterministic per seed, so the
/// guide, the preview and the stream agree.
/// </summary>
internal static class BumperFill
{
    /// <summary>One slice of a gap: a clip, or the card remainder when <see cref="Clip"/> is null.</summary>
    public readonly record struct Piece(PlayableItem? Clip, long StartOffset, long EndOffset);

    public static List<Piece> Plan(long gapTicks, IReadOnlyList<PlayableItem> clips, ulong seed)
    {
        var pieces = new List<Piece>();
        long pos = 0;
        if (clips.Count > 0 && gapTicks > 0)
        {
            foreach (var idx in SplitMix64.Permutation(clips.Count, seed))
            {
                var clip = clips[idx];
                if (clip.RuntimeTicks > 0 && clip.RuntimeTicks <= gapTicks - pos)
                {
                    pieces.Add(new Piece(clip, pos, pos + clip.RuntimeTicks));
                    pos += clip.RuntimeTicks;
                }
            }
        }

        if (pos < gapTicks)
        {
            pieces.Add(new Piece(null, pos, gapTicks));
        }

        return pieces;
    }

    /// <summary>Replaces every slate entry in a laid-out list with its planned clips plus any card remainder.</summary>
    public static List<ScheduleEntry> Expand(List<ScheduleEntry> entries, IReadOnlyList<PlayableItem> clips, Func<ScheduleEntry, ulong> seed)
    {
        if (clips.Count == 0)
        {
            return entries;
        }

        var result = new List<ScheduleEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (e.Kind != EntryKind.Slate)
            {
                result.Add(e);
                continue;
            }

            foreach (var p in Plan(e.Duration.Ticks, clips, seed(e)))
            {
                var start = e.StartUtc.AddTicks(p.StartOffset);
                var end = e.StartUtc.AddTicks(p.EndOffset);
                result.Add(p.Clip is null
                    ? e with { StartUtc = start, EndUtc = end }
                    : e with { Kind = EntryKind.Bumper, Item = p.Clip, StartUtc = start, EndUtc = end });
            }
        }

        return result;
    }
}
