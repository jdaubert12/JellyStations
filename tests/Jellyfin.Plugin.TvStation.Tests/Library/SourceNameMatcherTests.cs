using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Models;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Library;

public class SourceNameMatcherTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    [Theory]
    [InlineData("Harry Potter and the Philosopher's Stone (2001)", "Harry Potter and the Philosopher's Stone", 2001)]
    [InlineData("Dune", "Dune", null)]
    [InlineData("1917 (2019)", "1917", 2019)]
    [InlineData("2001 (1968)", "2001", 1968)]
    [InlineData("Blade Runner (Final Cut)", "Blade Runner (Final Cut)", null)]
    public void Parse_Movie_SplitsOffYear(string display, string name, int? year)
    {
        var p = SourceNameMatcher.Parse(SourceKind.Movie, display);
        Assert.Equal(name, p.Name);
        Assert.Equal(year, p.Year);
        Assert.Null(p.SeriesName);
    }

    [Fact]
    public void Parse_Episode_ReadsSeriesAndNumbers()
    {
        var p = SourceNameMatcher.Parse(SourceKind.Episode, "Avatar: The Last Airbender S01E02 – The Avatar Returns");
        Assert.Equal("Avatar: The Last Airbender", p.SeriesName);
        Assert.Equal(1, p.Season);
        Assert.Equal(2, p.Episode);
        Assert.Equal("The Avatar Returns", p.Name);
        Assert.True(SourceNameMatcher.LooksLikeEpisode("Avatar: The Last Airbender S01E02 – The Avatar Returns"));
        Assert.False(SourceNameMatcher.LooksLikeEpisode("Dune (2021)"));
    }

    [Fact]
    public void Parse_Season_ReadsSeriesAndNumber()
    {
        var p = SourceNameMatcher.Parse(SourceKind.Season, "Firefly – Season 1");
        Assert.Equal("Firefly", p.SeriesName);
        Assert.Equal("Season 1", p.Name);
        Assert.Equal(1, p.Season);

        var specials = SourceNameMatcher.Parse(SourceKind.Season, "Firefly – Specials");
        Assert.Equal("Specials", specials.Name);
        Assert.Null(specials.Season);
    }

    [Fact]
    public void Pick_UniqueTitle_MatchesCaseInsensitively()
    {
        var candidates = new[] { new NameCandidate(A, "The Wild Thornberrys", 1998), new NameCandidate(B, "Rugrats", 1991) };
        Assert.Equal(A, SourceNameMatcher.Pick(candidates, SourceNameMatcher.Parse(SourceKind.Series, "the wild thornberrys")));
        Assert.Null(SourceNameMatcher.Pick(candidates, SourceNameMatcher.Parse(SourceKind.Series, "Thornberrys")));
    }

    [Fact]
    public void Pick_YearBreaksTies_AndLoneMatchIgnoresYear()
    {
        var candidates = new[] { new NameCandidate(A, "Dune", 1984), new NameCandidate(B, "Dune", 2021) };
        Assert.Equal(B, SourceNameMatcher.Pick(candidates, SourceNameMatcher.Parse(SourceKind.Movie, "Dune (2021)")));
        Assert.Null(SourceNameMatcher.Pick(candidates, SourceNameMatcher.Parse(SourceKind.Movie, "Dune")));

        var one = new[] { new NameCandidate(A, "Dune", 1984) };
        Assert.Equal(A, SourceNameMatcher.Pick(one, SourceNameMatcher.Parse(SourceKind.Movie, "Dune (2021)")));
    }

    [Fact]
    public void Pick_Episode_ByTitle_ThenByNumbersWhenRenamed()
    {
        var episodes = new[]
        {
            new NameCandidate(A, "The Boy in the Iceberg", null, "Avatar", 1, 1),
            new NameCandidate(B, "The Avatar Returns", null, "Avatar", 1, 2),
            new NameCandidate(C, "The Avatar Returns", null, "Avatar", 3, 2)
        };

        Assert.Equal(B, SourceNameMatcher.Pick(episodes, SourceNameMatcher.Parse(SourceKind.Episode, "Avatar S01E02 – The Avatar Returns")));
        Assert.Equal(B, SourceNameMatcher.Pick(episodes, SourceNameMatcher.Parse(SourceKind.Episode, "Avatar S01E02 – Renamed Here")));
        Assert.Null(SourceNameMatcher.Pick(episodes, SourceNameMatcher.Parse(SourceKind.Episode, "Korra S01E02 – The Avatar Returns")));
    }

    [Fact]
    public void Pick_Normalizes_CurlyApostrophesAndDashes()
    {
        var candidates = new[] { new NameCandidate(A, "Harry Potter and the Philosopher’s Stone", 2001) };
        Assert.Equal(A, SourceNameMatcher.Pick(candidates, SourceNameMatcher.Parse(SourceKind.Movie, "Harry Potter and the Philosopher's Stone (2001)")));
    }
}
