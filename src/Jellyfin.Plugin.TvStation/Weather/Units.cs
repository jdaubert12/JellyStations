using System.Globalization;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Weather;

/// <summary>Unit conversion and formatting for the screens. Reports are metric; this is the only place that converts.</summary>
public static class Units
{
    public static double CToF(double c) => c * 9 / 5 + 32;

    public static double KmhToMph(double kmh) => kmh / 1.609344;

    public static double MmToInches(double mm) => mm / 25.4;

    public static double HpaToInHg(double hpa) => hpa * 0.02952998;

    /// <summary>"72°" (no unit letter: the screens say which system they use once).</summary>
    public static string Temperature(double c, WeatherUnits units)
        => Math.Round(units == WeatherUnits.Imperial ? CToF(c) : c).ToString("0", CultureInfo.InvariantCulture) + "°";

    /// <summary>"72°F" / "22°C".</summary>
    public static string TemperatureWithUnit(double c, WeatherUnits units)
        => Temperature(c, units) + (units == WeatherUnits.Imperial ? "F" : "C");

    /// <summary>"12 mph" / "19 km/h".</summary>
    public static string Speed(double kmh, WeatherUnits units)
        => units == WeatherUnits.Imperial
            ? Math.Round(KmhToMph(kmh)).ToString("0", CultureInfo.InvariantCulture) + " mph"
            : Math.Round(kmh).ToString("0", CultureInfo.InvariantCulture) + " km/h";

    /// <summary>"0.25 in" / "6 mm".</summary>
    public static string Precipitation(double mm, WeatherUnits units)
        => units == WeatherUnits.Imperial
            ? MmToInches(mm).ToString("0.00", CultureInfo.InvariantCulture) + " in"
            : Math.Round(mm, mm < 1 ? 1 : 0).ToString(mm < 1 ? "0.0" : "0", CultureInfo.InvariantCulture) + " mm";

    /// <summary>"30.12 in" / "1020 hPa".</summary>
    public static string Pressure(double hpa, WeatherUnits units)
        => units == WeatherUnits.Imperial
            ? HpaToInHg(hpa).ToString("0.00", CultureInfo.InvariantCulture) + " in"
            : Math.Round(hpa).ToString("0", CultureInfo.InvariantCulture) + " hPa";

    /// <summary>"3:47 PM" or "15:47".</summary>
    public static string Clock(DateTime local, bool twentyFourHour)
        => local.ToString(twentyFourHour ? "H:mm" : "h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>"3 PM" or "15:00", for the hourly strip.</summary>
    public static string Hour(DateTime local, bool twentyFourHour)
        => twentyFourHour ? local.ToString("H:00", CultureInfo.InvariantCulture) : local.ToString("h tt", CultureInfo.InvariantCulture);

    /// <summary>"Today", "Tomorrow" or the weekday name.</summary>
    public static string DayName(DateTime date, DateTime today)
    {
        if (date.Date == today.Date)
        {
            return "Today";
        }

        if (date.Date == today.Date.AddDays(1))
        {
            return "Tomorrow";
        }

        return date.ToString("dddd", CultureInfo.InvariantCulture);
    }

    /// <summary>"Thu", "Fri"... for narrow columns.</summary>
    public static string ShortDayName(DateTime date, DateTime today)
        => date.Date == today.Date ? "Today" : date.ToString("ddd", CultureInfo.InvariantCulture);

    /// <summary>The strftime format drawtext uses for the live clock.</summary>
    public static string DrawtextClockFormat(bool twentyFourHour) => twentyFourHour ? "%H\\\\\\:%M" : "%-I\\\\\\:%M %p";
}
