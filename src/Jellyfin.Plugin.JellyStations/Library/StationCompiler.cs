using Jellyfin.Plugin.JellyStations.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>Turns a Station definition into a frozen CompiledStation snapshot.</summary>
public sealed class StationCompiler
{
    private readonly LibraryContentResolver _resolver;
    private readonly MusicResolver? _music;
    private readonly ILogger<StationCompiler> _logger;

    public StationCompiler(LibraryContentResolver resolver, ILogger<StationCompiler> logger, MusicResolver? music = null)
    {
        _resolver = resolver;
        _logger = logger;
        _music = music;
    }

    public CompiledStation Compile(Station station, EncodingProfile effectiveProfile, IReadOnlyCollection<string>? globalExcludedTags = null)
    {
        var lang = effectiveProfile.AudioLanguagePreference ?? string.Empty;
        var tags = EffectiveExcludedTags(station, globalExcludedTags);
        var compiled = new CompiledStation
        {
            StationId = station.Id,
            Revision = station.Revision,
            CompiledUtc = DateTime.UtcNow,
            Format = CompiledStation.CurrentFormat
        };

        var excludedIds = new HashSet<Guid>(station.ExcludedItemIds ?? new List<Guid>());
        List<PlayableItem> Expand(List<ContentSource> sources, string section) => _resolver.Expand(
            sources,
            lang,
            compiled.Excluded,
            tags,
            compiled.TagExcluded,
            (source, found, kept, alreadyListed) => compiled.Sources.Add(SourceExpansion.For(section, source, found, kept, alreadyListed)));

        // Programs: expanded, minus the station's excluded items, minus anything under the minimum runtime.
        List<PlayableItem> Programs(List<ContentSource> sources, string section)
            => DropShort(ApplyExclusions(Expand(sources, section), excludedIds, compiled), station.MinRuntimeMinutes, compiled);

        void CompileBlocks()
        {
            foreach (var block in station.Slots.Blocks)
            {
                compiled.Blocks[block.Id] = Programs(block.Sources, string.IsNullOrWhiteSpace(block.Name) ? "Block" : block.Name);
            }
        }

        switch (station.Mode)
        {
            case ScheduleMode.Pool:
                compiled.Pool = Programs(station.Pool.Sources, "Pool");
                CompileBlocks();
                break;

            case ScheduleMode.Lineup:
                foreach (var entry in station.Lineup.Items)
                {
                    var item = _resolver.Single(entry.ItemId, lang, compiled.Excluded);
                    if (item is not null)
                    {
                        compiled.Lineup.Add(item);
                    }
                }

                break;

            case ScheduleMode.Slots:
                CompileBlocks();
                compiled.Filler = Programs(station.Slots.Filler.Sources, "Filler");
                break;

            case ScheduleMode.Weather:
                break;
        }

        // Only a weather channel compiles music; an ordinary station's weather block or breaks play the channel's.
        if (_music is not null && station.Mode == ScheduleMode.Weather && station.Weather.MusicEnabled)
        {
            compiled.Music = _music.Expand(station.Weather, compiled.Sources, compiled.Excluded);
        }

        if (station.Bumpers?.Enabled == true)
        {
            // Clips: expanded, minus the station's excluded items, kept only when they have a runtime within the clip cap.
            var maxTicks = TimeSpan.FromMinutes(Math.Max(1, station.Bumpers.MaxClipMinutes)).Ticks;
            List<PlayableItem> Clips(List<ContentSource> sources, string section)
                => ApplyExclusions(Expand(sources, section), excludedIds, compiled)
                    .Where(i => i.RuntimeTicks > 0 && i.RuntimeTicks <= maxTicks)
                    .ToList();

            // Bumpers first: a roll without sources of its own copies them.
            if (station.Bumpers.Sources.Count > 0)
            {
                compiled.Bumpers = Clips(station.Bumpers.Sources, "Bumpers");
            }

            compiled.PreRoll = CompileRoll(station.Bumpers.PreRoll, compiled.Bumpers, s => Clips(s, "Pre-roll"));
            compiled.PostRoll = CompileRoll(station.Bumpers.PostRoll, compiled.Bumpers, s => Clips(s, "Post-roll"));
        }

        // Same item may be excluded via several sources; report once.
        compiled.Excluded = compiled.Excluded
            .GroupBy(e => e.ItemId)
            .Select(g => g.First())
            .ToList();

        _logger.LogInformation(
            "JellyStations: compiled station {Name} rev {Revision}: {Items} item(s), {Tracks} music track(s), {Excluded} excluded",
            station.Name,
            station.Revision,
            compiled.TotalItemCount,
            compiled.Music.Count,
            compiled.Excluded.Count);

        return compiled;
    }

    /// <summary>The clips for a pre/post-roll position: its own sources when it has any, otherwise the main bumper list.</summary>
    private static List<PlayableItem> CompileRoll(BumperRollConfig? roll, List<PlayableItem> bumpers, Func<List<ContentSource>, List<PlayableItem>> clips)
    {
        if (roll?.Enabled != true)
        {
            return new List<PlayableItem>();
        }

        return roll.Sources.Count == 0 ? bumpers.ToList() : clips(roll.Sources);
    }

    /// <summary>The station's own excluded tags plus the global list when the station has not opted out.</summary>
    public static List<string> EffectiveExcludedTags(Station station, IEnumerable<string>? globalExcludedTags)
    {
        var all = new List<string>(station.ExcludedTags ?? new List<string>());
        if (station.UseGlobalExcludedTags && globalExcludedTags is not null)
        {
            all.AddRange(globalExcludedTags);
        }

        return all.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Drops items on the station's exclusion list, recording each once as <see cref="ExclusionReason.UserExcluded"/>
    /// and keeping its snapshot in <see cref="CompiledStation.UserExcluded"/> for the content view.
    /// </summary>
    public static List<PlayableItem> ApplyExclusions(List<PlayableItem> items, HashSet<Guid> excludedIds, CompiledStation compiled)
    {
        if (excludedIds.Count == 0)
        {
            return items;
        }

        var kept = new List<PlayableItem>(items.Count);
        foreach (var item in items)
        {
            if (!excludedIds.Contains(item.ItemId))
            {
                kept.Add(item);
                continue;
            }

            if (compiled.UserExcluded.All(u => u.ItemId != item.ItemId))
            {
                compiled.UserExcluded.Add(item);
                compiled.Excluded.Add(new ExcludedItem { ItemId = item.ItemId, Name = item.DisplayTitle, Reason = ExclusionReason.UserExcluded });
            }
        }

        return kept;
    }

    /// <summary>Drops items shorter than the station minimum, recording each once as <see cref="ExclusionReason.TooShort"/>.</summary>
    public static List<PlayableItem> DropShort(List<PlayableItem> items, int minMinutes, CompiledStation compiled)
    {
        if (minMinutes <= 0)
        {
            return items;
        }

        var minTicks = TimeSpan.FromMinutes(minMinutes).Ticks;
        var kept = new List<PlayableItem>(items.Count);
        foreach (var item in items)
        {
            if (item.RuntimeTicks >= minTicks)
            {
                kept.Add(item);
            }
            else if (compiled.Excluded.All(e => e.ItemId != item.ItemId))
            {
                compiled.Excluded.Add(new ExcludedItem { ItemId = item.ItemId, Name = item.DisplayTitle, Reason = ExclusionReason.TooShort });
            }
        }

        return kept;
    }

    /// <summary>True if two snapshots reference the same items in the same structure.</summary>
    public static bool SameContent(CompiledStation a, CompiledStation b)
    {
        static IEnumerable<Guid> Ids(IEnumerable<PlayableItem> items) => items.Select(i => i.ItemId);

        if (!Ids(a.Pool).SequenceEqual(Ids(b.Pool)) || !Ids(a.Lineup).SequenceEqual(Ids(b.Lineup)) || !Ids(a.Filler).SequenceEqual(Ids(b.Filler)) || !Ids(a.Bumpers).SequenceEqual(Ids(b.Bumpers)) || !Ids(a.PreRoll).SequenceEqual(Ids(b.PreRoll)) || !Ids(a.PostRoll).SequenceEqual(Ids(b.PostRoll)))
        {
            return false;
        }

        if (a.Blocks.Count != b.Blocks.Count)
        {
            return false;
        }

        if (!a.Music.Select(t => (t.Id, t.RuntimeTicks)).SequenceEqual(b.Music.Select(t => (t.Id, t.RuntimeTicks))))
        {
            return false;
        }

        foreach (var kv in a.Blocks)
        {
            if (!b.Blocks.TryGetValue(kv.Key, out var other) || !Ids(kv.Value).SequenceEqual(Ids(other)))
            {
                return false;
            }
        }

        // Every section's ids already match, so comparing the runtimes in one sequence is aligned item by item.
        return a.ProgramItems.Select(i => i.RuntimeTicks).SequenceEqual(b.ProgramItems.Select(i => i.RuntimeTicks));
    }
}
