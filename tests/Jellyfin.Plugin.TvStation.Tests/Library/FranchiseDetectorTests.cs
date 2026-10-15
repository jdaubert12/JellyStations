using Jellyfin.Plugin.TvStation.Library;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Library;

public class FranchiseDetectorTests
{
    private static FranchiseCandidate M(string title, int? year = null) => new(Guid.NewGuid(), title, year, TimeSpan.FromMinutes(100).Ticks);

    [Theory]
    [InlineData("Harry Potter and the Philosopher's Stone", "harry potter and the philosophers stone")]
    [InlineData("The Matrix Reloaded", "matrix reloaded")]
    [InlineData("Iron Man 2", "iron man")]
    [InlineData("Rocky IV", "rocky")]
    [InlineData("Dune: Part Two", "dune")]
    [InlineData("Kill Bill: Vol. 1", "kill bill")]
    [InlineData("Fast & Furious 6", "fast and furious")]
    [InlineData("Toy Story 3 (2010)", "toy story")]
    public void Normalize_StripsArticlesPunctuationAndNumbers(string title, string expected)
    {
        Assert.Equal(expected, FranchiseDetector.Normalize(title));
    }

    [Fact]
    public void Detect_GroupsByPrefix_WithoutFalseFriends()
    {
        var movies = new[]
        {
            M("Harry Potter and the Philosopher's Stone", 2001), M("Harry Potter and the Chamber of Secrets", 2002), M("Harry Potter and the Prisoner of Azkaban", 2004),
            M("The Lord of the Rings: The Fellowship of the Ring", 2001), M("The Lord of the Rings: The Two Towers", 2002),
            M("The Bourne Identity", 2002), M("The Bourne Supremacy", 2004), M("The Bourne Ultimatum", 2007),
            M("Iron Man", 2008), M("Iron Man 2", 2010), M("Iron Man 3", 2013),
            M("Dune", 2021), M("Dune: Part Two", 2024),
            M("Star Wars", 1977), M("Star Wars: The Empire Strikes Back", 1980),
            M("Star Trek", 2009), M("Star Trek Into Darkness", 2013),
            M("Black Swan", 2010), M("Black Panther", 2018),
            M("The Matrix", 1999), M("The Matrix Reloaded", 2003),
            M("Coco", 2017)
        };

        var franchises = FranchiseDetector.Detect(movies);
        var names = franchises.Select(f => f.Name).ToList();

        Assert.Contains("Harry Potter", names);
        Assert.Contains("Lord of the Rings", names);
        Assert.Contains("Bourne", names);
        Assert.Contains("Iron Man", names);
        Assert.Contains("Dune", names);
        Assert.Contains("Star Wars", names);
        Assert.Contains("Star Trek", names);
        Assert.DoesNotContain("Star", names);
        Assert.DoesNotContain("Black", names);
        Assert.DoesNotContain("Matrix", names); // two films, single-word prefix: needs three
        Assert.Equal(7, franchises.Count);

        var potter = franchises.Single(f => f.Name == "Harry Potter");
        Assert.Equal(3, potter.Members.Count);
        Assert.Equal(2001, potter.Members[0].Year);

        // Every title belongs to at most one franchise.
        var all = franchises.SelectMany(f => f.Members.Select(m => m.Id)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Detect_SingleWordPrefix_NeedsThreeTitles()
    {
        Assert.Empty(FranchiseDetector.Detect(new[] { M("Rocky"), M("Rocky II") }));
        Assert.Single(FranchiseDetector.Detect(new[] { M("Rocky"), M("Rocky II"), M("Rocky III") }));
    }

    [Fact]
    public void Detect_OrdersByHours()
    {
        var big = new[] { M("Alpha One"), M("Alpha Two"), M("Alpha Three") };
        var small = new[] { M("Beta: One") with { RuntimeTicks = TimeSpan.FromMinutes(10).Ticks }, M("Beta: Two") with { RuntimeTicks = TimeSpan.FromMinutes(10).Ticks } };
        var franchises = FranchiseDetector.Detect(small.Concat(big));
        Assert.Equal(new[] { "Alpha", "Beta" }, franchises.Select(f => f.Name));
    }
}
