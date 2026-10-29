using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// Station templates: what a station keeps when its content is taken away, the built-in presets, and how a template
/// becomes a new draft. Pure; the controller and the import path call it.
/// </summary>
public static class StationTemplates
{
    /// <summary>A template from a station: a deep copy with every id, source, item, place and music track removed.</summary>
    public static StationTemplate FromStation(Station station, string name)
    {
        var settings = Clone(station);
        StripContent(settings);
        return new StationTemplate
        {
            Id = Guid.NewGuid(),
            Name = (name ?? string.Empty).Trim(),
            Settings = settings,
            CreatedUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Removes everything that is content or identity: name, number, call sign, logo, anchor, time zone, sources,
    /// lineup items, excluded items, weather places and music. Time blocks stay (their days and times are the
    /// schedule's shape) but lose their sources; bumper clips stay, they are part of the look.
    /// </summary>
    public static void StripContent(Station s)
    {
        s.Id = Guid.Empty;
        s.Name = string.Empty;
        s.ChannelNumber = string.Empty;
        s.CallSign = null;
        s.Enabled = true;
        s.LogoFileName = null;
        s.TimeZoneId = string.Empty;
        s.AnchorUtc = default;
        s.Revision = 1;
        s.CreatedUtc = default;
        s.UpdatedUtc = default;
        s.Pool ??= new PoolConfig();
        s.Pool.Sources = new List<ContentSource>();
        s.Slots ??= new SlotsConfig();
        s.Slots.Filler ??= new FillerConfig();
        s.Slots.Filler.Sources = new List<ContentSource>();
        s.Slots.Blocks ??= new List<SlotBlock>();
        foreach (var block in s.Slots.Blocks)
        {
            block.Id = Guid.Empty;
            block.Sources = new List<ContentSource>();
        }

        s.Lineup = new LineupConfig();
        s.ExcludedItemIds = new List<Guid>();
        s.Weather ??= new WeatherConfig();
        s.Weather.Locations = new List<WeatherLocation>();
        s.Weather.MusicSources = new List<ContentSource>();
        s.Weather.MusicFolder = null;
    }

    /// <summary>
    /// A new draft from a template: the template's settings under the draft's own name, number, call sign and time
    /// zone. A blank name takes the template's default name; a blank time zone in the template keeps the draft's.
    /// </summary>
    public static Station ApplyTo(StationTemplate template, Station draft)
    {
        var station = Clone(template.Settings ?? new Station());
        StripContent(station);
        station.Id = draft.Id;
        station.Name = string.IsNullOrWhiteSpace(draft.Name) ? (template.DefaultName ?? string.Empty) : draft.Name;
        station.ChannelNumber = draft.ChannelNumber ?? string.Empty;
        station.CallSign = draft.CallSign;
        station.Enabled = draft.Enabled;
        var zone = template.Settings?.TimeZoneId;
        station.TimeZoneId = string.IsNullOrWhiteSpace(zone) ? (string.IsNullOrWhiteSpace(draft.TimeZoneId) ? "UTC" : draft.TimeZoneId) : zone!;
        foreach (var block in station.Slots.Blocks)
        {
            block.Id = Guid.NewGuid();
        }

        return station;
    }

    /// <summary>Saved templates first (by name), then the built-in ones whose names no saved template has taken.</summary>
    public static List<StationTemplate> Merge(IEnumerable<StationTemplate>? saved)
    {
        var list = (saved ?? Enumerable.Empty<StationTemplate>())
            .Where(t => t is not null)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var t in list)
        {
            t.BuiltIn = false;
        }

        var taken = new HashSet<string>(list.Select(t => t.Name.Trim()), StringComparer.OrdinalIgnoreCase);
        list.AddRange(BuiltIn.Where(b => !taken.Contains(b.Name)));
        return list;
    }

    /// <summary>The template with this id among the saved and built-in ones, or null.</summary>
    public static StationTemplate? Find(IEnumerable<StationTemplate>? saved, Guid id) => Merge(saved).FirstOrDefault(t => t.Id == id);

    public static Station Clone(Station station) => JsonFileStore.Clone(station);

    public static StationTemplate Clone(StationTemplate template) => JsonFileStore.Clone(template);

    /// <summary>The nine presets the New station page started with, now data. Ids are fixed so links and exports hold.</summary>
    public static IReadOnlyList<StationTemplate> BuiltIn { get; } = BuildPresets();

    private static List<StationTemplate> BuildPresets()
    {
        static StationTemplate Preset(string id, string icon, string name, string description, string next, Action<Station> apply, SourceDefaults? sourceDefaults = null, string? defaultName = null)
        {
            var settings = new Station();
            StripContent(settings);
            apply(settings);
            return new StationTemplate
            {
                Id = Guid.Parse(id),
                Icon = icon,
                Name = name,
                Description = description,
                NextStep = next,
                BuiltIn = true,
                SourceDefaults = sourceDefaults,
                DefaultName = defaultName,
                Settings = settings
            };
        }

        static void ByLength(Station s)
        {
            s.PaddingByLength = true;
            s.PaddingGridMinutes = 0;
        }

        return new List<StationTemplate>
        {
            Preset("7c1b3a10-0001-4b5e-9a6d-6e1f0c2d3e01", "shuffle", "Shuffle a collection",
                "Pick any shows and movies; they play around the clock, reshuffled each round, never the same show or film twice in a row.",
                "Next: add shows and movies.", _ => { }),
            Preset("7c1b3a10-0002-4b5e-9a6d-6e1f0c2d3e02", "child_care", "Kids channel",
                "Cartoons and family shows all day, rated G or below. Each show plays two or three episodes in order before switching.",
                "Next: add the shows. Whatever you add is limited to G unless you change it.",
                s =>
                {
                    s.Pool.Order = PlayOrder.ShuffleEpisodesInOrder;
                    s.Pool.MarathonMin = 2;
                    s.Pool.MarathonMax = 3;
                    ByLength(s);
                },
                new SourceDefaults { MaxRating = "G", AllowUnrated = false }),
            Preset("7c1b3a10-0003-4b5e-9a6d-6e1f0c2d3e03", "movie", "Movie night",
                "Films in two-hour slots, shuffled, and a film rests at least eight hours before it plays again. Nothing above PG-13 before 8 pm.",
                "Next: add a genre, a collection or single films.",
                s =>
                {
                    ByLength(s);
                    s.Pool.CooldownHours = 8;
                    s.Pool.DaypartRules = new List<DaypartRule> { new() { StartLocal = "06:00", EndLocal = "20:00", MaxRating = "PG-13", AllowUnrated = true } };
                },
                new SourceDefaults { ItemKinds = new List<SourceKind> { SourceKind.Movie } }),
            Preset("7c1b3a10-0004-4b5e-9a6d-6e1f0c2d3e04", "tv", "Comfort reruns",
                "A few favourite shows in half-hour and hour slots. A random show comes up, always on its next episode.",
                "Next: add the shows.",
                s =>
                {
                    s.Pool.Order = PlayOrder.ShuffleEpisodesInOrder;
                    ByLength(s);
                },
                new SourceDefaults { ItemKinds = new List<SourceKind> { SourceKind.Episode } }),
            Preset("7c1b3a10-0005-4b5e-9a6d-6e1f0c2d3e05", "ac_unit", "Holiday channel",
                "Seasonal films and specials. The channel only appears in the guide from late November to early January.",
                "Next: add the holiday titles. The excluded tags from Settings are off here so holiday-tagged items stay in.",
                s =>
                {
                    ByLength(s);
                    s.ActiveWindows = new List<SeasonWindow> { new() { Label = "Holiday season", StartMonth = 11, StartDay = 20, EndMonth = 1, EndDay = 6 } };
                    s.UseGlobalExcludedTags = false;
                }),
            Preset("7c1b3a10-0006-4b5e-9a6d-6e1f0c2d3e06", "playlist_play", "One show, start to finish",
                "One series from the first episode in order, around the clock, then again from the start.",
                "Next: add the show.",
                s =>
                {
                    s.Pool.Order = PlayOrder.Sequential;
                    s.Pool.AvoidSameSeriesBackToBack = false;
                    ByLength(s);
                }),
            Preset("7c1b3a10-0007-4b5e-9a6d-6e1f0c2d3e07", "event", "Weekly schedule",
                "Blocks at fixed times each week, say cartoons on weekday mornings and a film on Friday night, with filler in between.",
                "Next: add blocks and the filler.",
                s =>
                {
                    s.Mode = ScheduleMode.Slots;
                    s.PaddingGridMinutes = 15;
                }),
            Preset("7c1b3a10-0008-4b5e-9a6d-6e1f0c2d3e08", "format_list_numbered", "Play a fixed list",
                "Exactly the items you pick, in your order, looping forever.",
                "Next: add items in order.",
                s => s.Mode = ScheduleMode.Lineup),
            Preset("7c1b3a10-0009-4b5e-9a6d-6e1f0c2d3e09", "wb_sunny", "Weather channel",
                "Local forecasts for a few places, like the cable weather channel: current conditions, the hours ahead and the week, over music, with a live clock.",
                "Next: add places and some music.",
                s => s.Mode = ScheduleMode.Weather,
                defaultName: "Weather")
        };
    }
}
