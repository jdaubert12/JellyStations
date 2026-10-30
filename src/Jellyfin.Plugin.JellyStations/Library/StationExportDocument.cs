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

    /// <summary>
    /// Clears the parts of an imported station's encoding profile that reach ffmpeg's command line verbatim: the extra
    /// input and output arguments and the hardware device. An admin can type these in the editor, but a file from
    /// elsewhere must not be able to add, say, a second output that writes into the plugins folder. Returns a short
    /// description of each field that was cleared, empty when there was nothing to clear.
    /// </summary>
    public static IReadOnlyList<string> StripEncoderArguments(Station station)
    {
        var encoding = station.Encoding;
        if (encoding is null)
        {
            return Array.Empty<string>();
        }

        var stripped = new List<string>();
        if (!string.IsNullOrWhiteSpace(encoding.ExtraInputArgs))
        {
            stripped.Add("extra input arguments (" + encoding.ExtraInputArgs.Trim() + ")");
            encoding.ExtraInputArgs = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(encoding.ExtraOutputArgs))
        {
            stripped.Add("extra output arguments (" + encoding.ExtraOutputArgs.Trim() + ")");
            encoding.ExtraOutputArgs = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(encoding.HardwareDevice))
        {
            stripped.Add("hardware device (" + encoding.HardwareDevice.Trim() + ")");
            encoding.HardwareDevice = string.Empty;
        }

        return stripped;
    }

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
