using System.Globalization;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Weather.Screens;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Services;

/// <summary>Lists station suggestions for the current library and creates the chosen ones.</summary>
public sealed class StationSuggestionService
{
    private readonly LibraryAnalysisService _analysis;
    private readonly StationRepository _stations;
    private readonly StationService _service;
    private readonly SettingsRepository _settings;
    private readonly WeatherAssets _assets;
    private readonly ILogger<StationSuggestionService> _logger;

    public StationSuggestionService(LibraryAnalysisService analysis, StationRepository stations, StationService service, SettingsRepository settings, WeatherAssets assets, ILogger<StationSuggestionService> logger)
    {
        _analysis = analysis;
        _stations = stations;
        _service = service;
        _settings = settings;
        _assets = assets;
        _logger = logger;
    }

    /// <summary>Every suggestion, the ones the channels already cover included (see <see cref="StationSuggestionDto.Covered"/>).</summary>
    public List<StationSuggestionDto> List(bool refresh = false, double? minHours = null, int? minEpisodes = null)
        => Report(refresh, minHours, minEpisodes).Suggestions;

    /// <summary>
    /// The rules run over the library with the thresholds from Settings, set against what the enabled stations
    /// already play: each suggestion's new hours, the fully covered ones flagged, the "Everything Else" station for
    /// what no channel plays, the rules that fell just short, and the clip libraries to offer as bumpers. The week
    /// layouts (built-in and saved) are filled from the library and offered first.
    /// </summary>
    public SuggestionReportDto Report(bool refresh = false, double? minHours = null, int? minEpisodes = null)
    {
        var analysis = _analysis.Get(refresh);
        var facts = _analysis.Facts();
        var settings = _settings.Get();
        var thresholds = Thresholds(settings.Suggestions, minHours, minEpisodes);
        var names = _stations.GetAll().Select(s => s.Name).ToList();
        var set = StationSuggester.Build(analysis, names, thresholds, WeekLayouts.Merge(settings.WeekLayouts));
        var (onChannels, channels) = _service.ItemsOnChannels();
        SuggestionCoverage.Apply(set.Suggestions, new FactIndex(facts, _analysis.Collections()), onChannels, settings.ExcludedTags);
        var leftovers = SuggestionCoverage.Leftovers(facts, LibraryAnalyzer.ClipLibraryIds(analysis.Libraries), onChannels, channels, thresholds.LeftoversMinHours, names, settings.ExcludedTags);
        if (leftovers is not null)
        {
            set.Suggestions.Add(leftovers);
        }

        return new SuggestionReportDto
        {
            Suggestions = set.Suggestions,
            NearMisses = set.NearMisses,
            ClipLibraries = analysis.Libraries.Where(l => l.ClipLibrary).ToList(),
            Thresholds = thresholds,
            Channels = channels
        };
    }

    /// <summary>
    /// The thresholds from Settings, or lowered ones when a caller asks (minHours applies to every hours rule,
    /// minEpisodes to the episodes a series needs).
    /// </summary>
    public static SuggestionThresholds Thresholds(SuggestionThresholds? saved, double? minHours, int? minEpisodes)
    {
        var t = JsonFileStore.Clone(saved ?? new SuggestionThresholds());
        t.Normalize();
        if (minHours is > 0)
        {
            var h = Math.Max(0.01, minHours.Value);
            t.GenreMinHours = h;
            t.KidsMinHours = h;
            t.FamilyMoviesMinHours = h;
            t.DecadeMinHours = h;
            t.ClassicsMinHours = h;
            t.ComfortMinHours = h;
            t.LeftoversMinHours = Math.Min(t.LeftoversMinHours, h);
            t.SeasonalMinHours = Math.Min(t.SeasonalMinHours, h);
        }

        if (minEpisodes is > 0)
        {
            t.SeriesMinEpisodes = minEpisodes.Value;
        }

        return t;
    }

    /// <summary>
    /// One suggestion as an unsaved draft with the options applied (number, time zone, template look), for the
    /// editor. Null when there is no such suggestion.
    /// </summary>
    public Station? Draft(SuggestionDraftRequestDto request)
    {
        var suggestion = List(false, request.MinHours, request.MinEpisodes).FirstOrDefault(s => string.Equals(s.Key, request.Key, StringComparison.OrdinalIgnoreCase));
        if (suggestion is null)
        {
            return null;
        }

        var existing = _stations.GetAll();
        var used = UsedNumbers(existing);
        var draft = Prepare(suggestion.Station, NextFree(request.FirstChannelNumber ?? NextChannelNumber(existing), used), TimeZone(request.TimeZoneId, existing), Template(request.TemplateId), BumperSource(request.BumperLibraryId));
        return draft;
    }

    /// <summary>
    /// What one suggestion would air between two instants if it were created now with the same options. Nothing is
    /// saved. Null when there is no such suggestion. A shuffle is seeded by the station id, which a suggestion only
    /// gets when it is created, so this is a sample of the channel (its mix, lengths and breaks) and not the exact
    /// order of its first day.
    /// </summary>
    public IReadOnlyList<ScheduleEntry>? Preview(SuggestionDraftRequestDto request, DateTime fromUtc, DateTime toUtc)
    {
        var draft = Draft(request);
        return draft is null ? null : _service.Preview(draft, fromUtc, toUtc);
    }

    /// <summary>
    /// Creates the suggestions with the given keys: numbered from the first channel number (or after the highest
    /// existing channel), in the time zone asked for, with a template's look and a drawn logo when asked.
    /// </summary>
    public SuggestionCreateResultDto Create(SuggestionCreateRequestDto request)
    {
        var result = new SuggestionCreateResultDto();
        var suggestions = List(false, request.MinHours, request.MinEpisodes).ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var existing = _stations.GetAll();
        var used = UsedNumbers(existing);
        var next = request.FirstChannelNumber ?? NextChannelNumber(existing);
        var tz = TimeZone(request.TimeZoneId, existing);
        var template = Template(request.TemplateId);
        if (request.TemplateId is { } id && id != Guid.Empty && template is null)
        {
            result.Errors.Add("No station template has the id " + id.ToString("N") + "; the stations were created plain.");
        }

        var bumpers = BumperSource(request.BumperLibraryId);
        if (request.BumperLibraryId is { } lib && lib != Guid.Empty && bumpers is null)
        {
            result.Errors.Add("No library has the id " + lib.ToString("N") + "; the stations were created without its clips as bumpers.");
        }

        foreach (var key in (request.Keys ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!suggestions.TryGetValue(key, out var suggestion))
            {
                result.Errors.Add(key + ": no such suggestion (the library may have changed; reload the list).");
                continue;
            }

            if (suggestion.Exists)
            {
                result.Errors.Add(suggestion.Name + ": a station with this name already exists.");
                continue;
            }

            next = NextFree(next, used);
            var draft = Prepare(suggestion.Station, next, tz, template, bumpers);
            Station created;
            try
            {
                created = _service.Create(draft);
                result.Created.Add(suggestion.Name);
                used.Add(next);
                next++;
            }
            catch (ValidationException ex)
            {
                result.Errors.Add(suggestion.Name + ": " + string.Join(" ", ex.Errors));
                continue;
            }

            if (request.Logo && !DrawLogo(created, template))
            {
                result.Warnings.Add(suggestion.Name + ": created, but the logo could not be drawn.");
            }
        }

        return result;
    }

    /// <summary>
    /// The suggester's station with its identity filled in, with a template that template's look, and with a
    /// bumper library its clips in the breaks (see <see cref="UseBumpers"/>).
    /// </summary>
    private static Station Prepare(Station suggested, int number, string timeZoneId, StationTemplate? template, ContentSource? bumpers)
    {
        var draft = JsonFileStore.Clone(suggested);
        draft.Id = Guid.Empty;
        draft.ChannelNumber = number.ToString(CultureInfo.InvariantCulture);
        draft.TimeZoneId = timeZoneId;
        draft.LogoFileName = null;
        if (template is not null)
        {
            StationTemplates.ApplyLook(template, draft);
        }

        if (bumpers is not null)
        {
            UseBumpers(draft, bumpers);
        }

        return draft;
    }

    /// <summary>
    /// Switches the station's bumpers on with <paramref name="clips"/> as their only source, keeping whatever else
    /// the look set (break length, clip cap, pre-roll, post-roll, weather). A station that leaves no gap between
    /// programs (no break, no padding) gets a two-minute break, or the clips would never air.
    /// </summary>
    public static void UseBumpers(Station station, ContentSource clips)
    {
        station.Bumpers ??= new BumperConfig();
        station.Bumpers.Enabled = true;
        station.Bumpers.Sources = new List<ContentSource> { JsonFileStore.Clone(clips) };
        if (station.Bumpers.BreakMinutes <= 0 && !station.PaddingByLength && station.PaddingGridMinutes <= 0)
        {
            station.Bumpers.BreakMinutes = 2;
        }
    }

    /// <summary>A Library source for the bumper library asked for, or null when none was asked for or it is not a library.</summary>
    private ContentSource? BumperSource(Guid? libraryId)
    {
        if (libraryId is not { } id || id == Guid.Empty)
        {
            return null;
        }

        var library = _analysis.Get().Libraries.FirstOrDefault(l => l.Id == id);
        return library is null ? null : new ContentSource { Kind = SourceKind.Library, ItemId = library.Id, DisplayName = library.Name };
    }

    /// <summary>Draws the generated card for a created station and stores it as its logo. False when drawing failed.</summary>
    private bool DrawLogo(Station station, StationTemplate? template)
    {
        try
        {
            var color = StationLogo.ValidColor(template?.LogoColor) ?? StationLogo.ColorForName(station.Name);
            var png = StationLogo.Png(station.Name, station.ChannelNumber, color, StationLogo.SeedText(station), _assets.Typeface(FontWeight.Bold), _assets.Typeface(FontWeight.SemiBold));
            using var ms = new MemoryStream(png);
            _service.SaveLogo(station.Id, ".png", ms);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not draw a logo for suggested station {Name}", Text.ForLog(station.Name));
            return false;
        }
    }

    private StationTemplate? Template(Guid? id)
        => id is { } t && t != Guid.Empty ? StationTemplates.Find(_settings.Get().StationTemplates, t) : null;

    private static string TimeZone(string? requested, IReadOnlyList<Station> existing)
        => !string.IsNullOrWhiteSpace(requested) ? requested.Trim() : existing.Select(s => s.TimeZoneId).FirstOrDefault(z => !string.IsNullOrWhiteSpace(z)) ?? "UTC";

    public static HashSet<int> UsedNumbers(IEnumerable<Station> stations)
    {
        var used = new HashSet<int>();
        foreach (var s in stations)
        {
            if (int.TryParse(s.ChannelNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                used.Add(n);
            }
        }

        return used;
    }

    /// <summary>The first number at or after <paramref name="from"/> (at least 1) that no station has.</summary>
    public static int NextFree(int from, ISet<int> used)
    {
        var n = Math.Max(1, from);
        while (used.Contains(n))
        {
            n++;
        }

        return n;
    }

    public static int NextChannelNumber(IEnumerable<Station> stations)
    {
        var max = 0;
        foreach (var s in stations)
        {
            if (int.TryParse(s.ChannelNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > max)
            {
                max = n;
            }
        }

        return max + 1;
    }
}
