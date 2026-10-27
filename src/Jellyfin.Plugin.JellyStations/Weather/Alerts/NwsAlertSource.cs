using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyStations.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Weather.Alerts;

/// <summary>A source of watches, warnings and advisories for a place. The plugin sends it a latitude and longitude and nothing else.</summary>
public interface IAlertSource
{
    string Id { get; }

    string Name { get; }

    /// <summary>Credit line the screen shows.</summary>
    string Attribution { get; }

    /// <summary>Roughly whether the source has anything to say about the place; the fetch itself is the final word.</summary>
    bool Covers(double latitude, double longitude);

    /// <summary>The alerts in force for the place now, strongest first. Throws <see cref="NotCoveredException"/> when the place is outside the source's area.</summary>
    Task<IReadOnlyList<WeatherAlert>> FetchAsync(WeatherLocation location, CancellationToken ct);
}

/// <summary>The source answered that it does not cover the place at all (not a failure to retry).</summary>
public sealed class NotCoveredException : Exception
{
    public NotCoveredException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The US National Weather Service's alerts API (https://api.weather.gov): free, no key, every watch, warning,
/// advisory and statement in force for a point, as GeoJSON. Asks for a User-Agent that names the application.
/// Covers the United States and its territories; a point outside answers 400 "out of bounds".
/// </summary>
public sealed partial class NwsAlertSource : IAlertSource
{
    public const string SourceId = "nws";
    public const string ActiveUrl = "https://api.weather.gov/alerts/active";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<NwsAlertSource> _logger;

    public NwsAlertSource(IHttpClientFactory http, ILogger<NwsAlertSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Id => SourceId;

    public string Name => "National Weather Service";

    public string Attribution => "Alerts: US National Weather Service";

    /// <summary>The states and territories: the mainland with Alaska and Hawaii, the Caribbean territories, Guam and the Marianas, American Samoa, and the Aleutians past the date line.</summary>
    public bool Covers(double latitude, double longitude)
        => (latitude is >= 17 and <= 72 && longitude is >= -180 and <= -64)
        || (latitude is >= 12 and <= 21 && longitude is >= 143 and <= 147)
        || (latitude is >= -15 and <= -10 && longitude is >= -172 and <= -168)
        || (latitude is >= 48 and <= 56 && longitude is >= 165 and <= 180);

    public static string BuildUrl(double latitude, double longitude)
        => ActiveUrl + "?point=" + latitude.ToString("0.####", CultureInfo.InvariantCulture) + "," + longitude.ToString("0.####", CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<WeatherAlert>> FetchAsync(WeatherLocation location, CancellationToken ct)
    {
        var url = BuildUrl(location.Latitude, location.Longitude);
        using var client = _http.CreateClient("JellyStations.Weather");
        client.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-JellyStations", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/jdaubert12/JellyStations)"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/geo+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TryReadDetail(body) ?? response.ReasonPhrase ?? string.Empty;
            if (response.StatusCode == HttpStatusCode.BadRequest && detail.Contains("out of bounds", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotCoveredException("The National Weather Service does not cover this place");
            }

            throw new HttpRequestException($"The National Weather Service answered {(int)response.StatusCode} {detail}".Trim(), null, response.StatusCode);
        }

        var alerts = Parse(body);
        _logger.LogDebug("JellyStations alerts: {Place} has {Count} alert(s) from the NWS", location.Name, alerts.Count);
        return alerts;
    }

    private static string? TryReadDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns an alerts reply into the plugin's alerts: real ones only (no tests, exercises or cancellations), one
    /// per id, strongest first, then the one that ends soonest. Pure, so it is tested against a recorded reply.
    /// </summary>
    public static IReadOnlyList<WeatherAlert> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<WeatherAlert>();
        if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var status = Str(p, "status");
            if (!string.Equals(status, "Actual", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var messageType = Str(p, "messageType");
            if (string.Equals(messageType, "Cancel", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = Str(p, "id") ?? string.Empty;
            if (id.Length > 0 && !seen.Add(id))
            {
                continue;
            }

            var ev = Str(p, "event") ?? string.Empty;
            if (ev.Length == 0)
            {
                continue;
            }

            list.Add(new WeatherAlert
            {
                Id = id,
                Event = ev,
                Headline = Str(p, "headline") ?? ev,
                Description = Tidy(Str(p, "description")),
                Instruction = Tidy(Str(p, "instruction")),
                Severity = Str(p, "severity"),
                Urgency = Str(p, "urgency"),
                Certainty = Str(p, "certainty"),
                Sender = Str(p, "senderName"),
                Area = Str(p, "areaDesc"),
                SentUtc = Time(p, "sent"),
                StartUtc = Time(p, "onset") ?? Time(p, "effective"),
                EndUtc = Time(p, "ends"),
                ExpiresUtc = Time(p, "expires")
            });
        }

        return list
            .OrderByDescending(a => a.SeverityRank)
            .ThenByDescending(a => KindRank(a.Event))
            .ThenBy(a => a.EndUtc ?? a.ExpiresUtc ?? DateTime.MaxValue)
            .ToList();
    }

    /// <summary>Warnings over watches over advisories over statements, for alerts of one severity.</summary>
    public static int KindRank(string ev)
    {
        if (ev.EndsWith("Warning", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (ev.EndsWith("Watch", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (ev.EndsWith("Advisory", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// NWS text is wrapped at about 70 columns with "* WHAT...Heavy snow" bullets. Paragraphs (blank-line separated)
    /// are kept, one per line; lines inside a paragraph are joined; bullets become "WHAT: Heavy snow".
    /// </summary>
    public static string? Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var paragraphs = Regex.Split(text.Replace("\r", string.Empty), @"\n\s*\n");
        var sb = new StringBuilder();
        foreach (var raw in paragraphs)
        {
            var joined = Regex.Replace(raw.Trim(), @"\s*\n\s*", " ");
            if (joined.Length == 0)
            {
                continue;
            }

            joined = BulletHeading().Replace(joined, m => m.Groups[1].Value.ToUpperInvariant() + ": ");
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(joined);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    [GeneratedRegex(@"^\*\s*([A-Za-z][A-Za-z ]{1,30}?)\.\.\.\s*")]
    private static partial Regex BulletHeading();

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>NWS times carry an offset ("2026-10-27T16:00:00-08:00"); stored as UTC.</summary>
    private static DateTime? Time(JsonElement e, string name)
    {
        var s = Str(e, name);
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) ? dto.UtcDateTime : null;
    }
}
