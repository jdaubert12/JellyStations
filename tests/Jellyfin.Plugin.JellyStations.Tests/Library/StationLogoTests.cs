using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Weather.Screens;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

/// <summary>
/// The server-side logo card. The hash, random source and colors are checked against values computed from the
/// page's JavaScript, so a card drawn here uses the same pattern and colors as the editor would for the same seed.
/// </summary>
public class StationLogoTests
{
    private static readonly WeatherAssets Assets = new(Path.Combine(Path.GetTempPath(), "JellyStationsTests", "assets"));

    [Fact]
    public void Hash_matches_the_pages_fnv1a()
    {
        Assert.Equal(126640636u, StationLogo.Fnv1a("kids tv|12|look0"));
    }

    [Fact]
    public void Random_source_matches_the_pages_mulberry32()
    {
        var rnd = new StationLogo.Mulberry32(StationLogo.Fnv1a("kids tv|12|look0"));
        Assert.Equal(0.5668339785188437, rnd.Next(), 12);
        Assert.Equal(0.9909859492909163, rnd.Next(), 12);
        Assert.Equal(0.7857190428767353, rnd.Next(), 12);
        var zero = new StationLogo.Mulberry32(0);
        Assert.Equal(0.26642920868471265, zero.Next(), 12);
        Assert.Equal(0.0003297457005828619, zero.Next(), 12);
    }

    [Fact]
    public void Colors_match_the_pages_formulas()
    {
        Assert.Equal("#2c7396", StationLogo.HslToHex(200, 55, 38));
        Assert.Equal("#ffffff", StationLogo.HslToHex(0, 0, 100));
        Assert.Equal("#962c4d", StationLogo.ColorForName("Kids TV"));
        Assert.Equal("#2c9684", StationLogo.ColorForName("TV"));
        var (h, s, l) = StationLogo.HexToHsl("#2c7396");
        Assert.Equal(200, h, 0);
        Assert.Equal(55, s, 0);
        Assert.Equal(38, l, 0);
        Assert.True(StationLogo.Luminance("#ffffff") > 0.99);
        Assert.True(StationLogo.Luminance("#111111") < 0.1);
    }

    [Fact]
    public void Valid_color_accepts_only_six_hex_digits()
    {
        Assert.Equal("#2a5d9f", StationLogo.ValidColor("#2A5D9F"));
        Assert.Null(StationLogo.ValidColor("2a5d9f"));
        Assert.Null(StationLogo.ValidColor("#2a5d9"));
        Assert.Null(StationLogo.ValidColor("#zzzzzz"));
        Assert.Null(StationLogo.ValidColor(null));
    }

    [Fact]
    public void Seed_text_is_the_pages_name_number_sources_and_look()
    {
        var station = new Station { Name = "Kids TV", ChannelNumber = "12" };
        station.Pool.Sources.Add(new ContentSource { Kind = SourceKind.Library, DisplayName = "Movies" });
        station.Pool.Sources.Add(new ContentSource { Kind = SourceKind.Genre, Genre = "Comedy" });
        station.Pool.Sources.Add(new ContentSource { Kind = SourceKind.Tag, Tag = "xmas" });
        station.Pool.Sources.Add(new ContentSource { Kind = SourceKind.Series, ItemId = Guid.Parse("11111111-aaaa-0000-0000-000000000001") });
        station.Pool.Sources.Add(new ContentSource { Kind = SourceKind.Movie });
        station.Lineup.Items.Add(new LineupItem { ItemId = Guid.Parse("22222222-aaaa-0000-0000-000000000002"), DisplayName = "Dune" });

        Assert.Equal("kids tv|12|movies|comedy|xmas|11111111aaaa00000000000000000001|movie|dune|look0", StationLogo.SeedText(station));
        Assert.EndsWith("|look2", StationLogo.SeedText(station, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void Draws_a_png_card_that_is_the_same_for_the_same_seed()
    {
        var a = StationLogo.Png("Kids TV", "12", "#962c4d", "kids tv|12|look0", Assets.Typeface(FontWeight.Bold), Assets.Typeface(FontWeight.SemiBold));
        var b = StationLogo.Png("Kids TV", "12", "#962c4d", "kids tv|12|look0", Assets.Typeface(FontWeight.Bold), Assets.Typeface(FontWeight.SemiBold));
        var other = StationLogo.Png("Kids TV", "12", "#962c4d", "kids tv|12|look1", Assets.Typeface(FontWeight.Bold), Assets.Typeface(FontWeight.SemiBold));

        Assert.True(a.Length > 1000);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, a.Take(4));
        Assert.Equal(a, b);
        Assert.NotEqual(a, other);

        var dir = Path.Combine(Path.GetTempPath(), "JellyStationsTests", "logos");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "kids-tv.png"), a);
    }

    [Fact]
    public void Every_pattern_and_a_long_name_draw_without_error()
    {
        // The six styles come from the seed; a spread of seeds covers them all many times over.
        for (var i = 0; i < 24; i++)
        {
            var png = StationLogo.Png("The Very Long Running Comedy Channel Of Laughs", i % 2 == 0 ? "101" : null, i % 3 == 0 ? "#f0f0f0" : "#2c7396", "seed" + i, Assets.Typeface(FontWeight.Bold), Assets.Typeface(FontWeight.SemiBold));
            Assert.True(png.Length > 1000);
        }

        var blank = StationLogo.Png(string.Empty, string.Empty, "nonsense", string.Empty, Assets.Typeface(FontWeight.Bold), Assets.Typeface(FontWeight.SemiBold));
        Assert.True(blank.Length > 1000);
    }
}
