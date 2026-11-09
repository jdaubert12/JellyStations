using Jellyfin.Plugin.JellyStations.Scheduling;

namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>
/// The previous schedule's last stretch: what the station plays from the moment a revision was created until the
/// new timeline begins. The program on the air finishes, its break plays out, and the new schedule starts at the
/// next program boundary on the padding grid (see <see cref="Handovers.Plan"/>). Frozen into the compiled station
/// so a restart in between serves the same entries.
/// </summary>
public sealed class Handover
{
    /// <summary>When the revision was created (the air-log snapshot is taken at the same instant).</summary>
    public DateTime FromUtc { get; set; }

    /// <summary>When the new timeline begins; the entries below cover [their first start, this).</summary>
    public DateTime UntilUtc { get; set; }

    /// <summary>The previous schedule's entries that still play, contiguous and ordered; the first may have started before <see cref="FromUtc"/>.</summary>
    public List<ScheduleEntry> Entries { get; set; } = new();
}
