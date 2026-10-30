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
    PreRoll,

    /// <summary>A run of the station's weather screens.</summary>
    Weather
}

/// <summary>
/// Fills a gap with bumper clips. A post-roll clip (when configured) opens the gap and a pre-roll ident closes
/// it; between them a seeded pass over the fill clips places each one that still fits, in shuffled order, at
/// most once per gap, and whatever time is left stays a station card. When the station puts weather in its
/// breaks, a run of the weather screens takes up to its length at the head or the tail of that middle part,
/// stretching into time the clips left over. Deterministic per seed, so the guide, the preview and the stream agree.
/// </summary>
internal static class BumperFill
{
    public const string PreRollLabel = "Pre-roll";
    public const string PostRollLabel = "Post-roll";

    /// <summary>The least time a weather run in a break gets (a title card and a few screens); shorter room stays a card.</summary>
    public const int MinWeatherSeconds = 45;

    /// <summary>One slice of a gap: a clip, the weather (<see cref="BumperRole.Weather"/>), or the card remainder when <see cref="Clip"/> is null.</summary>
    public readonly record struct Piece(PlayableItem? Clip, long StartOffset, long EndOffset, BumperRole Role = BumperRole.Fill)
    {
        public bool IsWeather => Role == BumperRole.Weather;

        public bool IsCard => Clip is null && Role != BumperRole.Weather;
    }

    /// <summary>
    /// What a gap may draw from: clips (pre/post lists empty = no fixed clip in that position) and the weather
    /// (<paramref name="Weather"/> zero = none; otherwise the most a gap gets, at the head when
    /// <paramref name="WeatherFirst"/>, else at the tail before the pre-roll).
    /// </summary>
    public sealed record Clips(IReadOnlyList<PlayableItem> Fill, IReadOnlyList<PlayableItem> PreRoll, IReadOnlyList<PlayableItem> PostRoll, TimeSpan Weather = default, bool WeatherFirst = false, string? WeatherLabel = null)
    {
        public bool IsEmpty => Fill.Count == 0 && PreRoll.Count == 0 && PostRoll.Count == 0 && Weather <= TimeSpan.Zero;

        public static Clips From(ScheduleInput input) => new(input.Bumpers, input.PreRoll, input.PostRoll, input.WeatherBreak, input.WeatherBreakFirst, input.WeatherLabel);
    }

    /// <param name="gapTicks">Length of the gap.</param>
    /// <param name="clips">What may be placed.</param>
    /// <param name="seed">Seed for this gap; the pre- and post-roll picks derive their own from it.</param>
    /// <param name="programBefore">False when nothing aired right before the gap, so no post-roll is placed.</param>
    /// <param name="programAfter">False when no program follows the gap, so no pre-roll is placed.</param>
    /// <param name="weatherAdjacent">True when the weather already airs right before or after the gap (a weather block), so none is placed in it.</param>
    public static List<Piece> Plan(long gapTicks, Clips clips, ulong seed, bool programBefore = true, bool programAfter = true, bool weatherAdjacent = false)
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

        // The weather takes up to its length of the middle; at the head it goes first, at the tail the clips are
        // laid out around a reservation for it and it then stretches into whatever they left.
        var weatherMax = weatherAdjacent ? 0 : clips.Weather.Ticks;
        var minWeather = TimeSpan.FromSeconds(MinWeatherSeconds).Ticks;
        long weather = weatherMax > 0 ? Math.Min(weatherMax, end - pos) : 0;
        if (weather < minWeather)
        {
            weather = 0;
        }

        if (weather > 0 && clips.WeatherFirst)
        {
            pieces.Add(new Piece(null, pos, pos + weather, BumperRole.Weather));
            pos += weather;
            weather = 0;
        }

        var fillEnd = end - weather;
        if (clips.Fill.Count > 0)
        {
            foreach (var idx in SplitMix64.Permutation(clips.Fill.Count, seed))
            {
                var clip = clips.Fill[idx];
                if (clip.RuntimeTicks > 0 && clip.RuntimeTicks <= fillEnd - pos && !used.Contains(clip.ItemId))
                {
                    pieces.Add(new Piece(clip, pos, pos + clip.RuntimeTicks));
                    pos += clip.RuntimeTicks;
                    used.Add(clip.ItemId);
                }
            }
        }

        if (weather > 0)
        {
            weather = Math.Min(weatherMax, end - pos);
            if (pos < end - weather)
            {
                pieces.Add(new Piece(null, pos, end - weather));
            }

            pieces.Add(new Piece(null, end - weather, end, BumperRole.Weather));
            pos = end;
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
            var weatherAdjacent = (i > 0 && entries[i - 1].Kind == EntryKind.Weather) || (i + 1 < entries.Count && entries[i + 1].Kind == EntryKind.Weather);
            foreach (var p in Plan(e.Duration.Ticks, clips, seed(e), programBefore, programAfter, weatherAdjacent))
            {
                result.Add(ToEntry(e, p, clips.WeatherLabel));
            }
        }

        return result;
    }

    /// <summary>The entry for one piece of a slate: the card remainder keeps the slate's label, a clip gets its role as label, the weather its title.</summary>
    public static ScheduleEntry ToEntry(ScheduleEntry slate, Piece p, string? weatherLabel = null)
    {
        var start = slate.StartUtc.AddTicks(p.StartOffset);
        var end = slate.StartUtc.AddTicks(p.EndOffset);
        if (p.IsWeather)
        {
            return slate with { Kind = EntryKind.Weather, Item = null, StartUtc = start, EndUtc = end, SourceLabel = weatherLabel, InBreak = true };
        }

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
