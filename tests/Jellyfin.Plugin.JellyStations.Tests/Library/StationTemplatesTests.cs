using System.Text.Json;
using Jellyfin.Plugin.JellyStations.Api;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class StationTemplatesTests
{
    private static readonly Guid Clip = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Show = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    /// <summary>A station with every kind of content and a distinctive setting in every section.</summary>
    private static Station FullStation() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Comedy",
        ChannelNumber = "7",
        CallSign = "LOL",
        LogoFileName = "logo.png",
        TimeZoneId = "America/Denver",
        AnchorUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        Revision = 9,
        PaddingByLength = true,
        PaddingGridMinutes = 30,
        Mode = ScheduleMode.Pool,
        Pool = new PoolConfig
        {
            Order = PlayOrder.ShuffleEpisodesInOrder,
            CooldownHours = 12,
            MarathonMin = 2,
            MarathonMax = 4,
            Sources = { new ContentSource { Kind = SourceKind.Series, ItemId = Show, DisplayName = "Frasier" } },
            DaypartRules = { new DaypartRule { StartLocal = "06:00", EndLocal = "21:00", MaxRating = "PG" } }
        },
        Slots = new SlotsConfig
        {
            Blocks =
            {
                new SlotBlock { Name = "Prime", Days = { DayOfWeek.Friday }, StartLocal = "20:00", DurationMinutes = 120, Sources = { new ContentSource { Kind = SourceKind.Genre, Genre = "Comedy" } } },
                new SlotBlock { Name = "Weather", Kind = BlockKind.Weather, StartLocal = "06:00", DurationMinutes = 5, RepeatEveryMinutes = 60 }
            },
            Filler = new FillerConfig { Order = PlayOrder.Sequential, Sources = { new ContentSource { Kind = SourceKind.Tag, Tag = "filler" } } }
        },
        Lineup = new LineupConfig { Items = { new LineupItem { ItemId = Show, DisplayName = "x" } } },
        Weather = new WeatherConfig
        {
            Units = WeatherUnits.Metric,
            Theme = WeatherTheme.Classic,
            ScreenSeconds = 7,
            Locations = { new WeatherLocation { Name = "Denver", Latitude = 39.7, Longitude = -104.9 } },
            MusicSources = { new ContentSource { Kind = SourceKind.Album, ItemId = Show } },
            MusicFolder = "/music"
        },
        WeatherSourceStationId = Guid.NewGuid(),
        Encoding = new EncodingProfile { Width = 1920, Height = 1080, VideoEncoder = "hevc_qsv" },
        ActiveWindows = { new SeasonWindow { Label = "Summer", StartMonth = 6, StartDay = 1, EndMonth = 8, EndDay = 31 } },
        ExcludedItemIds = { Show },
        ExcludedTags = { "christmas" },
        UseGlobalExcludedTags = false,
        MinRuntimeMinutes = 10,
        Bumpers = new BumperConfig
        {
            Enabled = true,
            BreakMinutes = 2,
            Sources = { new ContentSource { Kind = SourceKind.Folder, ItemId = Clip, DisplayName = "Idents" } },
            PreRoll = new BumperRollConfig { Enabled = true },
            Weather = new WeatherBreakConfig { Enabled = true, Minutes = 1 }
        },
        LogoOverlay = new LogoOverlayConfig { Enabled = true, Corner = LogoCorner.BottomLeft, Opacity = 55 },
        UpNextCard = new UpNextCardConfig { Enabled = true, LeadTimesSeconds = new List<int> { 600, 60 } },
        CardText = "Back soon",
        AutoRecompileNightly = false,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    [Fact]
    public void FromStation_strips_identity_and_content()
    {
        var t = StationTemplates.FromStation(FullStation(), " Comedy look ");
        var s = t.Settings;

        Assert.Equal("Comedy look", t.Name);
        Assert.NotEqual(Guid.Empty, t.Id);
        Assert.Equal(Guid.Empty, s.Id);
        Assert.Equal(string.Empty, s.Name);
        Assert.Equal(string.Empty, s.ChannelNumber);
        Assert.Null(s.CallSign);
        Assert.Null(s.LogoFileName);
        Assert.Equal(string.Empty, s.TimeZoneId);
        Assert.Equal(default, s.AnchorUtc);
        Assert.Equal(1, s.Revision);

        Assert.Empty(s.Pool.Sources);
        Assert.Empty(s.Slots.Filler.Sources);
        Assert.All(s.Slots.Blocks, b => Assert.Empty(b.Sources));
        Assert.All(s.Slots.Blocks, b => Assert.Equal(Guid.Empty, b.Id));
        Assert.Empty(s.Lineup.Items);
        Assert.Empty(s.ExcludedItemIds);
        Assert.Empty(s.Weather.Locations);
        Assert.Empty(s.Weather.MusicSources);
        Assert.Null(s.Weather.MusicFolder);
        Assert.Null(s.WeatherSourceStationId); // a template belongs to no particular weather channel
    }

    [Fact]
    public void FromStation_keeps_the_look_and_the_schedule_shape()
    {
        var s = StationTemplates.FromStation(FullStation(), "t").Settings;

        Assert.True(s.PaddingByLength);
        Assert.Equal(30, s.PaddingGridMinutes);
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, s.Pool.Order);
        Assert.Equal(12, s.Pool.CooldownHours);
        Assert.Equal(2, s.Pool.MarathonMin);
        Assert.Equal(4, s.Pool.MarathonMax);
        Assert.Single(s.Pool.DaypartRules);
        Assert.Equal("PG", s.Pool.DaypartRules[0].MaxRating);

        Assert.Equal(2, s.Slots.Blocks.Count);
        Assert.Equal("Prime", s.Slots.Blocks[0].Name);
        Assert.Equal(120, s.Slots.Blocks[0].DurationMinutes);
        Assert.Contains(DayOfWeek.Friday, s.Slots.Blocks[0].Days);
        Assert.Equal(BlockKind.Weather, s.Slots.Blocks[1].Kind);
        Assert.Equal(60, s.Slots.Blocks[1].RepeatEveryMinutes);
        Assert.Equal(PlayOrder.Sequential, s.Slots.Filler.Order);

        Assert.Equal(WeatherUnits.Metric, s.Weather.Units);
        Assert.Equal(7, s.Weather.ScreenSeconds);
        Assert.NotNull(s.Encoding);
        Assert.Equal("hevc_qsv", s.Encoding!.VideoEncoder);
        Assert.Single(s.ActiveWindows);
        Assert.Equal("Summer", s.ActiveWindows[0].Label);
        Assert.Equal(new[] { "christmas" }, s.ExcludedTags);
        Assert.False(s.UseGlobalExcludedTags);
        Assert.Equal(10, s.MinRuntimeMinutes);

        // Bumper clips are part of the look and travel with the template (by name through import).
        Assert.True(s.Bumpers.Enabled);
        Assert.Equal(2, s.Bumpers.BreakMinutes);
        Assert.Single(s.Bumpers.Sources);
        Assert.Equal("Idents", s.Bumpers.Sources[0].DisplayName);
        Assert.True(s.Bumpers.PreRoll.Enabled);
        Assert.True(s.Bumpers.Weather.Enabled);

        Assert.True(s.LogoOverlay.Enabled);
        Assert.Equal(LogoCorner.BottomLeft, s.LogoOverlay.Corner);
        Assert.True(s.UpNextCard.Enabled);
        Assert.Equal(new[] { 600, 60 }, s.UpNextCard.LeadTimesSeconds);
        Assert.Equal("Back soon", s.CardText);
        Assert.False(s.AutoRecompileNightly);
    }

    [Fact]
    public void FromStation_is_a_deep_copy()
    {
        var station = FullStation();
        var t = StationTemplates.FromStation(station, "t");
        station.Pool.DaypartRules[0].MaxRating = "R";
        station.Bumpers.Sources.Clear();
        station.Slots.Blocks[0].Name = "changed";

        Assert.Equal("PG", t.Settings.Pool.DaypartRules[0].MaxRating);
        Assert.Single(t.Settings.Bumpers.Sources);
        Assert.Equal("Prime", t.Settings.Slots.Blocks[0].Name);
        Assert.Single(station.Pool.Sources); // the station itself was not stripped
    }

    [Fact]
    public void ApplyTo_gives_the_draft_the_settings_under_its_own_identity()
    {
        var t = StationTemplates.FromStation(FullStation(), "t");
        var draft = new Station { Name = "Laughs", ChannelNumber = "12", CallSign = "HA", TimeZoneId = "Europe/London" };

        var s = StationTemplates.ApplyTo(t, draft);

        Assert.Equal("Laughs", s.Name);
        Assert.Equal("12", s.ChannelNumber);
        Assert.Equal("HA", s.CallSign);
        Assert.Equal("Europe/London", s.TimeZoneId);
        Assert.Equal(Guid.Empty, s.Id);
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, s.Pool.Order);
        Assert.Equal(2, s.Slots.Blocks.Count);
        Assert.All(s.Slots.Blocks, b => Assert.NotEqual(Guid.Empty, b.Id));
        Assert.NotEqual(s.Slots.Blocks[0].Id, s.Slots.Blocks[1].Id);
        Assert.Empty(s.Pool.Sources);
        Assert.NotSame(t.Settings, s);
        Assert.NotSame(t.Settings.Bumpers, s.Bumpers);
    }

    [Fact]
    public void ApplyTo_uses_the_default_name_and_the_template_time_zone_when_set()
    {
        var t = new StationTemplate { DefaultName = "Weather", Settings = new Station { Mode = ScheduleMode.Weather, TimeZoneId = "Asia/Tokyo" } };

        var blank = StationTemplates.ApplyTo(t, new Station { TimeZoneId = "UTC" });
        Assert.Equal("Weather", blank.Name);
        Assert.Equal("Asia/Tokyo", blank.TimeZoneId);
        Assert.Equal(ScheduleMode.Weather, blank.Mode);

        var named = StationTemplates.ApplyTo(t, new Station { Name = "Sky" });
        Assert.Equal("Sky", named.Name);

        var noZones = StationTemplates.ApplyTo(new StationTemplate(), new Station());
        Assert.Equal("UTC", noZones.TimeZoneId);
    }

    [Fact]
    public void Built_in_presets_are_nine_with_fixed_unique_ids_and_names()
    {
        var presets = StationTemplates.BuiltIn;
        Assert.Equal(9, presets.Count);
        Assert.Equal(9, presets.Select(p => p.Id).Distinct().Count());
        Assert.Equal(9, presets.Select(p => p.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(presets, p => Assert.True(p.BuiltIn));
        Assert.All(presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        Assert.All(presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Icon)));
        Assert.All(presets, p => Assert.Empty(p.Settings.Pool.Sources));
    }

    [Fact]
    public void Built_in_presets_match_the_old_page_presets()
    {
        StationTemplate Named(string name) => StationTemplates.BuiltIn.Single(p => p.Name == name);
        Station Draft(string name) => StationTemplates.ApplyTo(Named(name), new Station { Name = "x", ChannelNumber = "1", TimeZoneId = "UTC" });

        var shuffle = Draft("Shuffle a collection");
        Assert.Equal(ScheduleMode.Pool, shuffle.Mode);
        Assert.Equal(PlayOrder.Shuffle, shuffle.Pool.Order);
        Assert.False(shuffle.PaddingByLength);

        var kids = Draft("Kids channel");
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, kids.Pool.Order);
        Assert.Equal(2, kids.Pool.MarathonMin);
        Assert.Equal(3, kids.Pool.MarathonMax);
        Assert.True(kids.PaddingByLength);
        Assert.Equal("G", Named("Kids channel").SourceDefaults!.MaxRating);
        Assert.False(Named("Kids channel").SourceDefaults!.AllowUnrated);

        var movies = Draft("Movie night");
        Assert.Equal(8, movies.Pool.CooldownHours);
        var rule = Assert.Single(movies.Pool.DaypartRules);
        Assert.Equal("PG-13", rule.MaxRating);
        Assert.Equal("20:00", rule.EndLocal);
        Assert.True(rule.AllowUnrated);
        Assert.Equal(new[] { SourceKind.Movie }, Named("Movie night").SourceDefaults!.ItemKinds);

        var reruns = Draft("Comfort reruns");
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, reruns.Pool.Order);
        Assert.Equal(1, reruns.Pool.MarathonMax);
        Assert.Equal(new[] { SourceKind.Episode }, Named("Comfort reruns").SourceDefaults!.ItemKinds);

        var holiday = Draft("Holiday channel");
        var window = Assert.Single(holiday.ActiveWindows);
        Assert.Equal(11, window.StartMonth);
        Assert.Equal(20, window.StartDay);
        Assert.Equal(1, window.EndMonth);
        Assert.Equal(6, window.EndDay);
        Assert.False(holiday.UseGlobalExcludedTags);

        var marathon = Draft("One show, start to finish");
        Assert.Equal(PlayOrder.Sequential, marathon.Pool.Order);
        Assert.False(marathon.Pool.AvoidSameSeriesBackToBack);

        var weekly = Draft("Weekly schedule");
        Assert.Equal(ScheduleMode.Slots, weekly.Mode);
        Assert.Equal(15, weekly.PaddingGridMinutes);

        Assert.Equal(ScheduleMode.Lineup, Draft("Play a fixed list").Mode);

        var weather = StationTemplates.ApplyTo(Named("Weather channel"), new Station { TimeZoneId = "UTC" });
        Assert.Equal(ScheduleMode.Weather, weather.Mode);
        Assert.Equal("Weather", weather.Name);
    }

    [Fact]
    public void Merge_lists_saved_first_and_lets_a_saved_name_shadow_a_built_in()
    {
        var saved = new List<StationTemplate>
        {
            new() { Id = Guid.NewGuid(), Name = "Zebra", Settings = new Station() },
            new() { Id = Guid.NewGuid(), Name = "kids CHANNEL", Settings = new Station { Pool = new PoolConfig { MarathonMax = 9 } } }
        };

        var merged = StationTemplates.Merge(saved);

        Assert.Equal(2 + 8, merged.Count);
        Assert.Equal("kids CHANNEL", merged[0].Name);
        Assert.Equal("Zebra", merged[1].Name);
        Assert.All(merged.Take(2), t => Assert.False(t.BuiltIn));
        Assert.All(merged.Skip(2), t => Assert.True(t.BuiltIn));
        Assert.DoesNotContain(merged, t => t.BuiltIn && t.Name == "Kids channel");
        Assert.Equal(9, merged[0].Settings.Pool.MarathonMax);

        Assert.Equal(9, StationTemplates.Merge(null).Count);
        Assert.NotNull(StationTemplates.Find(saved, saved[0].Id));
        Assert.NotNull(StationTemplates.Find(saved, StationTemplates.BuiltIn[0].Id));
        Assert.Null(StationTemplates.Find(saved, Guid.NewGuid()));
    }

    [Fact]
    public void Upsert_strips_content_and_replaces_by_name_or_id()
    {
        var list = new List<StationTemplate>();
        var first = StationTemplatesController.Upsert(list, new StationTemplate { Name = "Look", Settings = FullStation(), LogoColor = "#ABCDEF", Description = " d " });
        Assert.Single(list);
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.Empty(first.Settings.Pool.Sources);
        Assert.Equal("#abcdef", first.LogoColor);
        Assert.Equal("d", first.Description);

        StationTemplatesController.Upsert(list, new StationTemplate { Name = "look", Settings = new Station { Mode = ScheduleMode.Lineup }, LogoColor = "blue" });
        var only = Assert.Single(list);
        Assert.Equal(ScheduleMode.Lineup, only.Settings.Mode);
        Assert.Null(only.LogoColor);

        StationTemplatesController.Upsert(list, new StationTemplate { Id = only.Id, Name = "Renamed", Settings = new Station() });
        Assert.Equal("Renamed", Assert.Single(list).Name);
    }

    [Fact]
    public void Export_document_parses_both_shapes()
    {
        var options = JsonFileStore.Options;
        var array = JsonSerializer.SerializeToElement(new List<Station> { new() { Name = "A" }, new() { Name = "B" } }, options);
        var v1 = StationExportDocument.Parse(array, options);
        Assert.NotNull(v1);
        Assert.Equal(1, v1!.Version);
        Assert.Equal(2, v1.Stations.Count);
        Assert.Empty(v1.StationTemplates);

        var doc = new StationExportDocument
        {
            Stations = { new Station { Name = "A" } },
            StationTemplates = { StationTemplates.FromStation(FullStation(), "Look") },
            BlockTemplates = { new SlotBlock { Name = "Prime" } }
        };
        var v2 = StationExportDocument.Parse(JsonSerializer.SerializeToElement(doc, options), options);
        Assert.NotNull(v2);
        Assert.Equal(StationExportDocument.CurrentVersion, v2!.Version);
        Assert.Single(v2.Stations);
        Assert.Equal("Look", Assert.Single(v2.StationTemplates).Name);
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, v2.StationTemplates[0].Settings.Pool.Order);
        Assert.Equal("Prime", Assert.Single(v2.BlockTemplates).Name);

        // Jellyfin's API sends PascalCase; the store writes camelCase. Both must read.
        var pascal = JsonDocument.Parse("{\"Version\":2,\"Stations\":[{\"Name\":\"P\"}],\"StationTemplates\":[{\"Name\":\"T\",\"Settings\":{\"Mode\":\"Slots\"}}]}").RootElement;
        var fromApi = StationExportDocument.Parse(pascal, options)!;
        Assert.Equal("P", fromApi.Stations[0].Name);
        Assert.Equal(ScheduleMode.Slots, fromApi.StationTemplates[0].Settings.Mode);

        Assert.Null(StationExportDocument.Parse(JsonDocument.Parse("42").RootElement, options));

        // One station on its own (the example files) is a one-station document, not an empty v2 document.
        var single = StationExportDocument.Parse(JsonDocument.Parse("{\"Name\":\"Solo\",\"Mode\":\"Lineup\"}").RootElement, options)!;
        Assert.Equal(1, single.Version);
        Assert.Equal("Solo", Assert.Single(single.Stations).Name);
        var emptyDoc = StationExportDocument.Parse(JsonDocument.Parse("{\"stations\":[]}").RootElement, options)!;
        Assert.Empty(emptyDoc.Stations);
    }

    [Fact]
    public void Export_document_reads_jellyfins_dashless_guids_with_its_options()
    {
        var options = Jellyfin.Extensions.Json.JsonDefaults.Options;
        var id = Guid.NewGuid();
        var json = "{\"Version\":2,\"Stations\":[{\"Id\":\"" + id.ToString("N") + "\",\"Name\":\"P\"}],\"StationTemplates\":[{\"Id\":\"" + id.ToString("N") + "\",\"Name\":\"T\",\"Settings\":{\"Mode\":\"Slots\",\"Slots\":{\"Blocks\":[{\"Id\":\"" + Guid.NewGuid().ToString("N") + "\",\"Name\":\"b\"}]}}}]}";
        var doc = StationExportDocument.Parse(JsonDocument.Parse(json).RootElement, options)!;
        Assert.Equal(id, doc.Stations[0].Id);
        Assert.Equal(id, doc.StationTemplates[0].Id);
        Assert.Equal(ScheduleMode.Slots, doc.StationTemplates[0].Settings.Mode);
        Assert.Single(doc.StationTemplates[0].Settings.Slots.Blocks);
    }

    [Fact]
    public void Settings_round_trip_keeps_station_templates()
    {
        var settings = new GlobalSettings { StationTemplates = { StationTemplates.FromStation(FullStation(), "Look") } };
        var json = JsonFileStore.Serialize(settings);
        var back = JsonSerializer.Deserialize<GlobalSettings>(json, JsonFileStore.Options)!;
        var t = Assert.Single(back.StationTemplates);
        Assert.Equal("Look", t.Name);
        Assert.Equal(2, t.Settings.Slots.Blocks.Count);
        Assert.Equal(BlockKind.Weather, t.Settings.Slots.Blocks[1].Kind);
        Assert.True(t.Settings.Bumpers.Enabled);
    }
}
