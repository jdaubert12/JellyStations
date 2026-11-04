using System.Text.Json;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Storage;

public class StationUpgradesTests
{
    [Fact]
    public void A_station_from_before_the_defaults_drops_an_off_bug_and_card_for_the_default()
    {
        var station = new Station
        {
            Name = "Old",
            LogoOverlay = new LogoOverlayConfig { Enabled = false, Corner = LogoCorner.BottomLeft },
            UpNextCard = new UpNextCardConfig { Enabled = false, LeadTimesSeconds = new List<int> { 600 } }
        };

        Assert.True(StationUpgrades.Apply(station));

        Assert.Null(station.LogoOverlay);
        Assert.Null(station.UpNextCard);
        Assert.Equal(Station.CurrentFormatVersion, station.FormatVersion);
        Assert.False(StationUpgrades.Apply(station));
    }

    [Fact]
    public void A_station_that_had_turned_them_on_keeps_its_own_settings()
    {
        var station = new Station
        {
            LogoOverlay = new LogoOverlayConfig { Enabled = true, Corner = LogoCorner.BottomLeft, Opacity = 55 },
            UpNextCard = new UpNextCardConfig { Enabled = true, LeadTimesSeconds = new List<int> { 600, 60 } }
        };

        Assert.True(StationUpgrades.Apply(station));

        Assert.Equal(LogoCorner.BottomLeft, station.LogoOverlay!.Corner);
        Assert.Equal(55, station.LogoOverlay.Opacity);
        Assert.Equal(new[] { 600, 60 }, station.UpNextCard!.LeadTimesSeconds);
        Assert.Equal(Station.CurrentFormatVersion, station.FormatVersion);
    }

    [Fact]
    public void A_current_station_that_chose_off_over_the_default_keeps_that_choice()
    {
        var station = new Station
        {
            FormatVersion = Station.CurrentFormatVersion,
            UpNextCard = new UpNextCardConfig { Enabled = false }
        };

        Assert.False(StationUpgrades.Apply(station));

        Assert.NotNull(station.UpNextCard);
        Assert.False(station.UpNextCard.Enabled);
    }

    [Fact]
    public void Old_settings_get_the_defaults_and_their_templates_are_upgraded()
    {
        var settings = new GlobalSettings
        {
            LogoOverlay = null!,
            UpNextCard = null!,
            StationTemplates =
            {
                new StationTemplate { Name = "Look", Settings = new Station { UpNextCard = new UpNextCardConfig { Enabled = false } } },
                new StationTemplate { Name = "Card", Settings = new Station { UpNextCard = new UpNextCardConfig { Enabled = true } } }
            }
        };

        Assert.True(StationUpgrades.Apply(settings));

        Assert.NotNull(settings.LogoOverlay);
        Assert.False(settings.LogoOverlay.Enabled);
        Assert.NotNull(settings.UpNextCard);
        Assert.Null(settings.StationTemplates[0].Settings.UpNextCard);
        Assert.True(settings.StationTemplates[1].Settings.UpNextCard!.Enabled);
        Assert.All(settings.StationTemplates, t => Assert.Equal(Station.CurrentFormatVersion, t.Settings.FormatVersion));
    }

    [Fact]
    public void An_imported_station_file_from_an_older_version_is_upgraded()
    {
        const string json = """
            {
              "id": "6f1c2a3b-1111-4222-8333-444455556666",
              "name": "Imported",
              "logoOverlay": { "enabled": true, "corner": "topLeft", "opacity": 40, "heightPercent": 6 },
              "upNextCard": { "enabled": false, "corner": "bottomRight" }
            }
            """;

        var doc = StationExportDocument.Parse(JsonDocument.Parse(json).RootElement, JsonFileStore.Options);

        var station = Assert.Single(doc!.Stations);
        Assert.Equal(LogoCorner.TopLeft, station.LogoOverlay!.Corner);
        Assert.Null(station.UpNextCard);
        Assert.Equal(Station.CurrentFormatVersion, station.FormatVersion);
    }

    [Fact]
    public void A_station_without_its_own_settings_is_written_without_them_and_reads_back_the_same()
    {
        var station = new Station { Id = Guid.NewGuid(), Name = "Follows the defaults", FormatVersion = Station.CurrentFormatVersion };

        var json = JsonSerializer.Serialize(station, JsonFileStore.Options);
        var back = JsonSerializer.Deserialize<Station>(json, JsonFileStore.Options)!;

        Assert.DoesNotContain("upNextCard", json, StringComparison.Ordinal);
        Assert.Null(back.LogoOverlay);
        Assert.Null(back.UpNextCard);
        Assert.False(StationUpgrades.Apply(back));
    }
}
