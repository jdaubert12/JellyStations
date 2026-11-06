using System.Globalization;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class BlockTemplatesTests
{
    [Fact]
    public void Starters_HaveFixedDistinctIdsAndNames()
    {
        var starters = BlockTemplates.BuiltIn;

        Assert.Equal(
            new[] { "Saturday morning cartoons", "Prime-time movie", "Late night", "Weather on the hour" },
            starters.Select(b => b.Name));
        Assert.Equal(starters.Count, starters.Select(b => b.Id).Distinct().Count());
        Assert.All(starters, b => Assert.StartsWith("7c1b3a20-", b.Id.ToString("D"), StringComparison.Ordinal));
        Assert.Equal(Guid.Parse("7c1b3a20-0004-4b5e-9a6d-6e1f0c2d3e04"), starters[3].Id);
    }

    [Fact]
    public void Starters_AreBlocksTheEditorAccepts_WithoutSources()
    {
        // The same limits StationService.Validate puts on a block; only the sources are left for the user.
        Assert.All(BlockTemplates.BuiltIn, b =>
        {
            Assert.NotEmpty(b.Days);
            Assert.Equal(BlockRecurrence.Weekly, b.Recurrence);
            Assert.InRange(b.DurationMinutes, 5, 1440);
            Assert.True(TimeSpan.TryParseExact(b.StartLocal, @"hh\:mm", CultureInfo.InvariantCulture, out _));
            Assert.Empty(b.Sources);
            Assert.True(b.RepeatEveryMinutes == 0 || b.RepeatEveryMinutes is >= 5 and <= 1440);
            Assert.True(string.IsNullOrEmpty(b.RepeatUntilLocal) || TimeSpan.TryParseExact(b.RepeatUntilLocal, @"hh\:mm", CultureInfo.InvariantCulture, out _));
        });
    }

    [Fact]
    public void WeatherOnTheHour_IsFiveMinutesOfWeatherEveryHourOfTheDay()
    {
        var weather = BlockTemplates.BuiltIn.Single(b => b.Kind == BlockKind.Weather);

        Assert.Equal("Weather on the hour", weather.Name);
        Assert.Equal(7, weather.Days.Count);
        Assert.Equal("06:00", weather.StartLocal);
        Assert.Equal(5, weather.DurationMinutes);
        Assert.Equal(60, weather.RepeatEveryMinutes);
        Assert.Equal("23:00", weather.RepeatUntilLocal);
        Assert.All(BlockTemplates.BuiltIn.Where(b => b != weather), b => Assert.Equal(BlockKind.Content, b.Kind));
    }

    [Fact]
    public void ASavedTemplateOfTheSameName_TakesAStartersPlace()
    {
        Assert.Equal(4, BlockTemplates.Available(null).Count);

        var saved = new List<SlotBlock> { new() { Name = " late NIGHT " }, new() { Name = "My block" } };
        var available = BlockTemplates.Available(saved);

        Assert.Equal(3, available.Count);
        Assert.DoesNotContain(available, b => b.Name == "Late night");
    }
}
