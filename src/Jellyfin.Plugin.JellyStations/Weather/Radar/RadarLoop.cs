using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Weather.Radar;

/// <summary>One fetched radar frame: its time and the overlay PNG on disk.</summary>
public sealed record RadarFrame(DateTime TimeUtc, string Path);

/// <summary>The frames on hand for one place, oldest first, all from one source at one zoom.</summary>
public sealed class RadarLoop
{
    public RadarLoop(string sourceId, string sourceName, string attribution, RadarZoom zoom, IReadOnlyList<RadarFrame> frames, DateTime fetchedUtc)
    {
        SourceId = sourceId;
        SourceName = sourceName;
        Attribution = attribution;
        Zoom = zoom;
        Frames = frames;
        FetchedUtc = fetchedUtc;
    }

    public string SourceId { get; }

    public string SourceName { get; }

    public string Attribution { get; }

    public RadarZoom Zoom { get; }

    public IReadOnlyList<RadarFrame> Frames { get; }

    public DateTime FetchedUtc { get; }

    public RadarFrame Latest => Frames[^1];

    /// <summary>Two frames at least: one frame is a still, not a loop.</summary>
    public bool IsPlayable => Frames.Count >= 2;
}

/// <summary>How radar fetching for one place is going.</summary>
public sealed class RadarFetchStatus
{
    public Guid LocationId { get; set; }

    /// <summary>The source that covers the place, or null when none does.</summary>
    public string? SourceName { get; set; }

    public DateTime? LastAttemptUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public string? LastError { get; set; }

    public int Failures { get; set; }
}

/// <summary>
/// How a radar loop plays inside one screen: the frames in order with how long each stays. Short screens play
/// the loop once; longer ones repeat it, each pass about eight seconds, holding the newest frame a moment at
/// the end of every pass. The plan covers the whole span asked for, so the last frame also sits under the
/// cross-fade into the next screen.
/// </summary>
public static class RadarTiming
{
    public const double PassSeconds = 8.0;
    public const double MaxHoldSeconds = 2.0;

    public static IReadOnlyList<(int Frame, double Seconds)> Plan(double totalSeconds, int frameCount)
    {
        var plan = new List<(int, double)>();
        if (frameCount <= 0 || totalSeconds <= 0)
        {
            return plan;
        }

        if (frameCount == 1)
        {
            plan.Add((0, totalSeconds));
            return plan;
        }

        var passes = Math.Max(1, (int)Math.Round(totalSeconds / PassSeconds));
        var passLength = totalSeconds / passes;
        var hold = Math.Max(passLength / frameCount, Math.Min(MaxHoldSeconds, passLength / 4.0));
        var step = (passLength - hold) / (frameCount - 1);
        for (var p = 0; p < passes; p++)
        {
            for (var f = 0; f < frameCount; f++)
            {
                plan.Add((f, f == frameCount - 1 ? hold : step));
            }
        }

        return plan;
    }
}
