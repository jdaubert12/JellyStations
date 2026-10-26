namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>Unit system for a weather channel's screens.</summary>
public enum WeatherUnits
{
    /// <summary>°F, mph, inches, inHg.</summary>
    Imperial,

    /// <summary>°C, km/h, millimetres, hPa.</summary>
    Metric
}

/// <summary>How much map the radar screen shows around a place; the widths are for mid-latitudes.</summary>
public enum RadarZoom
{
    /// <summary>About 250 miles (400 km) across.</summary>
    Metro,

    /// <summary>About 500 miles (800 km) across.</summary>
    State,

    /// <summary>About 1,000 miles (1,600 km) across.</summary>
    Regional,

    /// <summary>About 2,000 miles (3,300 km) across.</summary>
    Wide
}

/// <summary>Colour scheme of the weather screens.</summary>
public enum WeatherTheme
{
    /// <summary>The deep blue gradient the format is known for.</summary>
    Classic,

    /// <summary>Near-black with light type.</summary>
    Dark,

    /// <summary>Pale background with dark type.</summary>
    Light
}

/// <summary>
/// A weather channel: current conditions and forecasts for a few places, drawn by the plugin as full-screen
/// graphics and cycled over music, in the spirit of the "Local on the 8s" format. Weather data comes from a free
/// forecast provider (Open-Meteo); the only thing ever sent is each place's latitude and longitude.
/// </summary>
public sealed class WeatherConfig
{
    public const int DefaultScreenSeconds = 10;
    public const int DefaultGuideEntryMinutes = 60;
    public const int DefaultStaleHours = 3;
    public const int MaxLocations = 8;
    public const string DefaultProvider = "open-meteo";

    /// <summary>Places to cycle through, in order. At least one.</summary>
    public List<WeatherLocation> Locations { get; set; } = new();

    public WeatherUnits Units { get; set; } = WeatherUnits.Imperial;

    /// <summary>Times as 20:00 rather than 8:00 PM.</summary>
    public bool TwentyFourHourClock { get; set; }

    /// <summary>How long each screen stays, 4-60 s.</summary>
    public int ScreenSeconds { get; set; } = DefaultScreenSeconds;

    /// <summary>Start a pass over all places at :08, :18, :28... and fill the rest of each ten minutes with current conditions.</summary>
    public bool OnTheEights { get; set; }

    public WeatherTheme Theme { get; set; } = WeatherTheme.Classic;

    /// <summary>Show the station logo in a corner of every screen.</summary>
    public bool ShowLogo { get; set; } = true;

    /// <summary>Show the "Travel cities" screen (every place on one screen) at the end of each pass. Needs two or more places.</summary>
    public bool ShowTravelCities { get; set; } = true;

    /// <summary>Show the next-12-hours screen for each place.</summary>
    public bool ShowHourly { get; set; } = true;

    /// <summary>Show the 7-day screen for each place.</summary>
    public bool ShowDaily { get; set; } = true;

    /// <summary>Show the local radar loop for each place (NEXRAD in the contiguous United States, RainViewer elsewhere).</summary>
    public bool ShowRadar { get; set; } = true;

    /// <summary>How much map the radar screen shows around the place.</summary>
    public RadarZoom RadarZoom { get; set; } = RadarZoom.State;

    /// <summary>Guide entry title; "{places}" is replaced by the place names. Null = "Local weather: {places}".</summary>
    public string? GuideTitle { get; set; }

    /// <summary>Length of each guide entry in minutes (15-240). The screens cycle inside it.</summary>
    public int GuideEntryMinutes { get; set; } = DefaultGuideEntryMinutes;

    /// <summary>After this many hours without a successful fetch the screens say the forecast is unavailable instead of showing old numbers as current (1-48).</summary>
    public int StaleHours { get; set; } = DefaultStaleHours;

    /// <summary>Forecast provider id; only <see cref="DefaultProvider"/> for now.</summary>
    public string Provider { get; set; } = DefaultProvider;

    /// <summary>Play music under the screens. Off = silence.</summary>
    public bool MusicEnabled { get; set; } = true;

    /// <summary>Jellyfin music to play: playlists, albums, artists, music genres or single tracks.</summary>
    public List<ContentSource> MusicSources { get; set; } = new();

    /// <summary>A folder of audio files to play as well (absolute path, or a name under the plugin's music folder).</summary>
    public string? MusicFolder { get; set; }

    /// <summary>Show "♫ title · artist" on the screens while a track plays.</summary>
    public bool ShowMusicCredit { get; set; } = true;

    /// <summary>Music level in percent (20-150); 100 = as recorded.</summary>
    public int MusicVolumePercent { get; set; } = 100;

    public const string DefaultGuideTitle = "Local weather: {places}";

    public string EffectiveGuideTitle => string.IsNullOrWhiteSpace(GuideTitle) ? DefaultGuideTitle : GuideTitle!;

    public void Normalize()
    {
        Locations ??= new List<WeatherLocation>();
        Locations = Locations.Where(l => l is not null).Take(MaxLocations).ToList();
        foreach (var l in Locations)
        {
            l.Normalize();
        }

        ScreenSeconds = Math.Clamp(ScreenSeconds, 4, 60);
        GuideEntryMinutes = Math.Clamp(GuideEntryMinutes, 15, 240);
        StaleHours = Math.Clamp(StaleHours, 1, 48);
        Provider = string.IsNullOrWhiteSpace(Provider) ? DefaultProvider : Provider.Trim().ToLowerInvariant();
        GuideTitle = string.IsNullOrWhiteSpace(GuideTitle) ? null : GuideTitle.Trim();
        if (GuideTitle is { Length: > 120 })
        {
            GuideTitle = GuideTitle[..120];
        }

        MusicSources ??= new List<ContentSource>();
        MusicFolder = string.IsNullOrWhiteSpace(MusicFolder) ? null : MusicFolder.Trim();
        MusicVolumePercent = Math.Clamp(MusicVolumePercent, 20, 150);
    }
}

/// <summary>One place on a weather channel. Coordinates come from the geocoding search in the editor.</summary>
public sealed class WeatherLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the screens call it ("Boston").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Second line under the name ("Massachusetts, United States"); optional.</summary>
    public string? Region { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>IANA time zone of the place, for its local clock; null = the station's zone.</summary>
    public string? TimeZoneId { get; set; }

    public void Normalize()
    {
        if (Id == Guid.Empty)
        {
            Id = Guid.NewGuid();
        }

        Name = (Name ?? string.Empty).Trim();
        if (Name.Length > 40)
        {
            Name = Name[..40];
        }

        Region = string.IsNullOrWhiteSpace(Region) ? null : Region.Trim();
        if (Region is { Length: > 60 })
        {
            Region = Region[..60];
        }

        TimeZoneId = string.IsNullOrWhiteSpace(TimeZoneId) ? null : TimeZoneId.Trim();
        Latitude = Math.Round(Latitude, 4);
        Longitude = Math.Round(Longitude, 4);
    }

    /// <summary>Named and placed: coordinates in range and not the (0, 0) a place gets before it is picked from the search.</summary>
    public bool IsValid => Name.Length > 0 && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 && !(Latitude == 0 && Longitude == 0);
}

/// <summary>An audio file a weather channel plays, frozen at compile time.</summary>
public sealed class MusicTrack
{
    /// <summary>Jellyfin item id, or a stable id derived from the path for folder files.</summary>
    public Guid Id { get; set; }

    public string Path { get; set; } = string.Empty;

    public long RuntimeTicks { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Artist { get; set; }

    /// <summary>"Playlist", "Album", "Folder"...: where it came from, for the content view.</summary>
    public string Source { get; set; } = string.Empty;

    public TimeSpan Runtime => TimeSpan.FromTicks(RuntimeTicks);
}
