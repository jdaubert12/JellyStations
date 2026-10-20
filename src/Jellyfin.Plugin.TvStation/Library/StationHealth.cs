using Jellyfin.Plugin.TvStation.Api.Dto;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Library;

/// <summary>
/// Pure health check over a station and its last compile: sources that expanded to nothing, stations with
/// less than a day of unique content, and items skipped for reasons the user did not choose.
/// </summary>
public static class StationHealth
{
    /// <summary>Below this much distinct programming a station repeats within a day.</summary>
    public const double MinUniqueHours = 24;

    /// <summary>Exclusion reasons that point at a library or file problem rather than a choice the user made.</summary>
    private static readonly ExclusionReason[] SkipReasons =
    {
        ExclusionReason.NoRuntime, ExclusionReason.NoPath, ExclusionReason.NotFound, ExclusionReason.NotVideo
    };

    public static StationHealthDto Build(Station station, CompiledStation? compiled, double minUniqueHours = MinUniqueHours)
    {
        var dto = new StationHealthDto
        {
            StationId = station.Id,
            Name = station.Name,
            ChannelNumber = station.ChannelNumber,
            Enabled = station.Enabled
        };

        if (compiled is null)
        {
            dto.Warnings.Add("Not compiled yet: save the station or press Refresh content.");
            return dto;
        }

        dto.CompiledUtc = DateTime.SpecifyKind(compiled.CompiledUtc, DateTimeKind.Utc);
        dto.UniqueHours = UniqueHours(compiled);
        foreach (var group in compiled.Excluded.GroupBy(e => e.Reason))
        {
            dto.ExcludedByReason[group.Key.ToString()] = group.Count();
        }

        foreach (var source in compiled.Sources.Where(s => s.IsEmpty))
        {
            dto.EmptySources.Add(source.Section + ": " + source.Label);
            dto.Warnings.Add(source.Found == 0
                ? source.Section + ": " + source.Label + " matched nothing in the library."
                : source.Section + ": " + source.Label + " found " + source.Found + " item(s) but none were usable (excluded, no runtime, or already listed by an earlier source).");
        }

        if (compiled.TotalItemCount == 0)
        {
            dto.Warnings.Add("No playable items: the station shows a card until content is added.");
        }
        else if (dto.UniqueHours < minUniqueHours)
        {
            dto.Warnings.Add("Only " + dto.UniqueHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                + " h of unique content (under a day): programs repeat within 24 hours.");
        }

        foreach (var reason in SkipReasons)
        {
            if (dto.ExcludedByReason.TryGetValue(reason.ToString(), out var count) && count > 0)
            {
                dto.Warnings.Add(count + " item" + (count == 1 ? string.Empty : "s") + " skipped: " + Describe(reason) + ".");
            }
        }

        return dto;
    }

    /// <summary>Hours of distinct programming (pool, lineup, blocks and filler; each item once; bumper clips not counted).</summary>
    public static double UniqueHours(CompiledStation compiled)
    {
        var seen = new HashSet<Guid>();
        long ticks = 0;
        foreach (var item in compiled.Pool.Concat(compiled.Lineup).Concat(compiled.Blocks.Values.SelectMany(b => b)).Concat(compiled.Filler))
        {
            if (seen.Add(item.ItemId))
            {
                ticks += item.RuntimeTicks;
            }
        }

        return Math.Round((double)ticks / TimeSpan.TicksPerHour, 1);
    }

    public static string Describe(ExclusionReason reason) => reason switch
    {
        ExclusionReason.NoRuntime => "no runtime in Jellyfin (refresh the item's metadata)",
        ExclusionReason.NoPath => "no file path",
        ExclusionReason.NotFound => "no longer in the library",
        ExclusionReason.NotVideo => "not a video",
        ExclusionReason.UserExcluded => "excluded on this station",
        ExclusionReason.TagExcluded => "excluded by tag",
        ExclusionReason.TooShort => "shorter than the station's minimum runtime",
        _ => reason.ToString()
    };
}
