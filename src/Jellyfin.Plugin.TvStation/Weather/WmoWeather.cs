namespace Jellyfin.Plugin.TvStation.Weather;

/// <summary>The icon a weather code is drawn with.</summary>
public enum WeatherIcon
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    HeavyRain,
    FreezingRain,
    Snow,
    HeavySnow,
    Sleet,
    Showers,
    SnowShowers,
    Thunderstorm,
    Unknown
}

/// <summary>What a chance of precipitation is a chance of.</summary>
public enum PrecipitationKind
{
    Rain,
    Snow,
    /// <summary>Rain and snow both forecast.</summary>
    Mix,
    /// <summary>Freezing rain or freezing drizzle.</summary>
    Ice
}

/// <summary>WMO 4677 weather interpretation codes, as Open-Meteo reports them, to words and icons.</summary>
public static class WmoWeather
{
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 => "Fog",
        48 => "Freezing fog",
        51 => "Light drizzle",
        53 => "Drizzle",
        55 => "Heavy drizzle",
        56 => "Freezing drizzle",
        57 => "Heavy freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 => "Freezing rain",
        67 => "Heavy freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 => "Light showers",
        81 => "Showers",
        82 => "Heavy showers",
        85 => "Snow showers",
        86 => "Heavy snow showers",
        95 => "Thunderstorm",
        96 => "Thunderstorm with hail",
        99 => "Severe thunderstorm with hail",
        _ => "Unknown"
    };

    /// <summary>Short form for narrow columns ("Showers" rather than "Heavy showers").</summary>
    public static string Short(int code) => code switch
    {
        0 or 1 => "Clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 or 66 or 67 => "Freezing rain",
        61 or 63 => "Rain",
        65 => "Heavy rain",
        71 or 73 or 77 => "Snow",
        75 => "Heavy snow",
        80 or 81 or 82 => "Showers",
        85 or 86 => "Snow showers",
        95 or 96 or 99 => "Storms",
        _ => "—"
    };

    public static WeatherIcon Icon(int code) => code switch
    {
        0 or 1 => WeatherIcon.Clear,
        2 => WeatherIcon.PartlyCloudy,
        3 => WeatherIcon.Cloudy,
        45 or 48 => WeatherIcon.Fog,
        51 or 53 or 55 => WeatherIcon.Drizzle,
        56 or 57 or 66 or 67 => WeatherIcon.FreezingRain,
        61 or 63 => WeatherIcon.Rain,
        65 => WeatherIcon.HeavyRain,
        71 or 73 or 77 => WeatherIcon.Snow,
        75 => WeatherIcon.HeavySnow,
        80 or 81 or 82 => WeatherIcon.Showers,
        85 or 86 => WeatherIcon.SnowShowers,
        95 or 96 or 99 => WeatherIcon.Thunderstorm,
        _ => WeatherIcon.Unknown
    };

    /// <summary>
    /// Names the precipitation a chance refers to. Open-Meteo's probability covers rain, showers and snow together,
    /// so the type comes from the forecast amounts when there are any (both kinds means a mix), else from the
    /// weather code, else from the temperature (at or below freezing it would fall as snow).
    /// </summary>
    public static PrecipitationKind PrecipitationType(int code, double rainMm, double snowfallCm, double temperatureC)
    {
        var rain = rainMm >= 0.1;
        var snow = snowfallCm >= 0.1;
        if (rain && snow)
        {
            return PrecipitationKind.Mix;
        }

        if (snow)
        {
            return PrecipitationKind.Snow;
        }

        if (rain)
        {
            return code is 56 or 57 or 66 or 67 ? PrecipitationKind.Ice : PrecipitationKind.Rain;
        }

        switch (code)
        {
            case 56 or 57 or 66 or 67:
                return PrecipitationKind.Ice;
            case 71 or 73 or 75 or 77 or 85 or 86:
                return PrecipitationKind.Snow;
            case 51 or 53 or 55 or 61 or 63 or 65 or 80 or 81 or 82 or 95 or 96 or 99:
                return PrecipitationKind.Rain;
            default:
                return temperatureC <= 0 ? PrecipitationKind.Snow : PrecipitationKind.Rain;
        }
    }

    /// <summary>"rain", "snow", "mix" or "ice", to follow a percentage.</summary>
    public static string PrecipitationWord(PrecipitationKind kind) => kind switch
    {
        PrecipitationKind.Snow => "snow",
        PrecipitationKind.Mix => "mix",
        PrecipitationKind.Ice => "ice",
        _ => "rain"
    };

    /// <summary>Codes that mean precipitation is falling right now.</summary>
    public static bool IsPrecipitation(int code) => code >= 51 && code != 77 || code == 77;

    /// <summary>The 16-point compass name for a wind direction.</summary>
    public static string Compass(int degrees)
    {
        var names = new[] { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
        var d = ((degrees % 360) + 360) % 360;
        return names[(int)Math.Round(d / 22.5) % 16];
    }
}
