using System.Globalization;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Streaming;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Services;

/// <summary>Coordinates station changes: validation, revision bumps, compilation, cache invalidation, guide refresh.</summary>
public sealed class StationService
{
    private readonly StationRepository _stations;
    private readonly CompiledStationRepository _compiled;
    private readonly SettingsRepository _settings;
    private readonly StationCompiler _compiler;
    private readonly IScheduleResolver _resolver;
    private readonly GuideRefreshService _guide;
    private readonly StreamSessionManager _sessions;
    private readonly JellyStationsPaths _paths;
    private readonly ILogger<StationService> _logger;
    private readonly object _writeLock = new();

    public StationService(
        StationRepository stations,
        CompiledStationRepository compiled,
        SettingsRepository settings,
        StationCompiler compiler,
        IScheduleResolver resolver,
        GuideRefreshService guide,
        StreamSessionManager sessions,
        JellyStationsPaths paths,
        ILogger<StationService> logger)
    {
        _stations = stations;
        _compiled = compiled;
        _settings = settings;
        _compiler = compiler;
        _resolver = resolver;
        _guide = guide;
        _sessions = sessions;
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyList<Station> GetAll() => _stations.GetAll();

    public Station? Get(Guid id) => _stations.Get(id);

    public CompiledStation? GetCompiled(Guid id) => _compiled.Get(id);

    public EncodingProfile EffectiveProfile(Station station) => station.Encoding ?? _settings.Get().DefaultEncoding;

    public Station Create(Station draft)
    {
        lock (_writeLock)
        {
            draft.Id = draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id;
            if (_stations.Get(draft.Id) is not null)
            {
                throw new ValidationException(new[] { "A station with this id already exists." });
            }

            Normalize(draft);
            var errors = Validate(draft, null);
            if (errors.Count > 0)
            {
                throw new ValidationException(errors);
            }

            draft.Revision = 1;
            draft.CreatedUtc = DateTime.UtcNow;
            if (draft.AnchorUtc == default)
            {
                draft.AnchorUtc = DefaultAnchor(draft);
            }

            var compiled = _compiler.Compile(draft, EffectiveProfile(draft), _settings.Get().ExcludedTags);
            _compiled.Save(compiled);
            _stations.Save(draft);
            _resolver.Invalidate(draft.Id);
            _guide.Trigger();
            return draft;
        }
    }

    public Station Update(Guid id, Station draft)
    {
        lock (_writeLock)
        {
            var existing = _stations.Get(id) ?? throw new KeyNotFoundException("Station not found.");
            draft.Id = id;
            Normalize(draft);
            var errors = Validate(draft, existing);
            if (errors.Count > 0)
            {
                throw new ValidationException(errors);
            }

            draft.CreatedUtc = existing.CreatedUtc;
            draft.LogoFileName = existing.LogoFileName;
            if (draft.AnchorUtc == default)
            {
                draft.AnchorUtc = existing.AnchorUtc;
            }

            var scheduleChanged = ScheduleFingerprint(existing) != ScheduleFingerprint(draft);
            draft.Revision = scheduleChanged ? existing.Revision + 1 : existing.Revision;

            var compiled = _compiler.Compile(draft, EffectiveProfile(draft), _settings.Get().ExcludedTags);
            var previous = _compiled.Get(id);
            if (!scheduleChanged && previous is not null && !StationCompiler.SameContent(previous, compiled))
            {
                draft.Revision = existing.Revision + 1;
                compiled.Revision = draft.Revision;
                scheduleChanged = true;
            }

            compiled.Revision = draft.Revision;
            _compiled.Save(compiled);
            _stations.Save(draft);
            _resolver.Invalidate(id);

            var encodingChanged = JsonFileStore.Serialize(existing.Encoding) != JsonFileStore.Serialize(draft.Encoding)
                || JsonFileStore.Serialize(existing.LogoOverlay) != JsonFileStore.Serialize(draft.LogoOverlay)
                || JsonFileStore.Serialize(existing.UpNextCard) != JsonFileStore.Serialize(draft.UpNextCard);
            var windowsChanged = JsonFileStore.Serialize(existing.ActiveWindows) != JsonFileStore.Serialize(draft.ActiveWindows);
            if (encodingChanged || !draft.Enabled || (windowsChanged && !Scheduling.SeasonWindows.IsActive(draft, DateTime.UtcNow)))
            {
                _sessions.StopStation(id);
            }

            if (scheduleChanged || windowsChanged || existing.Enabled != draft.Enabled || existing.Name != draft.Name || existing.ChannelNumber != draft.ChannelNumber || existing.CallSign != draft.CallSign)
            {
                _guide.Trigger();
            }

            return draft;
        }
    }

    public bool Delete(Guid id)
    {
        lock (_writeLock)
        {
            var station = _stations.Get(id);
            _sessions.StopStation(id);
            var removed = _stations.Delete(id);
            _compiled.Delete(id);
            _resolver.Invalidate(id);
            if (station?.LogoFileName is { Length: > 0 } logo)
            {
                try
                {
                    File.Delete(_paths.LogoFile(logo));
                }
                catch (Exception)
                {
                    // Ignore.
                }
            }

            if (removed)
            {
                _guide.Trigger();
            }

            return removed;
        }
    }

    /// <summary>Re-expands content from the library. Bumps the revision only if the item set changed.</summary>
    public CompileResultDto Recompile(Guid id, bool triggerGuide = true)
    {
        lock (_writeLock)
        {
            var station = _stations.Get(id) ?? throw new KeyNotFoundException("Station not found.");
            var previous = _compiled.Get(id);
            var compiled = _compiler.Compile(station, EffectiveProfile(station), _settings.Get().ExcludedTags);
            var changed = previous is null || !StationCompiler.SameContent(previous, compiled);
            if (changed && previous is not null)
            {
                station.Revision++;
                compiled.Revision = station.Revision;
                _stations.Save(station);
            }

            _compiled.Save(compiled);
            _resolver.Invalidate(id);
            if (changed && triggerGuide)
            {
                _guide.Trigger();
            }

            return ToCompileResult(compiled, changed);
        }
    }

    /// <summary>
    /// Re-expands every station against the library, then refreshes the guide once if any station's content
    /// changed. Used by the dashboard's "Recompile all" and after a settings change that affects content
    /// (e.g. excluded tags). A station that fails to compile is reported and does not stop the others.
    /// </summary>
    public RecompileAllResultDto RecompileAll()
    {
        var result = new RecompileAllResultDto();
        var all = _stations.GetAll()
            .OrderBy(s => double.TryParse(s.ChannelNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.MaxValue)
            .ThenBy(s => s.ChannelNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var station in all)
        {
            var row = new StationCompileResultDto { Id = station.Id, Name = station.Name, ChannelNumber = station.ChannelNumber };
            try
            {
                var compiled = Recompile(station.Id, triggerGuide: false);
                row.ItemCount = compiled.ItemCount;
                row.ExcludedCount = compiled.Excluded.Count;
                row.ContentChanged = compiled.ContentChanged;
                if (compiled.ContentChanged)
                {
                    result.ChangedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyStations: failed to recompile station {Name}", station.Name);
                row.Error = ex.Message;
                result.FailedCount++;
            }

            result.Stations.Add(row);
        }

        if (result.ChangedCount > 0)
        {
            _guide.Trigger();
        }

        return result;
    }

    /// <summary>
    /// Compiles any station whose snapshot is missing (e.g. first start after a data restore) or was written by a
    /// plugin version that did not record everything the restreamer now needs (see <see cref="CompiledStation.CurrentFormat"/>).
    /// </summary>
    public void EnsureCompiled()
    {
        foreach (var station in _stations.GetAll())
        {
            var compiled = _compiled.Get(station.Id);
            if (compiled is null || compiled.Format != CompiledStation.CurrentFormat)
            {
                if (compiled is not null)
                {
                    _logger.LogInformation("JellyStations: recompiling {Name} (snapshot format {Old} -> {New})", station.Name, compiled.Format, CompiledStation.CurrentFormat);
                }

                try
                {
                    Recompile(station.Id, triggerGuide: false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "JellyStations: failed to compile station {Name}", station.Name);
                }
            }
        }
    }

    public ScheduleInput? GetScheduleInput(Guid id)
    {
        var station = _stations.Get(id);
        if (station is null)
        {
            return null;
        }

        var compiled = _compiled.Get(id) ?? new CompiledStation { StationId = id, Revision = station.Revision };
        return ScheduleInput.From(station, compiled);
    }

    public IReadOnlyList<ScheduleEntry> Preview(Station draft, DateTime fromUtc, DateTime toUtc)
    {
        var compiled = CompileDraft(draft);
        return ScheduleResolver.EnumerateInput(ScheduleInput.From(draft, compiled), fromUtc, toUtc);
    }

    /// <summary>Normalizes an unsaved draft and expands its content in memory without persisting anything.</summary>
    public CompiledStation CompileDraft(Station draft)
    {
        Normalize(draft);
        if (draft.Id == Guid.Empty)
        {
            draft.Id = Guid.NewGuid();
        }

        if (draft.AnchorUtc == default)
        {
            draft.AnchorUtc = DefaultAnchor(draft);
        }

        if (draft.Revision <= 0)
        {
            draft.Revision = 1;
        }

        return _compiler.Compile(draft, EffectiveProfile(draft), _settings.Get().ExcludedTags);
    }

    /// <summary>Flattens a compiled snapshot into the content listing, excluded items last.</summary>
    public static ContentListDto ToContentList(Station station, CompiledStation compiled, int offset, int limit)
    {
        var blockNames = station.Slots.Blocks.ToDictionary(b => b.Id, b => string.IsNullOrWhiteSpace(b.Name) ? "Block" : b.Name);
        var rows = new List<ContentItemDto>();
        var seen = new HashSet<Guid>();
        double uniqueTicks = 0;

        void Add(IEnumerable<PlayableItem> items, string source, bool excluded, bool byRule = false)
        {
            foreach (var item in items)
            {
                if (!seen.Add(item.ItemId))
                {
                    continue;
                }

                if (!excluded)
                {
                    uniqueTicks += item.RuntimeTicks;
                }

                rows.Add(new ContentItemDto
                {
                    ItemId = item.ItemId,
                    Title = item.Title,
                    EpisodeTitle = item.EpisodeTitle,
                    SeasonNumber = item.SeasonNumber,
                    EpisodeNumber = item.EpisodeNumber,
                    Year = item.ProductionYear,
                    Rating = item.OfficialRating,
                    Minutes = (int)Math.Round(item.Runtime.TotalMinutes),
                    Genres = item.Genres,
                    Source = source,
                    IsSeries = item.IsSeries,
                    IsMovie = item.IsMovie,
                    Excluded = excluded,
                    ExcludedByRule = byRule
                });
            }
        }

        Add(compiled.Pool, "Pool", false);
        Add(compiled.Lineup, "Lineup", false);
        foreach (var kv in compiled.Blocks)
        {
            Add(kv.Value, blockNames.TryGetValue(kv.Key, out var n) ? n : "Block", false);
        }

        Add(compiled.Filler, "Filler", false);
        Add(compiled.Bumpers, "Bumpers", false);
        if (station.Bumpers?.PreRoll?.Sources is { Count: > 0 })
        {
            Add(compiled.PreRoll, "Pre-roll", false);
        }

        if (station.Bumpers?.PostRoll?.Sources is { Count: > 0 })
        {
            Add(compiled.PostRoll, "Post-roll", false);
        }
        foreach (var track in compiled.Music)
        {
            if (!seen.Add(track.Id))
            {
                continue;
            }

            uniqueTicks += track.RuntimeTicks;
            rows.Add(new ContentItemDto
            {
                ItemId = track.Id,
                Title = track.Title,
                EpisodeTitle = track.Artist,
                Minutes = (int)Math.Round(track.Runtime.TotalMinutes),
                Source = "Music: " + track.Source,
                IsMusic = true
            });
        }

        Add(compiled.UserExcluded, "Excluded", true);
        Add(compiled.TagExcluded, "Excluded by tag", true, byRule: true);

        var bySource = rows.Where(r => !r.Excluded).GroupBy(r => r.Source).ToDictionary(g => g.Key, g => g.Count());
        limit = Math.Clamp(limit, 1, 5000);
        offset = Math.Max(0, offset);
        return new ContentListDto
        {
            Total = rows.Count,
            Offset = offset,
            Items = rows.Skip(offset).Take(limit).ToList(),
            UniqueHours = Math.Round(uniqueTicks / TimeSpan.TicksPerHour, 1),
            CycleHours = station.Mode == ScheduleMode.Pool ? Math.Round(compiled.Pool.Sum(i => (double)i.RuntimeTicks) / TimeSpan.TicksPerHour, 1) : null,
            ExcludedCount = compiled.UserExcluded.Count + compiled.TagExcluded.Count,
            BySource = bySource
        };
    }

    public void SaveLogo(Guid id, string extension, Stream content)
    {
        lock (_writeLock)
        {
            var station = _stations.Get(id) ?? throw new KeyNotFoundException("Station not found.");
            _paths.EnsureDirectories();
            var fileName = id.ToString("N") + extension;
            var path = _paths.LogoFile(fileName);
            using (var fs = File.Create(path))
            {
                content.CopyTo(fs);
            }

            if (!string.IsNullOrEmpty(station.LogoFileName) && station.LogoFileName != fileName)
            {
                try
                {
                    File.Delete(_paths.LogoFile(station.LogoFileName));
                }
                catch (Exception)
                {
                    // Ignore.
                }
            }

            station.LogoFileName = fileName;
            _stations.Save(station);
            _guide.Trigger();
        }
    }

    public string? LogoPath(Station station)
    {
        if (string.IsNullOrEmpty(station.LogoFileName))
        {
            return null;
        }

        var path = _paths.LogoFile(station.LogoFileName);
        return File.Exists(path) ? path : null;
    }

    public static CompileResultDto ToCompileResult(CompiledStation compiled, bool changed)
    {
        return new CompileResultDto
        {
            Revision = compiled.Revision,
            ItemCount = compiled.TotalItemCount,
            Excluded = compiled.Excluded,
            CompiledUtc = compiled.CompiledUtc,
            ContentChanged = changed
        };
    }

    private DateTime DefaultAnchor(Station station)
    {
        var grid = TimeSpan.FromMinutes(Math.Max(station.PaddingGridMinutes, 1));
        return Padding.FloorToGrid(DateTime.UtcNow, grid);
    }

    private static void Normalize(Station s)
    {
        s.Name = (s.Name ?? string.Empty).Trim();
        s.ChannelNumber = (s.ChannelNumber ?? string.Empty).Trim();
        s.CallSign = string.IsNullOrWhiteSpace(s.CallSign) ? null : s.CallSign.Trim();
        s.TimeZoneId = string.IsNullOrWhiteSpace(s.TimeZoneId) ? "UTC" : s.TimeZoneId.Trim();
        s.PaddingGridMinutes = Math.Max(0, s.PaddingGridMinutes);
        if (s.PaddingByLength && s.PaddingGridMinutes == 0)
        {
            s.PaddingGridMinutes = 30; // anchor alignment only; program padding comes from the length rule
        }
        s.Pool ??= new PoolConfig();
        s.Slots ??= new SlotsConfig();
        s.Slots.Blocks ??= new List<SlotBlock>();
        s.Slots.Filler ??= new FillerConfig();
        s.Lineup ??= new LineupConfig();
        s.Lineup.Items ??= new List<LineupItem>();
        s.AnchorUtc = s.AnchorUtc == default ? default : DateTime.SpecifyKind(s.AnchorUtc.ToUniversalTime(), DateTimeKind.Utc);
        s.Bumpers ??= new BumperConfig();
        s.Bumpers.Sources ??= new List<ContentSource>();
        NormalizeSources(s.Bumpers.Sources);
        s.Bumpers.BreakMinutes = Math.Clamp(s.Bumpers.BreakMinutes, 0, 30);
        s.Bumpers.MaxClipMinutes = Math.Clamp(s.Bumpers.MaxClipMinutes, 1, 60);
        s.Bumpers.PreRoll ??= new BumperRollConfig();
        s.Bumpers.PreRoll.Sources ??= new List<ContentSource>();
        NormalizeSources(s.Bumpers.PreRoll.Sources);
        s.Bumpers.PostRoll ??= new BumperRollConfig();
        s.Bumpers.PostRoll.Sources ??= new List<ContentSource>();
        NormalizeSources(s.Bumpers.PostRoll.Sources);
        s.CardText = string.IsNullOrWhiteSpace(s.CardText) ? null : s.CardText.Trim();
        if (s.CardText is { Length: > 80 })
        {
            s.CardText = s.CardText[..80];
        }

        s.Weather ??= new WeatherConfig();
        s.Weather.Normalize();
        NormalizeSources(s.Weather.MusicSources);
        s.LogoOverlay ??= new LogoOverlayConfig();
        s.LogoOverlay.Normalize();
        s.UpNextCard ??= new UpNextCardConfig();
        s.UpNextCard.Normalize();
        s.MinRuntimeMinutes = Math.Clamp(s.MinRuntimeMinutes, 0, 600);
        s.ActiveWindows ??= new List<SeasonWindow>();
        s.ExcludedItemIds = (s.ExcludedItemIds ?? new List<Guid>()).Where(g => g != Guid.Empty).Distinct().ToList();
        s.ExcludedTags = NormalizeTags(s.ExcludedTags);
        foreach (var w in s.ActiveWindows)
        {
            w.Label = string.IsNullOrWhiteSpace(w.Label) ? null : w.Label.Trim();
        }

        s.Pool.Sources ??= new List<ContentSource>();
        s.Pool.DaypartRules ??= new List<DaypartRule>();
        foreach (var r in s.Pool.DaypartRules)
        {
            r.StartLocal = (r.StartLocal ?? string.Empty).Trim();
            r.EndLocal = (r.EndLocal ?? string.Empty).Trim();
            r.MaxRating = Library.RatingLadder.Normalize(r.MaxRating) ?? string.Empty;
        }

        s.Pool.CooldownHours = Math.Clamp(s.Pool.CooldownHours, 0, 168);
        s.Pool.MarathonMin = Math.Clamp(s.Pool.MarathonMin, 1, 50);
        s.Pool.MarathonMax = Math.Clamp(s.Pool.MarathonMax, s.Pool.MarathonMin, 50);
        s.Slots.Filler.Sources ??= new List<ContentSource>();
        NormalizeSources(s.Pool.Sources);
        NormalizeSources(s.Slots.Filler.Sources);
        foreach (var b in s.Slots.Blocks)
        {
            if (b.Id == Guid.Empty)
            {
                b.Id = Guid.NewGuid();
            }

            b.Days ??= new List<DayOfWeek>();
            b.Sources ??= new List<ContentSource>();
            b.MaxStartDelayMinutes = Math.Clamp(b.MaxStartDelayMinutes, 0, 240);
            NormalizeSources(b.Sources);
            NormalizeRecurrence(b, s);
        }
    }

    /// <summary>Clamps the recurrence fields and gives "every N weeks" a start date (today, station local) when it has none.</summary>
    private static void NormalizeRecurrence(SlotBlock b, Station s)
    {
        b.IntervalWeeks = Math.Clamp(b.IntervalWeeks, 1, 52);
        b.WeeksOfMonth = (b.WeeksOfMonth ?? new List<int>()).Where(w => w is >= 1 and <= 5).Distinct().OrderBy(w => w).ToList();
        b.Dates = (b.Dates ?? new List<string>()).Select(RecurrenceRule.ParseDate).Where(d => d.HasValue).Select(d => RecurrenceRule.Format(d!.Value)).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        var start = RecurrenceRule.ParseDate(b.RecurrenceStart);
        if (start is null && b.Recurrence == BlockRecurrence.EveryNWeeks)
        {
            var tz = WeeklySlotScheduler.ResolveTimeZone(s.TimeZoneId ?? "UTC");
            start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
        }

        b.RecurrenceStart = start is null ? null : RecurrenceRule.Format(start.Value);
    }

    /// <summary>Trims, drops blanks and de-duplicates a tag list (case-insensitive, first spelling wins).</summary>
    public static List<string> NormalizeTags(IEnumerable<string>? tags)
        => (tags ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void NormalizeSources(List<ContentSource> sources)
    {
        foreach (var src in sources)
        {
            src.ItemKinds ??= new List<SourceKind>();
            src.Tag = string.IsNullOrWhiteSpace(src.Tag) ? null : src.Tag.Trim();
            src.Weight = Math.Clamp(src.Weight, 1, 10);
            if (src.SeasonFrom is < 1)
            {
                src.SeasonFrom = null;
            }

            if (src.SeasonTo is < 1)
            {
                src.SeasonTo = null;
            }

            if (src.SeasonFrom.HasValue && src.SeasonTo.HasValue && src.SeasonFrom > src.SeasonTo)
            {
                (src.SeasonFrom, src.SeasonTo) = (src.SeasonTo, src.SeasonFrom);
            }

            src.MaxRating = Library.RatingLadder.Rank(src.MaxRating) is null ? null : Library.RatingLadder.Normalize(src.MaxRating);
            src.Ratings = (src.Ratings ?? new List<string>()).Select(Library.RatingLadder.Normalize).Where(r => r is not null).Select(r => r!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (src.YearFrom is < 1800)
            {
                src.YearFrom = null;
            }

            if (src.YearTo is < 1800)
            {
                src.YearTo = null;
            }

            if (src.YearFrom.HasValue && src.YearTo.HasValue && src.YearFrom > src.YearTo)
            {
                (src.YearFrom, src.YearTo) = (src.YearTo, src.YearFrom);
            }
        }
    }

    private List<string> Validate(Station s, Station? existing)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Name))
        {
            errors.Add("Name is required.");
        }

        for (var i = 0; i < s.Pool.DaypartRules.Count; i++)
        {
            var r = s.Pool.DaypartRules[i];
            if (!TimeSpan.TryParseExact(r.StartLocal, @"hh\:mm", null, out _) || !TimeSpan.TryParseExact(r.EndLocal, @"hh\:mm", null, out _))
            {
                errors.Add($"Daytime rule {i + 1}: times must be HH:mm.");
            }

            if (Library.RatingLadder.Rank(r.MaxRating) is null)
            {
                errors.Add($"Daytime rule {i + 1}: choose a rating from the list.");
            }
        }

        for (var i = 0; i < s.ActiveWindows.Count; i++)
        {
            if (!Scheduling.SeasonWindows.IsValid(s.ActiveWindows[i]))
            {
                errors.Add($"Season range {i + 1}: month must be 1-12 and day must exist in that month.");
            }
        }

        if (string.IsNullOrWhiteSpace(s.ChannelNumber))
        {
            errors.Add("Channel number is required.");
        }
        else if (_stations.GetAll().Any(o => o.Id != s.Id && string.Equals(o.ChannelNumber, s.ChannelNumber, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Channel number {s.ChannelNumber} is already used by another station.");
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId);
        }
        catch (Exception)
        {
            errors.Add($"Unknown time zone '{s.TimeZoneId}'.");
        }

        if (s.Mode == ScheduleMode.Pool && s.Pool.Sources.Count == 0)
        {
            errors.Add("Add at least one content source to the pool.");
        }

        if (s.Mode == ScheduleMode.Lineup && s.Lineup.Items.Count == 0)
        {
            errors.Add("Add at least one item to the lineup.");
        }

        if (s.Mode == ScheduleMode.Slots && s.Slots.Blocks.Count == 0 && s.Slots.Filler.Sources.Count == 0)
        {
            errors.Add("Add at least one time slot block or filler source.");
        }

        if (s.Mode == ScheduleMode.Weather)
        {
            if (!s.Weather.Locations.Any(l => l.IsValid))
            {
                errors.Add("Add at least one place to the weather channel.");
            }

            foreach (var l in s.Weather.Locations.Where(l => !l.IsValid))
            {
                errors.Add("Place \"" + (l.Name.Length == 0 ? "?" : l.Name) + "\": needs a name and coordinates; pick it from the search.");
            }

            if (!string.Equals(s.Weather.Provider, WeatherConfig.DefaultProvider, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Unknown weather provider '" + s.Weather.Provider + "'.");
            }
        }

        if (s.Mode != ScheduleMode.Lineup)
        {
            for (var i = 0; i < s.Slots.Blocks.Count; i++)
            {
                var b = s.Slots.Blocks[i];
                var label = string.IsNullOrWhiteSpace(b.Name) ? $"Block {i + 1}" : b.Name;
                if (b.Days.Count == 0 && b.Recurrence != BlockRecurrence.Dates)
                {
                    errors.Add($"{label}: choose at least one day.");
                }

                if (b.Recurrence == BlockRecurrence.WeeksOfMonth && b.WeeksOfMonth.Count == 0)
                {
                    errors.Add($"{label}: choose at least one week of the month.");
                }

                if (b.Recurrence == BlockRecurrence.Dates && b.Dates.Count == 0)
                {
                    errors.Add($"{label}: add at least one date.");
                }

                if (b.DurationMinutes < 5 || b.DurationMinutes > 1440)
                {
                    errors.Add($"{label}: duration must be between 5 and 1440 minutes.");
                }

                if (!TimeSpan.TryParseExact(b.StartLocal ?? string.Empty, @"hh\:mm", CultureInfo.InvariantCulture, out _))
                {
                    errors.Add($"{label}: start time must be HH:mm.");
                }

                if (b.Sources.Count == 0)
                {
                    errors.Add($"{label}: add at least one content source.");
                }
            }
        }

        if (s.Encoding is not null)
        {
            errors.AddRange(ValidateEncoding(s.Encoding).Select(e => "Encoding: " + e));
        }

        return errors;
    }

    public static List<string> ValidateEncoding(EncodingProfile p)
    {
        var errors = new List<string>();
        if (p.Width < 160 || p.Width > 4096 || p.Height < 90 || p.Height > 2304)
        {
            errors.Add("resolution out of range.");
        }

        if (p.Fps < 10 || p.Fps > 60)
        {
            errors.Add("fps must be between 10 and 60.");
        }

        if (p.VideoBitrateKbps < 200 || p.VideoBitrateKbps > 50000)
        {
            errors.Add("video bitrate must be between 200 and 50000 kbps.");
        }

        if (p.AudioBitrateKbps < 32 || p.AudioBitrateKbps > 512)
        {
            errors.Add("audio bitrate must be between 32 and 512 kbps.");
        }

        if (string.IsNullOrWhiteSpace(p.VideoEncoder))
        {
            errors.Add("video encoder is required.");
        }

        var hw = (p.HardwareDecode ?? string.Empty).Trim().ToLowerInvariant();
        if (hw.Length > 0 && hw != EncodingProfile.HardwareDecodeAuto && !EncodingProfile.HardwareDecodeKinds.Contains(hw, StringComparer.Ordinal))
        {
            errors.Add("hardware decoding must be auto, none, " + string.Join(", ", EncodingProfile.HardwareDecodeKinds) + ".");
        }

        return errors;
    }

    private static string ScheduleFingerprint(Station s)
    {
        return JsonFileStore.Serialize(new
        {
            s.Mode,
            s.Pool,
            s.Slots,
            s.Lineup,
            s.AnchorUtc,
            s.PaddingGridMinutes,
            s.PaddingByLength,
            s.TimeZoneId,
            s.Bumpers,
            s.MinRuntimeMinutes,
            s.Weather
        });
    }
}

public sealed class ValidationException : Exception
{
    public ValidationException(IEnumerable<string> errors)
        : base("Validation failed")
    {
        Errors = errors.ToList();
    }

    public List<string> Errors { get; }
}
