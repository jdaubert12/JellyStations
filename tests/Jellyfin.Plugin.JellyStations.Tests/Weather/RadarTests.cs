using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Streaming;
using Jellyfin.Plugin.JellyStations.Weather;
using Jellyfin.Plugin.JellyStations.Weather.Radar;
using Jellyfin.Plugin.JellyStations.Weather.Screens;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

/// <summary>The radar screen: map maths, the two sources' requests, the loop's timing, the ffmpeg sequence input and the painted screen.</summary>
public class RadarTests
{
    private static readonly WeatherLocation Boston = new() { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Name = "Boston", Region = "Massachusetts, United States", Latitude = 42.36, Longitude = -71.06 };
    private static readonly DateTime Now = new(2026, 10, 26, 21, 37, 0, DateTimeKind.Utc);

    // ---- Web Mercator ---------------------------------------------------------------------------

    [Fact]
    public void WorldPixels_PutTheEquatorAndGreenwichInTheMiddle_AndBostonOnItsTile()
    {
        var (x, y) = WebMercator.ToWorld(0, 0, 0);
        Assert.Equal(128, x, 6);
        Assert.Equal(128, y, 6);

        var (bx, by) = WebMercator.ToWorld(Boston.Latitude, Boston.Longitude, 6);
        Assert.Equal(19, (int)Math.Floor(bx / 256));
        Assert.Equal(23, (int)Math.Floor(by / 256));

        var (lat, lon) = WebMercator.FromWorld(bx, by, 6);
        Assert.Equal(Boston.Latitude, lat, 6);
        Assert.Equal(Boston.Longitude, lon, 6);
    }

    [Fact]
    public void Viewport_CentresThePlace_AndCoversItselfWithTiles()
    {
        var vp = RadarViewport.For(Boston, 8);

        Assert.Equal(7, vp.DataZoom);
        Assert.Equal(880, vp.DataWidth);
        Assert.Equal(380, vp.DataHeight);
        var (cx, cy) = vp.Project(Boston.Latitude, Boston.Longitude);
        Assert.Equal(880, cx, 3);
        Assert.Equal(380, cy, 3);

        var tiles = vp.Tiles().ToList();
        Assert.InRange(tiles.Count, 8, 15);
        Assert.True(tiles.Min(t => t.OffsetX) <= 0 && tiles.Min(t => t.OffsetX) > -256);
        Assert.True(tiles.Min(t => t.OffsetY) <= 0 && tiles.Min(t => t.OffsetY) > -256);
        Assert.True(tiles.Max(t => t.OffsetX) + 256 >= vp.DataWidth);
        Assert.True(tiles.Max(t => t.OffsetY) + 256 >= vp.DataHeight);
        Assert.All(tiles, t => Assert.InRange(t.X, 0, 127));

        var (minX, minY, maxX, maxY) = vp.MercatorBounds();
        var expectedWidth = 1760 * WebMercator.MetersPerPixel(8);
        Assert.Equal(expectedWidth, maxX - minX, 1);
        Assert.Equal(expectedWidth * 760 / 1760, maxY - minY, 1);

        var (lonMin, latMin, lonMax, latMax) = vp.Bounds();
        Assert.True(lonMin < Boston.Longitude && Boston.Longitude < lonMax);
        Assert.True(latMin < Boston.Latitude && Boston.Latitude < latMax);
    }

    [Theory]
    [InlineData(RadarZoom.Metro, 9)]
    [InlineData(RadarZoom.State, 8)]
    [InlineData(RadarZoom.Regional, 7)]
    [InlineData(RadarZoom.Wide, 6)]
    public void MapAreas_MapToZoomLevels(RadarZoom area, int zoom)
    {
        Assert.Equal(zoom, WebMercator.ZoomOf(area));
    }

    // ---- NEXRAD -------------------------------------------------------------------------------

    [Fact]
    public void Nexrad_CoversTheLower48_NotTokyoOrHonolulu()
    {
        var source = new NexradSource(null!);

        Assert.True(source.Covers(Boston.Latitude, Boston.Longitude));
        Assert.True(source.Covers(39.74, -104.99));
        Assert.False(source.Covers(35.68, 139.69));
        Assert.False(source.Covers(21.3, -157.8));
        Assert.False(source.Covers(61.2, -149.9));
    }

    [Fact]
    public async Task Nexrad_AsksForFiveMinuteFrames_AFewMinutesBehindTheClock()
    {
        var source = new NexradSource(null!);

        Assert.Equal(new DateTime(2026, 10, 26, 21, 30, 0, DateTimeKind.Utc), NexradSource.NewestFrame(Now));
        var times = await source.FrameTimesAsync(Now, 10, CancellationToken.None);
        Assert.Equal(10, times.Count);
        Assert.Equal(new DateTime(2026, 10, 26, 21, 30, 0, DateTimeKind.Utc), times[^1]);
        Assert.Equal(new DateTime(2026, 10, 26, 20, 45, 0, DateTimeKind.Utc), times[0]);
        for (var i = 1; i < times.Count; i++)
        {
            Assert.Equal(TimeSpan.FromMinutes(5), times[i] - times[i - 1]);
        }
    }

    [Fact]
    public void Nexrad_RequestsTheWindowFromTheTimeEnabledWms()
    {
        var url = NexradSource.BuildUrl(RadarViewport.For(Boston, 8), new DateTime(2026, 10, 26, 21, 30, 0, DateTimeKind.Utc));

        Assert.StartsWith("https://mesonet.agron.iastate.edu/cgi-bin/wms/nexrad/n0q-t.cgi?", url);
        Assert.Contains("LAYERS=nexrad-n0q-wmst", url);
        Assert.Contains("SRS=EPSG:900913", url);
        Assert.Contains("WIDTH=880&HEIGHT=380", url);
        Assert.Contains("TRANSPARENT=true", url);
        Assert.EndsWith("TIME=2026-10-26T21:30:00Z", url);
        var bbox = url.Split("BBOX=")[1].Split('&')[0].Split(',');
        Assert.Equal(4, bbox.Length);
        Assert.True(double.Parse(bbox[0], System.Globalization.CultureInfo.InvariantCulture) < -7_900_000);
        Assert.True(double.Parse(bbox[2], System.Globalization.CultureInfo.InvariantCulture) > -7_900_000);
    }

    // ---- RainViewer ----------------------------------------------------------------------------

    [Fact]
    public void RainViewer_ReadsThePublicIndex_OldestFirst()
    {
        var index = RainViewerSource.ParseIndex(OpenMeteoParserTests.Fixture("rainviewer-index.json"));

        Assert.Equal("https://tilecache.rainviewer.com", index.Host);
        Assert.Equal(13, index.Past.Count);
        Assert.True(index.Past[0].TimeUtc < index.Past[^1].TimeUtc);
        Assert.StartsWith("/v2/radar/", index.Past[^1].Path);
        Assert.Equal(TimeSpan.FromHours(2), index.Past[^1].TimeUtc - index.Past[0].TimeUtc);
        Assert.Equal("https://tilecache.rainviewer.com/v2/radar/abc/256/7/38/47/6/1_1.png", RainViewerSource.TileUrl(index.Host, "/v2/radar/abc", 7, 38, 47));
    }

    // ---- Timing and the sequence input --------------------------------------------------------

    [Fact]
    public void Timing_PlaysTheLoopOnceOnAShortScreen_AndRepeatsItOnALongOne()
    {
        var one = RadarTiming.Plan(11.5, 10);
        Assert.Equal(10, one.Count);
        Assert.Equal(11.5, one.Sum(e => e.Seconds), 6);
        Assert.True(one[^1].Seconds > one[0].Seconds, "the newest frame holds");
        Assert.Equal(Enumerable.Range(0, 10), one.Select(e => e.Frame));

        var four = RadarTiming.Plan(31.5, 10);
        Assert.Equal(40, four.Count);
        Assert.Equal(31.5, four.Sum(e => e.Seconds), 6);
        Assert.Equal(9, four[9].Frame);
        Assert.Equal(0, four[10].Frame);

        Assert.Single(RadarTiming.Plan(10, 1));
        Assert.Empty(RadarTiming.Plan(10, 0));
    }

    [Fact]
    public void ConcatList_QuotesPathsTheWayTheDemuxerReadsThem()
    {
        var text = ConcatList.Build(new[] { ("/a/b.png", 0.889), ("/c/it's.png", 1.5) });

        Assert.StartsWith("ffconcat version 1.0\n", text);
        Assert.Contains("file '/a/b.png'\nduration 0.889\n", text);
        Assert.Contains("file '/c/it'\\''s.png'\nduration 1.5\n", text);
    }

    [Fact]
    public void FrameFiles_AreNamedBySourceZoomAndTime()
    {
        var time = new DateTime(2026, 10, 26, 21, 30, 0, DateTimeKind.Utc);
        var unix = new DateTimeOffset(time).ToUnixTimeSeconds();

        Assert.Equal("nexrad-state-" + unix + ".png", RadarService.FrameFileName("nexrad", RadarZoom.State, time));
    }

    [Fact]
    public void Ffmpeg_ReadsASequenceSlideThroughTheConcatDemuxer_AndFillsItToTheFrameRate()
    {
        var seg = new WeatherSegment
        {
            StartUtc = Now,
            EndUtc = Now.AddSeconds(30),
            Slides = new[]
            {
                new WeatherSlide("/data/weather/screens/current.png", 10, -14400),
                new WeatherSlide("/data/weather/screens/seq-abc.txt", 10, -14400, IsSequence: true),
                new WeatherSlide("/data/weather/screens/hourly.png", 10, -14400)
            },
            Audio = Array.Empty<WeatherAudio>(),
            Credits = Array.Empty<WeatherCredit>(),
            FontPath = "/data/JellyStations/assets/Inter-SemiBold.ttf"
        };

        var args = FfmpegArgsBuilder.ForWeather(seg, TimeSpan.Zero, new EncodingProfile { Width = 1280, Height = 720, Fps = 30 });

        var i = args.IndexOf("/data/weather/screens/seq-abc.txt");
        Assert.Equal(new[] { "-f", "concat", "-safe", "0", "-i" }, args.Skip(i - 5).Take(5));
        var j = args.IndexOf("/data/weather/screens/current.png");
        Assert.Equal(new[] { "-framerate", "30", "-i" }, args.Skip(j - 3).Take(3));
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[1:v]scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,format=yuv420p,fps=30,settb=1/30,setpts=N/(30*TB),trim=end=10.5[s1];", filter);
        Assert.Contains("[0:v]scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,format=yuv420p,loop=loop=-1:size=1:start=0,", filter);
    }

    // ---- The cycle -------------------------------------------------------------------------------

    [Fact]
    public void Cycle_PutsRadarRightAfterCurrentConditions_OnlyWherePlacesHaveIt()
    {
        var config = new WeatherConfig { ShowRadar = true, ShowForecastRadar = false };

        var all = WeatherCycle.Pass(config, 2);
        Assert.Equal(9, all.Count);
        Assert.Equal(new[] { WeatherScreenKind.Current, WeatherScreenKind.Radar, WeatherScreenKind.Hourly, WeatherScreenKind.Daily }, all.Take(4).Select(s => s.Kind));
        Assert.Equal(1, all[5].LocationIndex);
        Assert.Equal(WeatherScreenKind.Radar, all[5].Kind);

        var second = WeatherCycle.Pass(config, 2, i => i == 1);
        Assert.Equal(8, second.Count);
        Assert.Single(second, s => s.Kind == WeatherScreenKind.Radar);
        Assert.Equal(1, second.Single(s => s.Kind == WeatherScreenKind.Radar).LocationIndex);

        Assert.Equal(7, WeatherCycle.Pass(new WeatherConfig { ShowRadar = false, ShowForecastRadar = false }, 2).Count);
        Assert.DoesNotContain(WeatherCycle.ShortPass(config, 2), s => s.Kind == WeatherScreenKind.Radar);

        var timeline = WeatherCycle.Timeline(config, 1, Now, Now.AddSeconds(40), Now, TimeZoneInfo.Utc, _ => true);
        Assert.Equal(WeatherScreenKind.Radar, timeline[1].Screen.Kind);
    }

    [Fact]
    public void Cycle_PutsForecastRadarAfterTheRadar_OnlyWherePlacesHaveIt()
    {
        var config = new WeatherConfig { ShowRadar = true, ShowForecastRadar = true };

        var all = WeatherCycle.Pass(config, 2);
        Assert.Equal(11, all.Count);
        Assert.Equal(new[] { WeatherScreenKind.Current, WeatherScreenKind.Radar, WeatherScreenKind.ForecastRadar, WeatherScreenKind.Hourly, WeatherScreenKind.Daily }, all.Take(5).Select(s => s.Kind));

        // Tokyo (index 1) has a past loop from RainViewer but no HRRR forecast.
        var mixed = WeatherCycle.Pass(config, 2, _ => true, i => i == 0);
        Assert.Equal(10, mixed.Count);
        Assert.Equal(0, mixed.Single(s => s.Kind == WeatherScreenKind.ForecastRadar).LocationIndex);

        // Forecast without the past loop still sits right after current conditions.
        var forecastOnly = WeatherCycle.Pass(new WeatherConfig { ShowRadar = false, ShowForecastRadar = true }, 1);
        Assert.Equal(new[] { WeatherScreenKind.Current, WeatherScreenKind.ForecastRadar, WeatherScreenKind.Hourly, WeatherScreenKind.Daily }, forecastOnly.Select(s => s.Kind));
        Assert.DoesNotContain(WeatherCycle.ShortPass(config, 2), s => s.Kind == WeatherScreenKind.ForecastRadar);

        var timeline = WeatherCycle.Timeline(config, 1, Now, Now.AddSeconds(40), Now, TimeZoneInfo.Utc, _ => false, _ => true);
        Assert.Equal(WeatherScreenKind.ForecastRadar, timeline[1].Screen.Kind);
    }

    // ---- The HRRR forecast ---------------------------------------------------------------------

    [Fact]
    public void Hrrr_NamesTilesByRunAndForecastMinute_AndProbesTheLastFewHours()
    {
        var run = new DateTime(2026, 10, 27, 1, 0, 0, DateTimeKind.Utc);

        Assert.Equal("hrrr::REFD-F0045-202610270100", HrrrSource.LayerName(run, 45));
        Assert.Equal("https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/hrrr::REFD-F1080-202610270100/7/37/48.png", HrrrSource.TileUrl(run, 1080, 7, 37, 48));

        var candidates = HrrrSource.RunCandidates(new DateTime(2026, 10, 27, 2, 44, 0, DateTimeKind.Utc)).ToList();
        Assert.Equal(HrrrSource.ProbeHours + 1, candidates.Count);
        Assert.Equal(new DateTime(2026, 10, 27, 2, 0, 0, DateTimeKind.Utc), candidates[0]);
        Assert.Equal(run, candidates[1]);
        Assert.All(candidates, c => Assert.Equal(0, c.Minute));

        var source = new HrrrSource(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<HrrrSource>.Instance);
        Assert.True(source.Covers(Boston.Latitude, Boston.Longitude));
        Assert.True(source.Covers(39.74, -104.99));
        Assert.False(source.Covers(35.68, 139.69));
        Assert.False(source.Covers(21.31, -157.86));
        Assert.Equal(1080, source.MaxMinutes);
    }

    [Fact]
    public void ForecastMinutes_RunFromTheStepBeforeNow_ToTheLookAheadPlusTheNextRun()
    {
        var run = new DateTime(2026, 10, 27, 1, 0, 0, DateTimeKind.Utc);
        var now = run.AddMinutes(52);

        var minutes = RadarService.ForecastMinutes(run, now, hours: 3, stepMinutes: 15, maxMinutes: 1080);
        Assert.Equal(45, minutes[0]);
        Assert.Equal(15, minutes[1] - minutes[0]);
        Assert.Equal(360, minutes[^1]);   // 52 + 180 + 120 = 352, rounded up to the step
        Assert.Equal(22, minutes.Count);

        var hourly = RadarService.ForecastMinutes(run, now, 18, 60, 1080);
        Assert.Equal(0, hourly[0]);
        Assert.Equal(1080, hourly[^1]);

        Assert.Equal(0, RadarService.ForecastMinutes(run, run.AddMinutes(-5), 1, 15, 1080)[0]);
        Assert.Empty(RadarService.ForecastMinutes(run, now, 1, 0, 1080));
    }

    [Fact]
    public void ForecastLoop_PlaysFromNowOutToTheLookAhead_AtTheStationsStep()
    {
        var run = new DateTime(2026, 10, 27, 1, 0, 0, DateTimeKind.Utc);
        var frames = Enumerable.Range(0, 25).Select(i => new RadarFrame(run.AddMinutes(i * 15), "f" + i)).ToList();
        var loop = new RadarLoop("hrrr", "HRRR", "credit", RadarZoom.State, frames, run.AddMinutes(50), run);
        Assert.True(loop.IsForecast);

        var now = run.AddMinutes(52);
        var play = loop.ForecastFrames(now, 3, 15);
        Assert.Equal(13, play.Count);
        Assert.Equal(run.AddMinutes(45), play[0].TimeUtc);
        Assert.Equal(run.AddMinutes(225), play[^1].TimeUtc);

        var coarse = loop.ForecastFrames(now, 3, 30);
        Assert.Equal(run.AddMinutes(30), coarse[0].TimeUtc);
        Assert.All(coarse, f => Assert.Equal(0, (int)(f.TimeUtc - run).TotalMinutes % 30));
        Assert.Equal(7, coarse.Count);

        // Frames beyond what was fetched are simply not there; a stale run yields nothing to play.
        Assert.Equal(22, loop.ForecastFrames(now, 18, 15).Count);
        Assert.Empty(loop.ForecastFrames(run.AddHours(9), 3, 15));
        Assert.Empty(new RadarLoop("nexrad", "n", "a", RadarZoom.State, frames, now).ForecastFrames(now, 3, 15));
    }

    [Fact]
    public void Config_CoarsensTheForecastStep_SoTheLoopStaysUnder25Frames()
    {
        var c = new WeatherConfig { ForecastRadarHours = 3, ForecastRadarStepMinutes = 15 };
        c.Normalize();
        Assert.Equal(15, c.ForecastRadarStepMinutes);
        Assert.Equal(13, c.ForecastRadarFrameCount);

        c = new WeatherConfig { ForecastRadarHours = 6, ForecastRadarStepMinutes = 15 };
        c.Normalize();
        Assert.Equal(15, c.ForecastRadarStepMinutes);
        Assert.Equal(25, c.ForecastRadarFrameCount);

        c = new WeatherConfig { ForecastRadarHours = 12, ForecastRadarStepMinutes = 15 };
        c.Normalize();
        Assert.Equal(30, c.ForecastRadarStepMinutes);

        c = new WeatherConfig { ForecastRadarHours = 18, ForecastRadarStepMinutes = 15 };
        c.Normalize();
        Assert.Equal(60, c.ForecastRadarStepMinutes);
        Assert.Equal(19, c.ForecastRadarFrameCount);

        c = new WeatherConfig { ForecastRadarHours = 40, ForecastRadarStepMinutes = 20 };
        c.Normalize();
        Assert.Equal(18, c.ForecastRadarHours);
        Assert.Equal(60, c.ForecastRadarStepMinutes);

        c = new WeatherConfig { ForecastRadarHours = 0, ForecastRadarStepMinutes = 0 };
        c.Normalize();
        Assert.Equal(1, c.ForecastRadarHours);
        Assert.Equal(15, c.ForecastRadarStepMinutes);
    }

    [Fact]
    public void ForecastFrameFiles_AreNamedByRunAndMinute()
    {
        var run = new DateTime(2026, 10, 27, 1, 0, 0, DateTimeKind.Utc);
        Assert.Equal("hrrr-metro-202610270100-f0045.png", RadarService.ForecastFrameFileName("hrrr", RadarZoom.Metro, run, 45));
        Assert.Equal("hrrr-state-202610270100-f1080.png", RadarService.ForecastFrameFileName("hrrr", RadarZoom.State, run, 1080));
    }

    // ---- The base map and the painted screen --------------------------------------------------

    [Fact]
    public void BaseMap_HasTheCoastsBordersAndTownsAroundBoston()
    {
        var map = BaseMap.Instance;
        var bounds = RadarViewport.For(Boston, 8).Bounds();

        Assert.True(map.Land.Count > 500, "land rings: " + map.Land.Count);
        Assert.NotEmpty(BaseMap.Within(map.Land, bounds));
        Assert.NotEmpty(BaseMap.Within(map.StateBorders, bounds));
        Assert.NotEmpty(BaseMap.Within(map.CountryBorders, RadarViewport.For(new WeatherLocation { Latitude = 45.0, Longitude = -73.5 }, 7).Bounds()));
        var towns = map.PlacesWithin(bounds).ToList();
        Assert.Contains(towns, t => t.Name == "Boston");
        Assert.Contains(towns, t => t.Name == "Providence");
        Assert.True(map.Places.Count > 3000, "places: " + map.Places.Count);
    }

    [Fact]
    public void Painter_DrawsTheRadarScreen_WithAndWithoutAFrame()
    {
        var dir = Path.Combine(Path.GetTempPath(), "JellyStationsTests", "radar");
        Directory.CreateDirectory(dir);
        var overlay = Path.Combine(dir, "overlay.png");
        using (var bitmap = new SKBitmap(880, 380))
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var green = new SKPaint { Color = new SKColor(0x02, 0xFD, 0x02, 0xC0), IsAntialias = true };
            using var yellow = new SKPaint { Color = new SKColor(0xFD, 0xF8, 0x02, 0xC0), IsAntialias = true };
            using var red = new SKPaint { Color = new SKColor(0xFD, 0x00, 0x00, 0xC0), IsAntialias = true };
            canvas.DrawCircle(300, 200, 120, green);
            canvas.DrawCircle(320, 190, 70, yellow);
            canvas.DrawCircle(330, 180, 30, red);
            canvas.DrawOval(new SKRect(500, 60, 760, 160), green);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(overlay);
            data.SaveTo(file);
        }

        var assets = new WeatherAssets(Path.Combine(Path.GetTempPath(), "JellyStationsTests", "assets"));
        var painter = new WeatherPainter(assets);
        var report = OpenMeteoProvider.Parse(OpenMeteoParserTests.Fixture("open-meteo-boston.json"), Now);
        var places = new List<PlaceWeather> { new(Boston, report, false) };
        var config = new WeatherConfig { ShowLogo = false, RadarZoom = RadarZoom.State };
        var frame = new RadarFrameRequest("nexrad", "NOAA NEXRAD via Iowa Environmental Mesonet", "Radar: NOAA NEXRAD, Iowa Environmental Mesonet", Now.AddMinutes(-7), overlay, 7, 10, 8);

        foreach (var theme in new[] { WeatherTheme.Classic, WeatherTheme.Dark, WeatherTheme.Light })
        {
            var request = new ScreenRequest(WeatherScreenKind.Radar, places[0], places, new WeatherConfig { ShowLogo = false, RadarZoom = RadarZoom.State, Theme = theme }, Now, TimeZoneInfo.Utc, null, 1920, 1080, "Weather data by Open-Meteo.com", frame);
            var path = Path.Combine(dir, "radar-" + theme.ToString().ToLowerInvariant() + ".png");
            painter.Paint(request, path);
            Assert.True(new FileInfo(path).Length > 40_000, theme + ": " + new FileInfo(path).Length + " bytes");
        }

        var metro = new ScreenRequest(WeatherScreenKind.Radar, places[0], places, config, Now, TimeZoneInfo.Utc, null, 1280, 720, "Weather data by Open-Meteo.com", frame with { Zoom = 9, Index = 9 });
        painter.Paint(metro, Path.Combine(dir, "radar-metro-720.png"));
        Assert.True(File.Exists(Path.Combine(dir, "radar-metro-720.png")));

        var none = new ScreenRequest(WeatherScreenKind.Radar, places[0], places, config, Now, TimeZoneInfo.Utc, null, 1920, 1080, "Weather data by Open-Meteo.com");
        painter.Paint(none, Path.Combine(dir, "radar-none.png"));
        Assert.True(new FileInfo(Path.Combine(dir, "radar-none.png")).Length > 10_000);

        var a = WeatherScreenRenderer.FileName(new ScreenRequest(WeatherScreenKind.Radar, places[0], places, config, Now, TimeZoneInfo.Utc, null, 1920, 1080, "x", frame));
        var b = WeatherScreenRenderer.FileName(new ScreenRequest(WeatherScreenKind.Radar, places[0], places, config, Now, TimeZoneInfo.Utc, null, 1920, 1080, "x", frame with { Index = 8 }));
        var c = WeatherScreenRenderer.FileName(new ScreenRequest(WeatherScreenKind.Radar, places[0], places, config, Now, TimeZoneInfo.Utc, null, 1920, 1080, "x", frame with { TimeUtc = Now.AddMinutes(-12) }));
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);

        // The forecast screen: same map, "FORECAST RADAR" header, the run in the credit; its own cache key per run.
        var run = new DateTime(2026, 10, 26, 21, 0, 0, DateTimeKind.Utc);
        var forecast = new RadarFrameRequest("hrrr", "NOAA HRRR via Iowa Environmental Mesonet", "Forecast radar: NOAA HRRR, Iowa Environmental Mesonet", Now.AddMinutes(128), overlay, 12, 13, 8, run);
        var forecastRequest = new ScreenRequest(WeatherScreenKind.ForecastRadar, places[0], places, config, Now, TimeZoneInfo.Utc, null, 1920, 1080, "Weather data by Open-Meteo.com", forecast);
        painter.Paint(forecastRequest, Path.Combine(dir, "forecast-radar.png"));
        Assert.True(new FileInfo(Path.Combine(dir, "forecast-radar.png")).Length > 40_000);
        painter.Paint(new ScreenRequest(WeatherScreenKind.ForecastRadar, places[0], places, config, Now, TimeZoneInfo.Utc, null, 1920, 1080, "x"), Path.Combine(dir, "forecast-none.png"));
        Assert.True(File.Exists(Path.Combine(dir, "forecast-none.png")));

        var f1 = WeatherScreenRenderer.FileName(forecastRequest);
        var f2 = WeatherScreenRenderer.FileName(forecastRequest with { Radar = forecast with { RunUtc = run.AddHours(1) } });
        Assert.NotEqual(f1, f2);
        Assert.NotEqual(f1, WeatherScreenRenderer.FileName(forecastRequest with { Kind = WeatherScreenKind.Radar }));
    }
}
