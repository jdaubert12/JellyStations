using System.Globalization;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>Minimum content a rule needs before it proposes a station.</summary>
public sealed class SuggestionThresholds
{
    public double GenreMinHours { get; set; } = 8;

    public int SeriesMinEpisodes { get; set; } = 20;

    public double KidsMinHours { get; set; } = 4;

    public double FamilyMoviesMinHours { get; set; } = 6;

    public double DecadeMinHours { get; set; } = 8;

    /// <summary>Hours of movies made before <see cref="ClassicsBeforeYear"/> that earn a "Classic Movies" station.</summary>
    public double ClassicsMinHours { get; set; } = 8;

    /// <summary>First year that is no longer a classic.</summary>
    public int ClassicsBeforeYear { get; set; } = 1970;

    /// <summary>Titles a franchise needs for its own marathon station.</summary>
    public int FranchiseMinTitles { get; set; } = 3;

    public int CollectionMinItems { get; set; } = 2;

    public double SeasonalMinHours { get; set; } = 1;
}

/// <summary>
/// Turns a <see cref="LibraryAnalysisDto"/> into ready-to-create stations: genre channels, series channels, a kids
/// channel and family movies by rating, decade channels and a classics channel, franchise marathons and a
/// "Franchise Nights" slots station, collection channels, and seasonal channels from holiday tags. Pure, so the
/// rules are testable.
/// </summary>
public static class StationSuggester
{
    private const string KidsFamily = "Kids & family";

    private static readonly (string Window, string Label, int StartMonth, int StartDay, int EndMonth, int EndDay, string[] Tags)[] Seasons =
    {
        ("Holiday", "Holiday season", 12, 1, 1, 6, new[] { "christmas", "xmas", "holiday", "holidays", "christmas special" }),
        ("Halloween", "Halloween", 10, 1, 10, 31, new[] { "halloween", "spooky" }),
        ("Thanksgiving", "Thanksgiving", 11, 15, 11, 30, new[] { "thanksgiving" }),
        ("Valentine's", "Valentine's", 2, 1, 2, 14, new[] { "valentine", "valentines", "valentine's day", "valentines day" }),
        ("Easter", "Easter", 3, 20, 4, 30, new[] { "easter" })
    };

    public static List<StationSuggestionDto> Suggest(LibraryAnalysisDto a, IEnumerable<string> existingNames, SuggestionThresholds? thresholds = null)
    {
        var t = thresholds ?? new SuggestionThresholds();
        var existing = new HashSet<string>(existingNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var result = new List<StationSuggestionDto>();

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
                Exists = existing.Contains(name),
                Station = station
            });
        }

        var libraries = a.Libraries;
        var movieLibraries = libraries.Where(l => l.Movies > 0).ToList();

        // Kids and family, by rating across every library.
        if (a.KidsHours >= t.KidsMinHours && libraries.Count > 0)
        {
            var station = Pool(PlayOrder.ShuffleEpisodesInOrder, libraries.Select(l => LibrarySource(l, null, "G")));
            Add("kids", KidsFamily, "Kids TV", "Everything rated G, TV-G or below, shows and movies mixed, each show's episodes in order.", a.KidsHours, station);
        }

        var familyMovieHours = a.Ratings.Where(r => (RatingLadder.Rank(r.Rating) ?? int.MaxValue) <= 3).Sum(r => r.MovieHours);
        if (familyMovieHours >= t.FamilyMoviesMinHours && movieLibraries.Count > 0)
        {
            var station = Pool(PlayOrder.Shuffle, movieLibraries.Select(l => LibrarySource(l, SourceKind.Movie, "PG")));
            station.PaddingByLength = true;
            Add("family-movies", KidsFamily, "Family Movies", "Movies rated PG or below, shuffled.", familyMovieHours, station);
        }

        // Genres.
        foreach (var g in a.Genres.Where(g => g.Hours >= t.GenreMinHours))
        {
            var source = new ContentSource { Kind = SourceKind.Genre, Genre = g.Name, DisplayName = g.Name };
            var station = Pool(g.Episodes > 0 ? PlayOrder.ShuffleEpisodesInOrder : PlayOrder.Shuffle, new[] { source });
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

        // Series with enough episodes for a channel of their own.
        foreach (var s in a.Series.Where(s => s.Episodes >= t.SeriesMinEpisodes))
        {
            var source = new ContentSource { Kind = SourceKind.Series, ItemId = s.Id, DisplayName = s.Name };
            var station = Pool(PlayOrder.Sequential, new[] { source });
            station.Pool.AvoidSameSeriesBackToBack = false;
            var desc = Count(s.Episodes, "episode") + (s.Seasons > 0 ? " over " + Count(s.Seasons, "season") : string.Empty) + ", in order" + (s.Rating is null ? string.Empty : ", rated " + s.Rating) + ".";
            Add("series:" + s.Id.ToString("N"), "Series", s.Name, desc, s.Hours, station);
        }

        // Franchises: a marathon lineup each, plus one slots station with a franchise per night.
        var franchises = a.Franchises.Where(f => f.ItemIds.Count >= 2).OrderByDescending(f => f.Hours).ToList();
        foreach (var f in franchises.Where(f => f.ItemIds.Count >= t.FranchiseMinTitles))
        {
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

        // Decades.
        foreach (var d in a.Decades.Where(d => d.Hours >= t.DecadeMinHours))
        {
            var station = Pool(PlayOrder.Shuffle, movieLibraries.Select(l =>
            {
                var s = LibrarySource(l, SourceKind.Movie, null);
                s.YearFrom = d.Decade;
                s.YearTo = d.Decade + 9;
                return s;
            }));
            station.PaddingByLength = true;
            if (movieLibraries.Count > 0)
            {
                Add("decade:" + d.Decade.ToString(CultureInfo.InvariantCulture), "Decade", d.Decade.ToString(CultureInfo.InvariantCulture) + "s Movies", Count(d.Movies, "movie") + " from " + d.Decade.ToString(CultureInfo.InvariantCulture) + " to " + (d.Decade + 9).ToString(CultureInfo.InvariantCulture) + ", shuffled.", d.Hours, station);
            }
        }

        // Classics: every movie made before the cut-off year, which no single decade may reach on its own.
        var classics = a.Decades.Where(d => d.Decade < t.ClassicsBeforeYear).ToList();
        var classicHours = classics.Sum(d => d.Hours);
        if (classics.Count > 0 && classicHours >= t.ClassicsMinHours && movieLibraries.Count > 0)
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

        // Seasonal channels from holiday tags, listed in the guide only during their window.
        foreach (var season in Seasons)
        {
            var tags = a.Tags.Where(tag => season.Tags.Contains(tag.Name.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            var hours = tags.Sum(tag => tag.Hours);
            if (tags.Count == 0 || hours < t.SeasonalMinHours)
            {
                continue;
            }

            var station = Pool(PlayOrder.Shuffle, tags.Select(tag => new ContentSource { Kind = SourceKind.Tag, Tag = tag.Name, DisplayName = tag.Name }));
            station.PaddingByLength = true;
            station.UseGlobalExcludedTags = false;
            station.ActiveWindows.Add(new SeasonWindow { Label = season.Label, StartMonth = season.StartMonth, StartDay = season.StartDay, EndMonth = season.EndMonth, EndDay = season.EndDay });
            var what = string.Join(", ", tags.Select(tag => Count(tag.Movies, "movie") + (tag.Series > 0 ? " and " + Count(tag.Series, "series", "series") : string.Empty) + " tagged \"" + tag.Name + "\""));
            Add("seasonal:" + season.Window, "Seasonal", season.Window, what + ". In the guide " + Dates(season.StartMonth, season.StartDay) + " to " + Dates(season.EndMonth, season.EndDay) + ".", hours, station);
        }

        return result;
    }

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

    private static string Count(int n, string singular, string? plural = null)
        => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");

    private static string Dates(int month, int day)
        => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month) + " " + day.ToString(CultureInfo.InvariantCulture);
}
