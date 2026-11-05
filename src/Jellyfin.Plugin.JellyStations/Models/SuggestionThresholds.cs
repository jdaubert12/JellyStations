namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>Minimum content a suggestion rule needs before it proposes a station. Kept in the plugin settings.</summary>
public sealed class SuggestionThresholds
{
    /// <summary>Hours a genre needs. A genre with this much in movies and again in episodes is offered as two stations.</summary>
    public double GenreMinHours { get; set; } = 8;

    public int SeriesMinEpisodes { get; set; } = 20;

    public double KidsMinHours { get; set; } = 4;

    public double FamilyMoviesMinHours { get; set; } = 6;

    /// <summary>Hours of movies from one decade, or of shows that began in one, that earn a decade station.</summary>
    public double DecadeMinHours { get; set; } = 8;

    /// <summary>Hours of movies made before <see cref="ClassicsBeforeYear"/> that earn a "Classic Movies" station.</summary>
    public double ClassicsMinHours { get; set; } = 8;

    /// <summary>First year that is no longer a classic.</summary>
    public int ClassicsBeforeYear { get; set; } = 1970;

    /// <summary>Titles a franchise needs for its own marathon station.</summary>
    public int FranchiseMinTitles { get; set; } = 3;

    public int CollectionMinItems { get; set; } = 2;

    public double SeasonalMinHours { get; set; } = 1;

    /// <summary>Hours of half-hour comedies (from at least <see cref="ComfortMinSeries"/> shows) that earn a "Comfort Reruns" station.</summary>
    public double ComfortMinHours { get; set; } = 6;

    public int ComfortMinSeries { get; set; } = 3;

    /// <summary>Hours no channel plays yet that earn the "Everything Else" station.</summary>
    public double LeftoversMinHours { get; set; } = 4;

    /// <summary>Brings every value into a range the rules can work with (a hand-edited or partial settings file).</summary>
    public void Normalize()
    {
        GenreMinHours = Hours(GenreMinHours, 8);
        KidsMinHours = Hours(KidsMinHours, 4);
        FamilyMoviesMinHours = Hours(FamilyMoviesMinHours, 6);
        DecadeMinHours = Hours(DecadeMinHours, 8);
        ClassicsMinHours = Hours(ClassicsMinHours, 8);
        SeasonalMinHours = Hours(SeasonalMinHours, 1);
        ComfortMinHours = Hours(ComfortMinHours, 6);
        LeftoversMinHours = Hours(LeftoversMinHours, 4);
        SeriesMinEpisodes = Math.Clamp(SeriesMinEpisodes, 1, 10000);
        FranchiseMinTitles = Math.Clamp(FranchiseMinTitles, 2, 100);
        CollectionMinItems = Math.Clamp(CollectionMinItems, 1, 1000);
        ComfortMinSeries = Math.Clamp(ComfortMinSeries, 2, 100);
        ClassicsBeforeYear = Math.Clamp(ClassicsBeforeYear, 1900, 2100);
    }

    private static double Hours(double value, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Round(Math.Clamp(value, 0.1, 10000), 1);
}
