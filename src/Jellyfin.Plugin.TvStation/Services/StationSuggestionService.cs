using System.Globalization;
using Jellyfin.Plugin.TvStation.Api.Dto;
using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Storage;

namespace Jellyfin.Plugin.TvStation.Services;

/// <summary>Lists station suggestions for the current library and creates the chosen ones.</summary>
public sealed class StationSuggestionService
{
    private readonly LibraryAnalysisService _analysis;
    private readonly StationRepository _stations;
    private readonly StationService _service;

    public StationSuggestionService(LibraryAnalysisService analysis, StationRepository stations, StationService service)
    {
        _analysis = analysis;
        _stations = stations;
        _service = service;
    }

    public List<StationSuggestionDto> List(bool refresh = false, double? minHours = null, int? minEpisodes = null)
    {
        return StationSuggester.Suggest(_analysis.Get(refresh), _stations.GetAll().Select(s => s.Name), Thresholds(minHours, minEpisodes));
    }

    /// <summary>Default thresholds, or lowered ones for a small library (minHours applies to every hours rule).</summary>
    public static SuggestionThresholds Thresholds(double? minHours, int? minEpisodes)
    {
        var t = new SuggestionThresholds();
        if (minHours is > 0)
        {
            var h = Math.Max(0.01, minHours.Value);
            t.GenreMinHours = h;
            t.KidsMinHours = h;
            t.FamilyMoviesMinHours = h;
            t.DecadeMinHours = h;
            t.SeasonalMinHours = Math.Min(t.SeasonalMinHours, h);
        }

        if (minEpisodes is > 0)
        {
            t.SeriesMinEpisodes = minEpisodes.Value;
        }

        return t;
    }

    /// <summary>Creates the suggestions with the given keys, numbering them after the highest existing channel.</summary>
    public SuggestionCreateResultDto Create(IEnumerable<string> keys, string? timeZoneId, double? minHours = null, int? minEpisodes = null)
    {
        var result = new SuggestionCreateResultDto();
        var suggestions = List(false, minHours, minEpisodes).ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var existing = _stations.GetAll();
        var next = NextChannelNumber(existing);
        var tz = !string.IsNullOrWhiteSpace(timeZoneId) ? timeZoneId.Trim() : existing.Select(s => s.TimeZoneId).FirstOrDefault(z => !string.IsNullOrWhiteSpace(z)) ?? "UTC";

        foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase))
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

            var draft = suggestion.Station;
            draft.Id = Guid.Empty;
            draft.ChannelNumber = next.ToString(CultureInfo.InvariantCulture);
            draft.TimeZoneId = tz;
            try
            {
                _service.Create(draft);
                result.Created.Add(suggestion.Name);
                next++;
            }
            catch (ValidationException ex)
            {
                result.Errors.Add(suggestion.Name + ": " + string.Join(" ", ex.Errors));
            }
        }

        return result;
    }

    public static int NextChannelNumber(IEnumerable<Models.Station> stations)
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
