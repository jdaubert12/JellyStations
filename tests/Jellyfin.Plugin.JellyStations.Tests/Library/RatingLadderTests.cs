using Jellyfin.Plugin.JellyStations.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class RatingLadderTests
{
    [Theory]
    [InlineData("G", 2)]
    [InlineData("tv-g", 2)]
    [InlineData("PG-13", 4)]
    [InlineData("TV-14", 4)]
    [InlineData("US-R", 5)]
    [InlineData("TV-MA", 5)]
    [InlineData("TV-Y7-FV", 1)]
    public void Rank_MapsBothLaddersOntoOneScale(string rating, int expected)
    {
        Assert.Equal(expected, RatingLadder.Rank(rating));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NR")]
    [InlineData("Not Rated")]
    [InlineData("Approved")]
    [InlineData("FSK 12")]
    public void UnknownOrMissing_IsUnrated(string? rating)
    {
        Assert.Null(RatingLadder.Rank(rating));
        Assert.True(RatingLadder.IsUnrated(rating));
    }

    [Theory]
    [InlineData("PG", "PG-13", true)]
    [InlineData("TV-14", "PG-13", true)]
    [InlineData("R", "PG-13", false)]
    [InlineData("TV-MA", "TV-14", false)]
    [InlineData("TV-Y7", "G", true)]
    [InlineData("NR", "R", false)]
    [InlineData("PG", "bogus", false)]
    public void IsAtMost_ComparesAcrossLadders(string rating, string max, bool expected)
    {
        Assert.Equal(expected, RatingLadder.IsAtMost(rating, max));
    }
}
