using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>What one weather screen shows.</summary>
public enum WeatherScreenKind
{
    /// <summary>Big temperature, icon, conditions, details, for one place.</summary>
    Current,

    /// <summary>The next twelve hours for one place.</summary>
    Hourly,

    /// <summary>Seven days for one place.</summary>
    Daily,

    /// <summary>Every place on one screen.</summary>
    Travel,

    /// <summary>The last three quarters of an hour of precipitation radar around one place, as a loop.</summary>
    Radar,

    /// <summary>Where the precipitation is forecast to go over the next hours around one place, as a loop.</summary>
    ForecastRadar,

    /// <summary>The short card that opens a place: its name and region.</summary>
    TitleCard,

    /// <summary>The watches, warnings and advisories in force for one place.</summary>
    Alerts
}

/// <summary>One screen of the pass: which place (index into the station's list, -1 for all) and what.</summary>
public sealed record CycleScreen(int LocationIndex, WeatherScreenKind Kind);

/// <summary>A screen on the clock: when it starts and ends (UTC), and whether it opens a new pass.</summary>
public sealed record TimedScreen(CycleScreen Screen, DateTime StartUtc, DateTime EndUtc, bool StartsPass)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>Which places have which optional screens: a radar loop, a forecast radar loop, an alert in force. Null = every place for the radars, no place for the alerts.</summary>
public sealed record CycleAvailability(Func<int, bool>? HasRadar = null, Func<int, bool>? HasForecastRadar = null, Func<int, bool>? HasAlerts = null)
{
    public static readonly CycleAvailability All = new();

    public bool Radar(int i) => HasRadar?.Invoke(i) ?? true;

    public bool ForecastRadar(int i) => HasForecastRadar?.Invoke(i) ?? true;

    public bool Alerts(int i) => HasAlerts?.Invoke(i) ?? false;
}

/// <summary>
/// The screen cycle of a weather channel as a pure function of the clock, so every viewer sees the same screen
/// at the same moment and a viewer tuning in lands mid-cycle like real TV. A <b>pass</b> shows every place's
/// screens in order and ends with the travel screen; passes repeat back to back from the guide entry's start.
/// With "on the 8s", passes start at :08, :18, :28... (station time) and the rest of each ten minutes repeats a
/// short pass of current conditions. Every screen stays the station's screen length except the title card,
/// which has its own shorter one.
/// </summary>
public static class WeatherCycle
{
    public static readonly TimeSpan EightsWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EightsOffset = TimeSpan.FromMinutes(8);

    /// <summary>How long a screen of this kind stays on air.</summary>
    public static TimeSpan ScreenLength(WeatherConfig c, WeatherScreenKind kind)
        => TimeSpan.FromSeconds(kind == WeatherScreenKind.TitleCard ? Math.Clamp(c.TitleCardSeconds, 1, 10) : Math.Clamp(c.ScreenSeconds, 4, 60));

    /// <summary>
    /// The screens of one full pass for a station with <paramref name="locationCount"/> places. Each place opens
    /// with its title card, then its alerts when it has any, then current conditions; the radar and forecast radar
    /// follow for every place that has a loop (<paramref name="available"/>), so a place outside any coverage
    /// simply skips them; then the hourly and daily screens. The travel screen closes the pass.
    /// </summary>
    public static IReadOnlyList<CycleScreen> Pass(WeatherConfig c, int locationCount, CycleAvailability? available = null)
    {
        var a = available ?? CycleAvailability.All;
        var list = new List<CycleScreen>();
        for (var i = 0; i < locationCount; i++)
        {
            if (c.ShowTitleCards)
            {
                list.Add(new CycleScreen(i, WeatherScreenKind.TitleCard));
            }

            if (c.ShowAlerts && a.Alerts(i))
            {
                list.Add(new CycleScreen(i, WeatherScreenKind.Alerts));
            }

            list.Add(new CycleScreen(i, WeatherScreenKind.Current));
            if (c.ShowRadar && a.Radar(i))
            {
                list.Add(new CycleScreen(i, WeatherScreenKind.Radar));
            }

            if (c.ShowForecastRadar && a.ForecastRadar(i))
            {
                list.Add(new CycleScreen(i, WeatherScreenKind.ForecastRadar));
            }

            if (c.ShowHourly)
            {
                list.Add(new CycleScreen(i, WeatherScreenKind.Hourly));
            }

            if (c.ShowDaily)
            {
                list.Add(new CycleScreen(i, WeatherScreenKind.Daily));
            }
        }

        if (c.ShowTravelCities && locationCount >= 2)
        {
            list.Add(new CycleScreen(-1, WeatherScreenKind.Travel));
        }

        return list;
    }

    /// <summary>The short pass that fills the rest of an "on the 8s" window: each place's alerts (when it has any) and current conditions, then the travel screen.</summary>
    public static IReadOnlyList<CycleScreen> ShortPass(WeatherConfig c, int locationCount, CycleAvailability? available = null)
    {
        var a = available ?? CycleAvailability.All;
        var list = new List<CycleScreen>();
        for (var i = 0; i < locationCount; i++)
        {
            if (c.ShowAlerts && a.Alerts(i))
            {
                list.Add(new CycleScreen(i, WeatherScreenKind.Alerts));
            }

            list.Add(new CycleScreen(i, WeatherScreenKind.Current));
        }

        if (c.ShowTravelCities && locationCount >= 2)
        {
            list.Add(new CycleScreen(-1, WeatherScreenKind.Travel));
        }

        return list;
    }

    /// <summary>The on-air length of a list of screens.</summary>
    public static TimeSpan Length(WeatherConfig c, IReadOnlyList<CycleScreen> screens)
    {
        long ticks = 0;
        foreach (var s in screens)
        {
            ticks += ScreenLength(c, s.Kind).Ticks;
        }

        return TimeSpan.FromTicks(ticks);
    }

    public static TimeSpan PassLength(WeatherConfig c, int locationCount, CycleAvailability? available = null) => Length(c, Pass(c, locationCount, available));

    /// <summary>
    /// The screens on air from <paramref name="fromUtc"/> until <paramref name="toUtc"/>, in order, the first and
    /// last cut to the window. <paramref name="cycleStartUtc"/> is where passes are counted from when "on the 8s"
    /// is off (the guide entry's start).
    /// </summary>
    public static IReadOnlyList<TimedScreen> Timeline(WeatherConfig c, int locationCount, DateTime fromUtc, DateTime toUtc, DateTime cycleStartUtc, TimeZoneInfo zone, CycleAvailability? available = null)
    {
        var a = available ?? CycleAvailability.All;
        var result = new List<TimedScreen>();
        if (locationCount <= 0 || toUtc <= fromUtc)
        {
            return result;
        }

        var cursor = fromUtc;
        var guard = 0;
        while (cursor < toUtc && guard++ < 10000)
        {
            var (frameStart, frameEnd, screens) = c.OnTheEights
                ? EightsFrame(c, locationCount, cursor, zone, a)
                : PassFrame(c, locationCount, cursor, cycleStartUtc, a);

            // Walk the frame's screens from the cursor.
            var t = frameStart;
            for (var i = 0; i < screens.Count && t < frameEnd; i++)
            {
                var end = t + ScreenLength(c, screens[i].Kind);
                if (end > frameEnd)
                {
                    end = frameEnd;
                }

                if (end > cursor)
                {
                    var start = t > cursor ? t : cursor;
                    var clipEnd = end < toUtc ? end : toUtc;
                    if (clipEnd > start)
                    {
                        result.Add(new TimedScreen(screens[i], start, clipEnd, StartsPass: i == 0 && t >= cursor));
                    }

                    cursor = end;
                    if (cursor >= toUtc)
                    {
                        break;
                    }
                }

                t = end;
            }

            if (cursor < frameEnd)
            {
                // The frame's screens ran out before its end (short pass loops fill this in EightsFrame; here it is a guard).
                cursor = frameEnd;
            }
        }

        return result;
    }

    /// <summary>Without "on the 8s": the pass containing <paramref name="utc"/>, counted from the cycle start.</summary>
    private static (DateTime Start, DateTime End, IReadOnlyList<CycleScreen> Screens) PassFrame(WeatherConfig c, int locationCount, DateTime utc, DateTime cycleStartUtc, CycleAvailability a)
    {
        var pass = Pass(c, locationCount, a);
        var length = Length(c, pass);
        var elapsed = utc.Ticks - cycleStartUtc.Ticks;
        var k = Padding.FloorDiv(elapsed, length.Ticks);
        var start = new DateTime(cycleStartUtc.Ticks + (k * length.Ticks), DateTimeKind.Utc);
        return (start, start + length, pass);
    }

    /// <summary>
    /// With "on the 8s": the ten-minute window containing <paramref name="utc"/> (station local minutes 08-18, 18-28,
    /// ...), filled with one full pass and then the short pass repeated until the window ends.
    /// </summary>
    private static (DateTime Start, DateTime End, IReadOnlyList<CycleScreen> Screens) EightsFrame(WeatherConfig c, int locationCount, DateTime utc, TimeZoneInfo zone, CycleAvailability a)
    {
        var windowStart = WindowStart(utc, zone);
        var windowEnd = windowStart + EightsWindow;
        var screens = new List<CycleScreen>(Pass(c, locationCount, a));
        var shortPass = ShortPass(c, locationCount, a);
        var filled = Length(c, screens);
        var shortLength = Length(c, shortPass);
        while (filled < EightsWindow && shortPass.Count > 0 && shortLength > TimeSpan.Zero)
        {
            screens.AddRange(shortPass);
            filled += shortLength;
        }

        return (windowStart, windowEnd, screens);
    }

    /// <summary>The start (UTC) of the ten-minute "on the 8s" window containing <paramref name="utc"/>: station-local minute 8, 18, 28, 38, 48 or 58.</summary>
    public static DateTime WindowStart(DateTime utc, TimeZoneInfo zone)
    {
        var offset = zone.GetUtcOffset(utc);
        var local = utc + offset;
        var minutesIntoHour = local.TimeOfDay.Ticks % TimeSpan.TicksPerHour;
        var shifted = minutesIntoHour - EightsOffset.Ticks;
        var k = Padding.FloorDiv(shifted, EightsWindow.Ticks);
        var windowLocalStart = local - TimeSpan.FromTicks(minutesIntoHour) + EightsOffset + TimeSpan.FromTicks(k * EightsWindow.Ticks);
        return DateTime.SpecifyKind(windowLocalStart - offset, DateTimeKind.Utc);
    }
}
