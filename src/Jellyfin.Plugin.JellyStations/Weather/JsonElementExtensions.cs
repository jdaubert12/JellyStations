using System.Text.Json;

namespace Jellyfin.Plugin.JellyStations.Weather;

/// <summary>Lenient reads of the weather services' JSON: a missing or wrongly typed value reads as absent rather than throwing.</summary>
public static class JsonElementExtensions
{
    /// <summary>The named property when it is a string; null otherwise.</summary>
    public static string? StringOrNull(this JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The named property when it is a number; 0 otherwise.</summary>
    public static double NumberOrZero(this JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>The named array's numbers (null for entries that are not numbers); null when there is no such array.</summary>
    public static List<double?>? NumberArray(this JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : (double?)null).ToList();
    }

    /// <summary>The named array's strings (null for entries that are not strings); null when there is no such array.</summary>
    public static List<string?>? StringArray(this JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList();
    }
}
