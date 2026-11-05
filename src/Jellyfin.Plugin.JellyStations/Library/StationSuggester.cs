using System.Globalization;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>What the rules made of a library: the stations they propose and the ones that fell just short.</summary>
public sealed class SuggestionSet
{
    public List<StationSuggestionDto> Suggestions { get; } = new();

    public List<NearMissDto> NearMisses { get; set; } = new();
}

/// <summary>
/// Turns a <see cref="LibraryAnalysisDto"/> into ready-to-create stations: genre channels (a movie channel and a
/// TV channel when a genre has enough of both), series channels, a kids channel and family movies by rating,
/// theme channels (anime, documentaries) from a genre or a tag, a "Comfort Reruns" channel of half-hour comedies,
/// decade channels for movies and for shows and a classics channel, franchise marathons and a "Franchise Nights"
/// slots station, collection channels, and seasonal channels from holiday tags. A rule that has at least
/// <see cref="NearMissShare"/> of what it needs reports a near miss instead. Clip libraries (see
/// <see cref="LibraryStatDto.ClipLibrary"/>) are never a source. Pure, so the rules are testable.
/// </summary>
public static class StationSuggester
{
    /// <summary>A rule with this share of its threshold, but not all of it, is reported as a near miss.</summary>
    public const double NearMissShare = 0.5;

    /// <summary>The most near misses reported, closest first.</summary>
    public const int MaxNearMisses = 20;

    /// <summary>A comedy whose episodes average this many minutes or fewer is a half-hour show.</summary>
    public const int ComfortMaxEpisodeMinutes = 35;

    /// <summary>A show needs this many episodes to be worth rerunning.</summary>
    public const int ComfortMinEpisodes = 5;

    private const string KidsFamily = "Kids & family";

    /// <summary>Channels one genre name does not describe: every genre and tag spelling that means the same thing.</summary>
    private static readonly (string Key, string Name, string[] Genres, string[] Tags)[] Themes =
    {
        ("anime", "Anime", new[] { "Anime" }, new[] { "anime" }),
        ("documentary", "Documentaries", new[] { "Documentary", "Documentaries" }, new[] { "documentary", "documentaries", "docuseries", "nature documentary" })
    };

    private static readonly (string Window, string Label, int StartMonth, int StartDay, int EndMonth, int EndDay, string[] Tags)[] Seasons =
    {
        ("Holiday", "Holiday season", 12, 1, 1, 6, new[] { "christmas", "xmas", "holiday", "holidays", "christmas special" }),
        ("Halloween", "Halloween", 10, 1, 10, 31, new[] { "halloween", "spooky" }),
        ("Thanksgiving", "Thanksgiving", 11, 15, 11, 30, new[] { "thanksgiving" }),
        ("Valentine's", "Valentine's", 2, 1, 2, 14, new[] { "valentine", "valentines", "valentine's day", "valentines day" }),
        ("Easter", "Easter", 3, 20, 4, 30, new[] { "easter" })
    };

    public static List<StationSuggestionDto> Suggest(LibraryAnalysisDto a, IEnumerable<string> existingNames, SuggestionThresholds? thresholds = null)
        => Build(a, existingNames, thresholds).Suggestions;

    public static SuggestionSet Build(LibraryAnalysisDto a, IEnumerable<string> existingNames, SuggestionThresholds? thresholds = null)
    {
        var t = thresholds ?? new SuggestionThresholds();
        var existing = new HashSet<string>(existingNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var set = new SuggestionSet();
        var result = set.Suggestions;
        var near = new List<NearMissDto>();

        void Add(string key, string category, string name, string description, double hours, Station station)
        {
            station.Name = name;
            result.Add(new StationSuggestionDto
            {
                Key = key,
                Category = category,
                Name = name,
                Description = description,
                Hours = Math.Round(hours, 1),
                NewHours = Math.Round(hours, 1),
                Exists = existing.Contains(name),
                Station = station
            });
        }

        // What a rule had when it fell short, kept when it is at least half way there.
        void Near(string category, string name, double have, double need, string unit)
        {
            if (have > 0 && have < need && have >= need * NearMissShare && !existing.Contains(name))
            {
                near.Add(new NearMissDto { Category = category, Name = name, Have = Math.Round(have, 1), Need = Math.Round(need, 1), Unit = unit });
            }
        }

        // A library of idents and bumps is a bumper source, never a channel's content.
        var libraries = a.Libraries.Where(l => !l.ClipLibrary && l.Movies + l.Episodes > 0).ToList();
        var movieLibraries = libraries.Where(l => l.Movies > 0).ToList();

        // Kids and family, by rating across every library.
        if (libraries.Count > 0)
        {
            if (a.KidsHours >= t.KidsMinHours)
            {
                var station = Pool(PlayOrder.ShuffleEpisodesInOrder, libraries.Select(l => LibrarySource(l, null, "G")));
                Add("kids", KidsFamily, "Kids TV", "Everything rated G, TV-G or below, shows and movies mixed, each show's episodes in order.", a.KidsHours, station);
            }
            else
            {
                Near(KidsFamily, "Kids TV", a.KidsHours, t.KidsMinHours, "h");
            }
        }

        var familyMovieHours = a.Ratings.Where(r => (RatingLadder.Rank(r.Rating) ?? int.MaxValue) <= 3).Sum(r => r.MovieHours);
        if (movieLibraries.Count > 0)
        {
            if (familyMovieHours >= t.FamilyMoviesMinHours)
            {
                var station = Pool(PlayOrder.Shuffle, movieLibraries.Select(l => LibrarySource(l, SourceKind.Movie, "PG")));
                station.PaddingByLength = true;
                Add("family-movies", KidsFamily, "Family Movies", "Movies rated PG or below, shuffled.", familyMovieHours, station);
            }
            else
            {
                Near(KidsFamily, "Family Movies", familyMovieHours, t.FamilyMoviesMinHours, "h");
            }
        }

        // Themes: one channel from every genre and tag that spell the same thing. A theme that is only a single
        // genre is left to the genre rule; one that takes genres keeps them from being offered twice. They are
        // worked out first for that reason and listed after the genres.
        var themed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var themeStations = new List<(string Key, string Name, string Description, double Hours, Station Station)>();
        foreach (var theme in Themes)
        {
            var genres = a.Genres.Where(g => theme.Genres.Contains(g.Name.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            var tags = a.Tags.Where(tag => theme.Tags.Contains(tag.Name.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            if (tags.Count == 0 && genres.Count < 2)
            {
                continue;
            }

            // The genres and tags overlap, so the largest single one is the floor of what the channel plays.
            var hours = genres.Select(g => g.Hours).Concat(tags.Select(tag => tag.Hours)).Max();
            if (hours < t.GenreMinHours)
            {
                Near("Genre", theme.Name, hours, t.GenreMinHours, "h");
                continue;
            }

            foreach (var g in genres)
            {
                themed.Add(g.Name);
            }

            var sources = genres.Select(g => GenreSource(g.Name, null))
                .Concat(tags.Select(tag => new ContentSource { Kind = SourceKind.Tag, Tag = tag.Name, DisplayName = tag.Name }));
            var station = Pool(PlayOrder.ShuffleEpisodesInOrder, sources);
            station.PaddingByLength = genres.Any(g => g.Movies > 0) || tags.Any(tag => tag.Movies > 0);
            var from = genres.Select(g => "the genre \"" + g.Name + "\"").Concat(tags.Select(tag => "the tag \"" + tag.Name + "\""));
            themeStations.Add(("theme:" + theme.Key, theme.Name, "Everything under " + string.Join(" or ", from) + ", shuffled, each show's episodes in order.", hours, station));
        }

        // Genres. One with enough movies and enough episodes on their own becomes a movie channel and a TV channel.
        foreach (var g in a.Genres.Where(g => !themed.Contains(g.Name)))
        {
            if (g.MovieHours >= t.GenreMinHours && g.EpisodeHours >= t.GenreMinHours)
            {
                var movieStation = Pool(PlayOrder.Shuffle, new[] { GenreSource(g.Name, SourceKind.Movie) });
                movieStation.PaddingByLength = true;
                Add("genre-movies:" + g.Name, "Genre", g.Name + " Movies", Count(g.Movies, "movie") + ", shuffled.", g.MovieHours, movieStation);
                var tvStation = Pool(PlayOrder.ShuffleEpisodesInOrder, new[] { GenreSource(g.Name, SourceKind.Episode) });
                Add("genre-tv:" + g.Name, "Genre", g.Name + " TV", Count(g.Episodes, "episode") + " from " + Count(g.Series, "series", "series") + ", each show's episodes in order.", g.EpisodeHours, tvStation);
                continue;
            }

            if (g.Hours < t.GenreMinHours)
            {
                Near("Genre", g.Name, g.Hours, t.GenreMinHours, "h");
                continue;
            }

            var station = Pool(g.Episodes > 0 ? PlayOrder.ShuffleEpisodesInOrder : PlayOrder.Shuffle, new[] { GenreSource(g.Name, null) });
            station.PaddingByLength = g.Movies > 0;
            var parts = new List<string>();
            if (g.Movies > 0)
            {
                parts.Add(Count(g.Movies, "movie"));
            }

            if (g.Episodes > 0)
            {
                parts.Add(Count(g.Episodes, "episode") + " from " + Count(g.Series, "series", "series"));
            }

            Add("genre:" + g.Name, "Genre", g.Name, string.Join(" and ", parts) + ".", g.Hours, station);
        }

        foreach (var theme in themeStations)
        {
            Add(theme.Key, "Genre", theme.Name, theme.Description, theme.Hours, theme.Station);
        }

        // Series with enough episodes for a channel of their own.
        foreach (var s in a.Series)
        {
            if (s.Episodes < t.SeriesMinEpisodes)
            {
                Near("Series", s.Name, s.Episodes, t.SeriesMinEpisodes, "episodes");
                continue;
            }

            var station = Pool(PlayOrder.Sequential, new[] { SeriesSource(s) });
            station.Pool.AvoidSameSeriesBackToBack = false;
            var desc = Count(s.Episodes, "episode") + (s.Seasons > 0 ? " over " + Count(s.Seasons, "season") : string.Empty) + ", in order" + (s.Rating is null ? string.Empty : ", rated " + s.Rating) + ".";
            Add("series:" + s.Id.ToString("N"), "Series", s.Name, desc, s.Hours, station);
        }

        // Comfort reruns: the half-hour comedies together, whether or not each could carry a channel alone.
        var comfort = a.Series.Where(IsComfortRerun).ToList();
        var comfortHours = comfort.Sum(s => s.Hours);
        if (comfort.Count >= t.ComfortMinSeries && comfortHours >= t.ComfortMinHours)
        {
            var station = Pool(PlayOrder.ShuffleEpisodesInOrder, comfort.Select(SeriesSource));
            Add("comfort-reruns", "Series", "Comfort Reruns", Count(comfort.Count, "half-hour comedy", "half-hour comedies") + " (" + Names(comfort.Select(s => s.Name)) + "), shuffled, each show's episodes in order.", comfortHours, station);
        }
        else if (comfort.Count >= t.ComfortMinSeries)
        {
            Near("Series", "Comfort Reruns", comfortHours, t.ComfortMinHours, "h");
        }
        else
        {
            Near("Series", "Comfort Reruns", comfort.Count, t.ComfortMinSeries, "half-hour comedies");
        }

        // Franchises: a marathon lineup each, plus one slots station with a franchise per night.
        var franchises = a.Franchises.Where(f => f.ItemIds.Count >= 2).OrderByDescending(f => f.Hours).ToList();
        foreach (var f in franchises)
        {
            if (f.ItemIds.Count < t.FranchiseMinTitles)
            {
                Near("Franchise", f.Name + " Marathon", f.ItemIds.Count, t.FranchiseMinTitles, "titles");
                continue;
            }

            var station = new Station { Mode = ScheduleMode.Lineup, PaddingByLength = true };
            for (var i = 0; i < f.ItemIds.Count; i++)
            {
                station.Lineup.Items.Add(new LineupItem { ItemId = f.ItemIds[i], DisplayName = i < f.Titles.Count ? f.Titles[i] : null });
            }

            Add("franchise:" + f.Name, "Franchise", f.Name + " Marathon", string.Join(", ", f.Titles) + ", in release order, looping.", f.Hours, station);
        }

        if (franchises.Count >= 2 && movieLibraries.Count > 0)
        {
            var station = new Station { Mode = ScheduleMode.Slots, PaddingByLength = true };
            station.Slots.Filler.Sources.AddRange(movieLibraries.Select(l => LibrarySource(l, SourceKind.Movie, null)));
            station.Slots.Filler.Order = PlayOrder.Shuffle;
            var days = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
            var nights = new List<string>();
            foreach (var (f, i) in franchises.Take(7).Select((f, i) => (f, i)))
            {
                var minutes = (int)Math.Min(360, Math.Ceiling(f.Hours * 60 / 30) * 30);
                var block = new SlotBlock
                {
                    Name = f.Name,
                    Days = new List<DayOfWeek> { days[i] },
                    StartLocal = "19:00",
                    DurationMinutes = Math.Max(30, minutes),
                    Order = PlayOrder.Sequential,
                    Overflow = SlotOverflow.FillerRemainder,
                    RemainderMayOverrun = true,
                    MaxStartDelayMinutes = 30
                };
                for (var j = 0; j < f.ItemIds.Count; j++)
                {
                    block.Sources.Add(new ContentSource { Kind = SourceKind.Movie, ItemId = f.ItemIds[j], DisplayName = j < f.Titles.Count ? f.Titles[j] : null });
                }

                station.Slots.Blocks.Add(block);
                nights.Add(days[i].ToString()[..3] + " " + f.Name);
            }

            Add("franchise-nights", "Franchise", "Franchise Nights", "One franchise per night from 19:00 (" + string.Join(", ", nights) + "), shuffled movies in between.", franchises.Take(7).Sum(f => f.Hours), station);
        }

        // Collections.
        foreach (var c in a.Collections.Where(c => c.Items >= t.CollectionMinItems))
        {
            var source = new ContentSource { Kind = SourceKind.BoxSet, ItemId = c.Id, DisplayName = c.Name };
            var station = Pool(PlayOrder.Sequential, new[] { source });
            station.PaddingByLength = true;
            Add("collection:" + c.Id.ToString("N"), "Collection", c.Name, Count(c.Items, "item") + " in collection order.", c.Hours, station);
        }

        // Decades of movies.
        foreach (var d in a.Decades.Where(d => movieLibraries.Count > 0))
        {
            if (d.Hours < t.DecadeMinHours)
            {
                Near("Decade", DecadeName(d.Decade) + " Movies", d.Hours, t.DecadeMinHours, "h");
                continue;
            }

            var station = Pool(PlayOrder.Shuffle, movieLibraries.Select(l =>
            {
                var s = LibrarySource(l, SourceKind.Movie, null);
                s.YearFrom = d.Decade;
                s.YearTo = d.Decade + 9;
                return s;
            }));
            station.PaddingByLength = true;
            Add("decade:" + d.Decade.ToString(CultureInfo.InvariantCulture), "Decade", DecadeName(d.Decade) + " Movies", Count(d.Movies, "movie") + " from " + d.Decade.ToString(CultureInfo.InvariantCulture) + " to " + (d.Decade + 9).ToString(CultureInfo.InvariantCulture) + ", shuffled.", d.Hours, station);
        }

        // Classics: every movie made before the cut-off year, which no single decade may reach on its own.
        var classics = a.Decades.Where(d => d.Decade < t.ClassicsBeforeYear).ToList();
        var classicHours = classics.Sum(d => d.Hours);
        if (classics.Count > 0 && movieLibraries.Count > 0)
        {
            if (classicHours >= t.ClassicsMinHours)
            {
                var lastYear = t.ClassicsBeforeYear - 1;
                var station = Pool(PlayOrder.Shuffle, movieLibraries.Select(l =>
                {
                    var s = LibrarySource(l, SourceKind.Movie, null);
                    s.YearTo = lastYear;
                    return s;
                }));
                station.PaddingByLength = true;
                Add("era:classics", "Decade", "Classic Movies", Count(classics.Sum(d => d.Movies), "movie") + " from before " + t.ClassicsBeforeYear.ToString(CultureInfo.InvariantCulture) + ", shuffled.", classicHours, station);
            }
            else
            {
                Near("Decade", "Classic Movies", classicHours, t.ClassicsMinHours, "h");
            }
        }

        // Decades of television: the shows that began in one, when there are at least two of them.
        foreach (var d in a.Series.Where(s => s.Year.HasValue).GroupBy(s => s.Year!.Value / 10 * 10).OrderBy(g => g.Key))
        {
            var shows = d.ToList();
            if (shows.Count < 2)
            {
                continue;
            }

            var hours = shows.Sum(s => s.Hours);
            if (hours < t.DecadeMinHours)
            {
                Near("Decade", DecadeName(d.Key) + " TV", hours, t.DecadeMinHours, "h");
                continue;
            }

            var station = Pool(PlayOrder.ShuffleEpisodesInOrder, shows.Select(SeriesSource));
            Add("decade-tv:" + d.Key.ToString(CultureInfo.InvariantCulture), "Decade", DecadeName(d.Key) + " TV", Count(shows.Count, "show") + " that began between " + d.Key.ToString(CultureInfo.InvariantCulture) + " and " + (d.Key + 9).ToString(CultureInfo.InvariantCulture) + " (" + Names(shows.Select(s => s.Name)) + "), each show's episodes in order.", hours, station);
        }

        // Seasonal channels from holiday tags, listed in the guide only during their window.
        foreach (var season in Seasons)
        {
            var tags = a.Tags.Where(tag => season.Tags.Contains(tag.Name.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            var hours = tags.Sum(tag => tag.Hours);
            if (tags.Count == 0)
            {
                continue;
            }

            if (hours < t.SeasonalMinHours)
            {
                Near("Seasonal", season.Window, hours, t.SeasonalMinHours, "h");
                continue;
            }

            var station = Pool(PlayOrder.Shuffle, tags.Select(tag => new ContentSource { Kind = SourceKind.Tag, Tag = tag.Name, DisplayName = tag.Name }));
            station.PaddingByLength = true;
            station.UseGlobalExcludedTags = false;
            station.ActiveWindows.Add(new SeasonWindow { Label = season.Label, StartMonth = season.StartMonth, StartDay = season.StartDay, EndMonth = season.EndMonth, EndDay = season.EndDay });
            var what = string.Join(", ", tags.Select(tag => Count(tag.Movies, "movie") + (tag.Series > 0 ? " and " + Count(tag.Series, "series", "series") : string.Empty) + " tagged \"" + tag.Name + "\""));
            Add("seasonal:" + season.Window, "Seasonal", season.Window, what + ". In the guide " + Dates(season.StartMonth, season.StartDay) + " to " + Dates(season.EndMonth, season.EndDay) + ".", hours, station);
        }

        set.NearMisses = near
            .OrderByDescending(n => n.Have / n.Need)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxNearMisses)
            .ToList();
        return set;
    }

    /// <summary>
    /// A comedy with enough episodes whose episodes run half an hour or less, and that is not a children's show
    /// (those belong on the kids channel).
    /// </summary>
    public static bool IsComfortRerun(SeriesStatDto s)
        => s.Episodes >= ComfortMinEpisodes
            && s.Genres.Any(g => string.Equals(g.Trim(), "Comedy", StringComparison.OrdinalIgnoreCase) || string.Equals(g.Trim(), "Sitcom", StringComparison.OrdinalIgnoreCase))
            && s.Hours * 60 / s.Episodes <= ComfortMaxEpisodeMinutes
            && !(RatingLadder.Rank(s.Rating) is { } rank && rank <= 2);

    private static Station Pool(PlayOrder order, IEnumerable<ContentSource> sources)
    {
        var station = new Station { Mode = ScheduleMode.Pool };
        station.Pool.Order = order;
        station.Pool.Sources.AddRange(sources);
        return station;
    }

    private static ContentSource LibrarySource(LibraryStatDto library, SourceKind? kind, string? maxRating)
    {
        var source = new ContentSource { Kind = SourceKind.Library, ItemId = library.Id, DisplayName = library.Name, MaxRating = maxRating };
        if (kind.HasValue)
        {
            source.ItemKinds.Add(kind.Value);
        }

        return source;
    }

    private static ContentSource GenreSource(string genre, SourceKind? kind)
    {
        var source = new ContentSource { Kind = SourceKind.Genre, Genre = genre, DisplayName = genre };
        if (kind.HasValue)
        {
            source.ItemKinds.Add(kind.Value);
        }

        return source;
    }

    private static ContentSource SeriesSource(SeriesStatDto s) => new() { Kind = SourceKind.Series, ItemId = s.Id, DisplayName = s.Name };

    private static string DecadeName(int decade) => decade.ToString(CultureInfo.InvariantCulture) + "s";

    /// <summary>The first few names and how many more there are.</summary>
    private static string Names(IEnumerable<string> names, int show = 5)
    {
        var list = names.ToList();
        return string.Join(", ", list.Take(show)) + (list.Count > show ? " and " + (list.Count - show).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty);
    }

    private static string Count(int n, string singular, string? plural = null)
        => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");

    private static string Dates(int month, int day)
        => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month) + " " + day.ToString(CultureInfo.InvariantCulture);
}
