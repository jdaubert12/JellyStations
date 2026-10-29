using System.Text.Json;
using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// What Export writes and Import reads: the stations plus the saved station and block templates. Import also
/// accepts the pre-1.21 shapes: a bare array of stations, or one station (the example files).
/// </summary>
public sealed class StationExportDocument
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public List<Station> Stations { get; set; } = new();

    public List<StationTemplate> StationTemplates { get; set; } = new();

    public List<SlotBlock> BlockTemplates { get; set; } = new();

    /// <summary>Parses either shape; null when the JSON is neither an array nor an object.</summary>
    public static StationExportDocument? Parse(JsonElement json, JsonSerializerOptions options)
    {
        if (json.ValueKind == JsonValueKind.Array)
        {
            return new StationExportDocument { Version = 1, Stations = json.Deserialize<List<Station>>(options) ?? new List<Station>() };
        }

        if (json.ValueKind == JsonValueKind.Object)
        {
            // A single station (the example files are one station each) has no Stations / template lists.
            if (!HasProperty(json, "Stations") && !HasProperty(json, "StationTemplates") && !HasProperty(json, "BlockTemplates"))
            {
                var station = json.Deserialize<Station>(options);
                return new StationExportDocument { Version = 1, Stations = station is null ? new List<Station>() : new List<Station> { station } };
            }

            var doc = json.Deserialize<StationExportDocument>(options) ?? new StationExportDocument();
            doc.Stations ??= new List<Station>();
            doc.StationTemplates ??= new List<StationTemplate>();
            doc.BlockTemplates ??= new List<SlotBlock>();
            return doc;
        }

        return null;
    }

    private static bool HasProperty(JsonElement json, string name) =>
        json.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
