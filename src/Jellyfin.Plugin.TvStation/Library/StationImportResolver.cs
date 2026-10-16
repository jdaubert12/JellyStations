using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TvStation.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.TvStation.Library;

/// <summary>What happened to the item references of an imported station.</summary>
public sealed class ImportResolution
{
    /// <summary>References whose id was unknown here and that were matched by name.</summary>
    public List<string> Resolved { get; } = new();

    /// <summary>References that could not be matched and were dropped.</summary>
    public List<string> Unmatched { get; } = new();
}

/// <summary>
/// Makes an exported station usable on another server: every item reference whose id does not exist here is
/// looked up by the display name stored beside it (see <see cref="SourceNameMatcher"/>); what cannot be matched is
/// dropped and reported. Genres and tags already reference by name.
/// </summary>
public sealed class StationImportResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryContentResolver _resolver;

    public StationImportResolver(ILibraryManager libraryManager, LibraryContentResolver resolver)
    {
        _libraryManager = libraryManager;
        _resolver = resolver;
    }

    public ImportResolution Resolve(Station draft)
    {
        var result = new ImportResolution();
        FixSources(draft.Pool?.Sources, "pool", result);
        FixSources(draft.Slots?.Filler?.Sources, "filler", result);
        foreach (var block in draft.Slots?.Blocks ?? new List<SlotBlock>())
        {
            FixSources(block.Sources, "block \"" + (string.IsNullOrWhiteSpace(block.Name) ? "Block" : block.Name) + "\"", result);
        }

        FixSources(draft.Bumpers?.Sources, "bumpers", result);
        FixSources(draft.Bumpers?.PreRoll?.Sources, "pre-roll", result);
        FixSources(draft.Bumpers?.PostRoll?.Sources, "post-roll", result);
        FixLineup(draft.Lineup?.Items, result);

        if (draft.ExcludedItemIds is { Count: > 0 })
        {
            var known = draft.ExcludedItemIds.Where(Exists).ToList();
            var dropped = draft.ExcludedItemIds.Count - known.Count;
            if (dropped > 0)
            {
                draft.ExcludedItemIds = known;
                result.Unmatched.Add($"{dropped} excluded item id(s) unknown on this server were dropped");
            }
        }

        return result;
    }

    private void FixSources(List<ContentSource>? sources, string where, ImportResolution result)
    {
        if (sources is null)
        {
            return;
        }

        for (var i = sources.Count - 1; i >= 0; i--)
        {
            var source = sources[i];
            var name = source.DisplayName ?? string.Empty;
            if (source.Kind is SourceKind.Genre or SourceKind.Tag)
            {
                if (source.LibraryId.HasValue && !Exists(source.LibraryId.Value))
                {
                    source.LibraryId = null;
                    result.Unmatched.Add($"{where}: library restriction on {Describe(source)} dropped (library not found)");
                }

                continue;
            }

            if (source.ItemId.HasValue && Exists(source.ItemId.Value))
            {
                continue;
            }

            var id = string.IsNullOrWhiteSpace(name) ? null : Find(source.Kind, name);
            if (id.HasValue)
            {
                source.ItemId = id;
                result.Resolved.Add($"{where}: {Describe(source)} matched by name");
            }
            else
            {
                sources.RemoveAt(i);
                result.Unmatched.Add($"{where}: {Describe(source)} not found");
            }
        }
    }

    private void FixLineup(List<LineupItem>? items, ImportResolution result)
    {
        if (items is null)
        {
            return;
        }

        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item.ItemId != Guid.Empty && Exists(item.ItemId))
            {
                continue;
            }

            var name = item.DisplayName ?? string.Empty;
            var kind = SourceNameMatcher.LooksLikeEpisode(name) ? SourceKind.Episode : SourceKind.Movie;
            var id = string.IsNullOrWhiteSpace(name) ? null : Find(kind, name) ?? (kind == SourceKind.Movie ? Find(SourceKind.Episode, name) : null);
            if (id.HasValue)
            {
                item.ItemId = id.Value;
                result.Resolved.Add($"lineup: \"{name}\" matched by name");
            }
            else
            {
                items.RemoveAt(i);
                result.Unmatched.Add($"lineup: \"{(string.IsNullOrWhiteSpace(name) ? item.ItemId.ToString("N") : name)}\" not found");
            }
        }
    }

    private static string Describe(ContentSource s)
    {
        var label = s.DisplayName ?? s.Genre ?? s.Tag ?? s.ItemId?.ToString("N") ?? "?";
        return s.Kind.ToString().ToLowerInvariant() + " \"" + label + "\"";
    }

    private bool Exists(Guid id)
    {
        try
        {
            return id != Guid.Empty && _libraryManager.GetItemById(id) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The id of the item a display name refers to on this server, or null when none or several match.</summary>
    public Guid? Find(SourceKind kind, string displayName)
    {
        try
        {
            var parsed = SourceNameMatcher.Parse(kind, displayName);
            switch (kind)
            {
                case SourceKind.Movie:
                    return SourceNameMatcher.Pick(Query(BaseItemKind.Movie, parsed.Name), parsed);
                case SourceKind.Series:
                    return SourceNameMatcher.Pick(Query(BaseItemKind.Series, parsed.Name), parsed);
                case SourceKind.BoxSet:
                    return SourceNameMatcher.Pick(Query(BaseItemKind.BoxSet, parsed.Name), parsed);
                case SourceKind.Playlist:
                    return SourceNameMatcher.Pick(Query(BaseItemKind.Playlist, parsed.Name), parsed);
                case SourceKind.Library:
                    return SourceNameMatcher.Pick(
                        _libraryManager.GetVirtualFolders().Where(v => Guid.TryParse(v.ItemId, out _)).Select(v => new NameCandidate(Guid.Parse(v.ItemId), v.Name)),
                        parsed);
                case SourceKind.Season:
                    return FindSeason(parsed);
                case SourceKind.Episode:
                    return FindEpisode(parsed);
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Guid? FindSeason(ParsedSourceName parsed)
    {
        var episodes = EpisodesOfSeries(parsed.SeriesName);
        if (episodes is null)
        {
            return null;
        }

        var seasons = episodes
            .Where(e => e.SeasonId != Guid.Empty)
            .GroupBy(e => e.SeasonId)
            .Select(g => new NameCandidate(g.Key, SeasonName(g.First()), null, g.First().SeriesName, g.First().ParentIndexNumber))
            .ToList();

        var byName = SourceNameMatcher.Pick(seasons, parsed);
        if (byName.HasValue || !parsed.Season.HasValue)
        {
            return byName;
        }

        // Season folder named differently here ("Season 02", "Specials"): the number alone decides.
        var byNumber = seasons.Where(s => s.Season == parsed.Season).ToList();
        return byNumber.Count == 1 ? byNumber[0].Id : null;
    }

    private Guid? FindEpisode(ParsedSourceName parsed)
    {
        var episodes = EpisodesOfSeries(parsed.SeriesName);
        if (episodes is null)
        {
            return null;
        }

        return SourceNameMatcher.Pick(
            episodes.Select(e => new NameCandidate(e.Id, e.Name ?? string.Empty, null, e.SeriesName, e.ParentIndexNumber, e.IndexNumber)),
            parsed);
    }

    private List<Episode>? EpisodesOfSeries(string? seriesName)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var seriesId = Find(SourceKind.Series, seriesName);
        return seriesId.HasValue ? _resolver.EpisodesOfSeries(seriesId.Value).OfType<Episode>().ToList() : null;
    }

    private static string SeasonName(Episode e)
    {
        try
        {
            var name = e.Season?.Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch (Exception)
        {
            // fall through
        }

        return e.ParentIndexNumber == 0 ? "Specials" : "Season " + e.ParentIndexNumber;
    }

    private IEnumerable<NameCandidate> Query(BaseItemKind kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Array.Empty<NameCandidate>();
        }

        // SearchTerm is cleaned (lower-cased, diacritics removed) by Jellyfin before matching; NameContains is not.
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            SearchTerm = name,
            Limit = 100,
            DtoOptions = new DtoOptions(false)
        }).Select(i => new NameCandidate(i.Id, i.Name ?? string.Empty, i.ProductionYear));
    }
}
