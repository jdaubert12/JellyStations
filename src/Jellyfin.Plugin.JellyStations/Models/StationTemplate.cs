namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>
/// A station without its content: mode, order, padding, family hours, time blocks (emptied), bumpers, weather look,
/// logo style, encoding, up-next card. The "New station" page offers the built-in ones (the presets) and the ones
/// saved from a station editor; a saved template with the same name as a built-in one takes its place.
/// </summary>
public sealed class StationTemplate
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>One or two sentences on the New station card.</summary>
    public string? Description { get; set; }

    /// <summary>Material icon name for the card. Null = a bookmark.</summary>
    public string? Icon { get; set; }

    /// <summary>What to do after picking the template ("Next: add the shows."), shown on the card.</summary>
    public string? NextStep { get; set; }

    /// <summary>Ships with the plugin; not stored in settings and cannot be deleted.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>Name a station made from this template gets when the person leaves it blank.</summary>
    public string? DefaultName { get; set; }

    /// <summary>Hex color ("#2a5d9f") for the generated logo. Null = from the station name.</summary>
    public string? LogoColor { get; set; }

    /// <summary>Filters put on every source the person adds to a station made from this template.</summary>
    public SourceDefaults? SourceDefaults { get; set; }

    /// <summary>The settings, as a station with all content removed (see <see cref="Library.StationTemplates.FromStation"/>).</summary>
    public Station Settings { get; set; } = new();

    public DateTime CreatedUtc { get; set; }
}

/// <summary>Filters a template wants on every source added to its stations (a rating cap, movies or episodes only).</summary>
public sealed class SourceDefaults
{
    public string? MaxRating { get; set; }

    public bool AllowUnrated { get; set; }

    public List<SourceKind> ItemKinds { get; set; } = new();
}
