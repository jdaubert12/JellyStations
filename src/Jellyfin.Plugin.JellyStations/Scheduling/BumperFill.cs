using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>Where a clip sits in a break.</summary>
public enum BumperRole
{
    /// <summary>Gap fill: shuffled clips that happen to fit.</summary>
    Fill,

    /// <summary>The clip right after a program, at the head of the break.</summary>
    PostRoll,

    /// <summary>The ident right before the next program, at the tail of the break.</summary>
    PreRoll
}

/// <summary>
/// Fills a gap with bumper clips. A post-roll clip (when configured) opens the gap and a pre-roll ident closes
/// it; between them a seeded pass over the fill clips places each one that still fits, in shuffled order, at
/// most once per gap, and whatever time is left stays a station card. Deterministic per seed, so the guide, the
/// preview and the stream agree.
/// </summary>
internal static class BumperFill
{
    public const string PreRollLabel = "Pre-roll";
    public const string PostRollLabel = "Post-roll";

    /// <summary>One slice of a gap: a clip, or the card remainder when <see cref="Clip"/> is null.</summary>
    public readonly record struct Piece(PlayableItem? Clip, long StartOffset, long EndOffset, BumperRole Role = BumperRole.Fill);

    /// <summary>The clips a gap may draw from. Pre/post lists empty = no fixed clip in that position.</summary>
    public sealed record Clips(IReadOnlyList<PlayableItem> Fill, IReadOnlyList<PlayableItem> PreRoll, IReadOnlyList<PlayableItem> PostRoll)
    {
        public bool IsEmpty => Fill.Count == 0 && PreRoll.Count == 0 && PostRoll.Count == 0;

        public static Clips From(ScheduleInput input) => new(input.Bumpers, input.PreRoll, input.PostRoll);

        public static Clips FillOnly(IReadOnlyList<PlayableItem> fill) => new(fill, Array.Empty<PlayableItem>(), Array.Empty<PlayableItem>());
    }

    public static List<Piece> Plan(long gapTicks, IReadOnlyList<PlayableItem> clips, ulong seed)
        => Plan(gapTicks, Clips.FillOnly(clips), seed);

    /// <param name="gapTicks">Length of the gap.</param>
    /// <param name="clips">What may be placed.</param>
    /// <param name="seed">Seed for this gap; the pre- and post-roll picks derive their own from it.</param>
    /// <param name="programBefore">False when nothing aired right before the gap, so no post-roll is placed.</param>
    /// <param name="programAfter">False when no program follows the gap, so no pre-roll is placed.</param>
    public static List<Piece> Plan(long gapTicks, Clips clips, ulong seed, bool programBefore = true, bool programAfter = true)
    {
        var pieces = new List<Piece>();
        if (gapTicks <= 0)
        {
            return pieces;
        }

        long pos = 0;
        var end = gapTicks;
        var used = new HashSet<Guid>();

        var post = programBefore ? Pick(clips.PostRoll, end - pos, seed ^ 0x706F7374UL, used) : null;
        if (post is not null)
        {
            pieces.Add(new Piece(post, pos, pos + post.RuntimeTicks, BumperRole.PostRoll));
            pos += post.RuntimeTicks;
            used.Add(post.ItemId);
        }

        var pre = programAfter ? Pick(clips.PreRoll, end - pos, seed ^ 0x707265UL, used) : null;
        if (pre is not null)
        {
            end -= pre.RuntimeTicks;
            used.Add(pre.ItemId);
        }

        if (clips.Fill.Count > 0)
        {
            foreach (var idx in SplitMix64.Permutation(clips.Fill.Count, seed))
            {
                var clip = clips.Fill[idx];
                if (clip.RuntimeTicks > 0 && clip.RuntimeTicks <= end - pos && !used.Contains(clip.ItemId))
                {
                    pieces.Add(new Piece(clip, pos, pos + clip.RuntimeTicks));
                    pos += clip.RuntimeTicks;
                    used.Add(clip.ItemId);
                }
            }
        }

        if (pos < end)
        {
            pieces.Add(new Piece(null, pos, end));
        }

        if (pre is not null)
        {
            pieces.Add(new Piece(pre, end, gapTicks, BumperRole.PreRoll));
        }

        return pieces;
    }

    /// <summary>A seeded pick of the first clip that fits; prefers one not already used in this gap.</summary>
    private static PlayableItem? Pick(IReadOnlyList<PlayableItem> candidates, long room, ulong seed, HashSet<Guid> used)
    {
        if (candidates.Count == 0 || room <= 0)
        {
            return null;
        }

        PlayableItem? fallback = null;
        foreach (var idx in SplitMix64.Permutation(candidates.Count, seed))
        {
            var clip = candidates[idx];
            if (clip.RuntimeTicks <= 0 || clip.RuntimeTicks > room)
            {
                continue;
            }

            if (!used.Contains(clip.ItemId))
            {
                return clip;
            }

            fallback ??= clip;
        }

        return fallback;
    }

    /// <summary>Replaces every slate entry in a laid-out list with its planned clips plus any card remainder.</summary>
    public static List<ScheduleEntry> Expand(List<ScheduleEntry> entries, IReadOnlyList<PlayableItem> clips, Func<ScheduleEntry, ulong> seed)
        => Expand(entries, Clips.FillOnly(clips), seed);

    public static List<ScheduleEntry> Expand(List<ScheduleEntry> entries, Clips clips, Func<ScheduleEntry, ulong> seed)
    {
        if (clips.IsEmpty)
        {
            return entries;
        }

        var result = new List<ScheduleEntry>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Kind != EntryKind.Slate)
            {
                result.Add(e);
                continue;
            }

            var programBefore = i > 0 && entries[i - 1].Kind == EntryKind.Program;
            var programAfter = i + 1 < entries.Count && entries[i + 1].Kind == EntryKind.Program;
            foreach (var p in Plan(e.Duration.Ticks, clips, seed(e), programBefore, programAfter))
            {
                result.Add(ToEntry(e, p));
            }
        }

        return result;
    }

    /// <summary>The entry for one piece of a slate: the card remainder keeps the slate's label, a clip gets its role as label.</summary>
    public static ScheduleEntry ToEntry(ScheduleEntry slate, Piece p)
    {
        var start = slate.StartUtc.AddTicks(p.StartOffset);
        var end = slate.StartUtc.AddTicks(p.EndOffset);
        return p.Clip is null
            ? slate with { StartUtc = start, EndUtc = end }
            : slate with { Kind = EntryKind.Bumper, Item = p.Clip, StartUtc = start, EndUtc = end, SourceLabel = Label(p.Role) };
    }

    public static string? Label(BumperRole role) => role switch
    {
        BumperRole.PreRoll => PreRollLabel,
        BumperRole.PostRoll => PostRollLabel,
        _ => null
    };
}
