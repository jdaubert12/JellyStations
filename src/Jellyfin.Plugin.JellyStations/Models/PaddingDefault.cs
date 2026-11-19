namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>
/// The padding a new station starts with (<see cref="GlobalSettings.NewStationPadding"/>): the same two values a
/// station carries. It is only a starting point; the station's own padding is what airs and can be changed freely.
/// </summary>
public sealed class PaddingDefault
{
    /// <summary>Pad by program length (see <see cref="Station.PaddingByLength"/>); the grid is then ignored.</summary>
    public bool ByLength { get; set; }

    /// <summary>The fixed grid in minutes; 0 = no padding, programs play back to back.</summary>
    public int GridMinutes { get; set; }

    public void Normalize()
    {
        GridMinutes = ByLength ? 0 : Math.Max(0, GridMinutes);
    }

    /// <summary>Gives the station this padding. A weather channel has no programs to pad and is left alone.</summary>
    public void ApplyTo(Station station)
    {
        if (station.Mode == ScheduleMode.Weather)
        {
            return;
        }

        station.PaddingByLength = ByLength;
        station.PaddingGridMinutes = ByLength ? 0 : Math.Max(0, GridMinutes);
    }
}
