using Jellyfin.Plugin.JellyStations.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// The plugin-wide "Hide shows and movies added after" date (<see cref="GlobalSettings.HideAddedAfter"/>). A movie goes
/// by its own date added; an episode by its show's, so new episodes of a show that was already there keep playing.
/// Only the sources that sweep content in are filtered (<see cref="Filters"/>).
/// </summary>
public sealed class AddedCutoff
{
    private readonly DateTime _limitUtc;

    private AddedCutoff(DateTime limitUtc)
    {
        _limitUtc = limitUtc;
    }

    /// <summary>The cutoff for a date, or null when none is set. The date itself still counts as in time, in the server's time zone.</summary>
    public static AddedCutoff? For(DateOnly? date, TimeZoneInfo? zone = null)
    {
        if (date is not { } day || day == DateOnly.MaxValue)
        {
            return null;
        }

        zone ??= TimeZoneInfo.Local;
        var midnight = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(midnight))
        {
            // A clock change at midnight: the day starts at the first time that exists.
            midnight = midnight.AddMinutes(15);
        }

        return new AddedCutoff(TimeZoneInfo.ConvertTimeToUtc(midnight, zone));
    }

    /// <summary>True for the sources that sweep content in; a show, season, movie or episode picked by name always plays.</summary>
    public static bool Filters(ContentSource source)
        => source.Kind is SourceKind.Genre or SourceKind.Tag or SourceKind.Library or SourceKind.BoxSet or SourceKind.Playlist;

    /// <summary>True for a movie, or an episode of a show, added after the date. Other videos (clips) are never hidden.</summary>
    public bool Hides(BaseItem item, Func<Episode, BaseItem?> seriesOf) => item switch
    {
        Episode e => IsAfter((seriesOf(e) ?? e).DateCreated),
        Movie m => IsAfter(m.DateCreated),
        _ => false
    };

    private bool IsAfter(DateTime added)
        => (added.Kind == DateTimeKind.Local ? added.ToUniversalTime() : added) >= _limitUtc;
}
