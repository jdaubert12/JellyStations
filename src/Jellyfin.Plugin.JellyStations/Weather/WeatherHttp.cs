using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyStations.Weather;

/// <summary>A text reply: the status line and the body, for the caller to judge.</summary>
public readonly record struct HttpTextReply(HttpStatusCode StatusCode, string? ReasonPhrase, string Body)
{
    /// <summary>True for a 2xx status.</summary>
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}

/// <summary>The plumbing the weather, alert and radar sources share: a request that names the plugin, and plain GETs.</summary>
public static class WeatherHttp
{
    /// <summary>The plugin's version in three parts, as the User-Agent carries it.</summary>
    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";

    /// <summary>A GET that identifies the plugin (as the NWS asks every caller to) and accepts the given media types, in order.</summary>
    public static HttpRequestMessage CreateGet(string url, params string[] accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-JellyStations", Version));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/jdaubert12/JellyStations)"));
        foreach (var type in accept)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        }

        return request;
    }

    /// <summary>GETs <paramref name="url"/> with the named client and reads the body whatever the status.</summary>
    public static async Task<HttpTextReply> GetStringAsync(IHttpClientFactory http, string clientName, TimeSpan timeout, string url, CancellationToken ct, params string[] accept)
    {
        using var client = http.CreateClient(clientName);
        client.Timeout = timeout;
        using var request = CreateGet(url, accept);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new HttpTextReply(response.StatusCode, response.ReasonPhrase, body);
    }

    /// <summary>A string property of a JSON error body's root (Open-Meteo's "reason", the NWS's "detail"); null when the body is not JSON or lacks it.</summary>
    public static string? TryReadJsonString(string body, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
