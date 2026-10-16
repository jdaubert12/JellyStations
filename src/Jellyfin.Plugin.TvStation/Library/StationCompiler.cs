using Jellyfin.Plugin.TvStation.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Library;

/// <summary>Turns a Station definition into a frozen CompiledStation snapshot.</summary>
public sealed class StationCompiler
{
    private readonly LibraryContentResolver _resolver;
    private readonly ILogger<StationCompiler> _logger;

    public StationCompiler(LibraryContentResolver resolver, ILogger<StationCompiler> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public CompiledStation Compile(Station station, EncodingProfile effectiveProfile, IReadOnlyCollection<string>? globalExcludedTags = null)
    {
        var lang = effectiveProfile.AudioLanguagePreference ?? string.Empty;
        var tags = EffectiveExcludedTags(station, globalExcludedTags);
        var compiled = new CompiledStation
        {
            StationId = station.Id,
            Revision = station.Revision,
            CompiledUtc = DateTime.UtcNow
        };

        var excludedIds = new HashSet<Guid>(station.ExcludedItemIds ?? new List<Guid>());
        List<PlayableItem> Expand(List<ContentSource> sources) => _resolver.Expand(sources, lang, compiled.Excluded, tags, compiled.TagExcluded);
        void CompileBlocks()
        {
            foreach (var block in station.Slots.Blocks)
            {
                compiled.Blocks[block.Id] = DropShort(ApplyExclusions(Expand(block.Sources), excludedIds, compiled), station.MinRuntimeMinutes, compiled);
            }
        }

        switch (station.Mode)
        {
            case ScheduleMode.Pool:
                compiled.Pool = DropShort(ApplyExclusions(Expand(station.Pool.Sources), excludedIds, compiled), station.MinRuntimeMinutes, compiled);
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
                compiled.Filler = DropShort(ApplyExclusions(Expand(station.Slots.Filler.Sources), excludedIds, compiled), station.MinRuntimeMinutes, compiled);
                break;
        }

        if (station.Bumpers?.Enabled == true && station.Bumpers.Sources.Count > 0)
        {
            var maxTicks = TimeSpan.FromMinutes(Math.Max(1, station.Bumpers.MaxClipMinutes)).Ticks;
            compiled.Bumpers = ApplyExclusions(Expand(station.Bumpers.Sources), excludedIds, compiled)
                .Where(i => i.RuntimeTicks > 0 && i.RuntimeTicks <= maxTicks)
                .ToList();
        }

        if (station.Bumpers?.Enabled == true)
        {
            var maxTicks = TimeSpan.FromMinutes(Math.Max(1, station.Bumpers.MaxClipMinutes)).Ticks;
            compiled.PreRoll = CompileRoll(station.Bumpers.PreRoll, compiled.Bumpers, maxTicks, Expand, excludedIds, compiled);
            compiled.PostRoll = CompileRoll(station.Bumpers.PostRoll, compiled.Bumpers, maxTicks, Expand, excludedIds, compiled);
        }

        // Same item may be excluded via several sources; report once.
        compiled.Excluded = compiled.Excluded
            .GroupBy(e => e.ItemId)
            .Select(g => g.First())
            .ToList();

        _logger.LogInformation(
            "TvStation: compiled station {Name} rev {Revision}: {Items} item(s), {Excluded} excluded",
            station.Name,
            station.Revision,
            compiled.TotalItemCount,
            compiled.Excluded.Count);

        return compiled;
    }

    /// <summary>The clips for a pre/post-roll position: its own sources when it has any, otherwise the main bumper list.</summary>
    private static List<PlayableItem> CompileRoll(BumperRollConfig? roll, List<PlayableItem> bumpers, long maxTicks, Func<List<ContentSource>, List<PlayableItem>> expand, HashSet<Guid> excludedIds, CompiledStation compiled)
    {
        if (roll?.Enabled != true)
        {
            return new List<PlayableItem>();
        }

        if (roll.Sources.Count == 0)
        {
            return bumpers.ToList();
        }

        return ApplyExclusions(expand(roll.Sources), excludedIds, compiled)
            .Where(i => i.RuntimeTicks > 0 && i.RuntimeTicks <= maxTicks)
            .ToList();
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

        foreach (var kv in a.Blocks)
        {
            if (!b.Blocks.TryGetValue(kv.Key, out var other) || !Ids(kv.Value).SequenceEqual(Ids(other)))
            {
                return false;
            }
        }

        return a.Pool.Concat(a.Lineup).Concat(a.Filler).Concat(a.Blocks.Values.SelectMany(x => x)).Select(i => i.RuntimeTicks)
            .SequenceEqual(b.Pool.Concat(b.Lineup).Concat(b.Filler).Concat(b.Blocks.Values.SelectMany(x => x)).Select(i => i.RuntimeTicks));
    }
}
