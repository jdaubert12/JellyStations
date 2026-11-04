using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Storage;

/// <summary>
/// Brings a station written by an older version up to the current <see cref="Station.FormatVersion"/>. Runs on
/// every station file at load, on imported stations and templates, and on templates saved in settings.json, so
/// a file is converted once and then saved in the new shape.
///
/// Format 2 (1.26.0.0): the logo bug and the "Up next" card got plugin-wide defaults, and a station's own
/// settings became optional (null = the default). Before, every station carried both, off unless the person
/// had turned them on. A station whose bug or card was off had not chosen anything, so it now follows the
/// default; one that had turned it on keeps its own settings, exactly as they were.
/// </summary>
public static class StationUpgrades
{
    /// <summary>Upgrades the station in place. True when something changed.</summary>
    public static bool Apply(Station station)
    {
        if (station.FormatVersion >= Station.CurrentFormatVersion)
        {
            return false;
        }

        if (station.FormatVersion < 2)
        {
            if (station.LogoOverlay is { Enabled: false })
            {
                station.LogoOverlay = null;
            }

            if (station.UpNextCard is { Enabled: false })
            {
                station.UpNextCard = null;
            }
        }

        station.FormatVersion = Station.CurrentFormatVersion;
        return true;
    }

    /// <summary>Upgrades the station a template carries.</summary>
    public static bool Apply(StationTemplate template) => template.Settings is not null && Apply(template.Settings);

    /// <summary>Upgrades the templates saved in settings and fills in defaults a settings file from before them lacks.</summary>
    public static bool Apply(GlobalSettings settings)
    {
        var changed = false;
        if (settings.LogoOverlay is null)
        {
            settings.LogoOverlay = new LogoOverlayConfig();
            changed = true;
        }

        if (settings.UpNextCard is null)
        {
            settings.UpNextCard = new UpNextCardConfig();
            changed = true;
        }

        foreach (var template in settings.StationTemplates ?? new List<StationTemplate>())
        {
            changed |= Apply(template);
        }

        return changed;
    }
}
