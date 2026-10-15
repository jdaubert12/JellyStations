namespace Jellyfin.Plugin.TvStation.Scheduling;

internal static class Padding
{
    /// <summary>Rounds a duration up to the next multiple of the grid (grid of zero returns the input).</summary>
    public static TimeSpan Pad(TimeSpan runtime, TimeSpan grid)
    {
        if (grid <= TimeSpan.Zero)
        {
            return runtime;
        }

        var g = grid.Ticks;
        var padded = (runtime.Ticks + g - 1) / g * g;
        return TimeSpan.FromTicks(Math.Max(padded, g));
    }

    /// <summary>Half-hour slots for programs up to an hour, whole hours beyond that.</summary>
    public static TimeSpan GridForLength(TimeSpan runtime)
        => runtime <= TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(1);

    /// <summary>Pads to the fixed grid, or by program length when <paramref name="byLength"/> is set.</summary>
    public static TimeSpan Pad(TimeSpan runtime, TimeSpan grid, bool byLength)
        => Pad(runtime, byLength ? GridForLength(runtime) : grid);

    /// <summary>Floors a UTC instant to the grid (grid of zero returns the input).</summary>
    public static DateTime FloorToGrid(DateTime utc, TimeSpan grid)
    {
        if (grid <= TimeSpan.Zero)
        {
            return utc;
        }

        return new DateTime(utc.Ticks / grid.Ticks * grid.Ticks, DateTimeKind.Utc);
    }

    public static long FloorDiv(long a, long b)
    {
        var q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0)))
        {
            q--;
        }

        return q;
    }
}
