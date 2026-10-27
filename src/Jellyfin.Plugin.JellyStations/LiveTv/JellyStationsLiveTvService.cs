using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.LiveTv;

/// <summary>Exposes stations as native Live TV channels with guide data and shared live streams.</summary>
public sealed class JellyStationsLiveTvService : ILiveTvService, ISupportsDirectStreamProvider
{
    private readonly StationService _stations;
    private readonly IScheduleResolver _resolver;
    private readonly StreamSessionManager _sessions;
    private readonly ILogger<JellyStationsLiveTvService> _logger;

    public JellyStationsLiveTvService(StationService stations, IScheduleResolver resolver, StreamSessionManager sessions, ILogger<JellyStationsLiveTvService> logger)
    {
        _stations = stations;
        _resolver = resolver;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Stays "TvStation", the name the plugin shipped under: Jellyfin derives every Live TV channel's internal id
    /// from the service name plus the channel id, so changing it would recreate every channel and drop favourites.
    /// </summary>
    public string Name => "TvStation";

    public string HomePageUrl => "https://github.com/jdaubert12/JellyStations";

    public static string ChannelId(Guid stationId) => stationId.ToString("N");

    public static bool TryParseChannelId(string? id, out Guid stationId) => Guid.TryParse(id, out stationId);

    /// <summary>
    /// The station behind an id Jellyfin hands back: a channel id (the station id itself) or a media source id,
    /// which is a hash of the station and its output format (StreamSessionManager.MediaSourceId) and so has to be
    /// found by comparison.
    /// </summary>
    public static bool TryResolveStationId(string? id, IEnumerable<Station> stations, Func<Station, EncodingProfile> profileOf, out Guid stationId)
    {
        stationId = Guid.Empty;
        if (!Guid.TryParse(id, out var parsed))
        {
            return false;
        }

        foreach (var station in stations)
        {
            if (station.Id == parsed || string.Equals(StreamSessionManager.MediaSourceId(station, profileOf(station)), parsed.ToString("N"), StringComparison.OrdinalIgnoreCase))
            {
                stationId = station.Id;
                return true;
            }
        }

        return false;
    }

    private bool TryResolveStationId(string? id, out Guid stationId)
        => TryResolveStationId(id, _stations.GetAll(), _stations.EffectiveProfile, out stationId);

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var channels = _stations.GetAll()
            .Where(s => s.Enabled && SeasonWindows.IsActive(s, now))
            .Select(s =>
            {
                var logo = _stations.LogoPath(s);
                return new ChannelInfo
                {
                    Id = ChannelId(s.Id),
                    Name = s.Name,
                    Number = s.ChannelNumber,
                    CallSign = s.CallSign ?? s.Name,
                    ChannelType = ChannelType.TV,
                    ChannelGroup = "JellyStations",
                    ImagePath = logo,
                    HasImage = logo is not null,
                    IsHD = true,
                    VideoCodec = _stations.EffectiveProfile(s).OutputCodec,
                    AudioCodec = "aac"
                };
            })
            .ToList();

        return Task.FromResult<IEnumerable<ChannelInfo>>(channels);
    }

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        if (!TryParseChannelId(channelId, out var stationId))
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        var entries = _resolver.Enumerate(stationId, startDateUtc, endDateUtc);
        var programs = new List<ProgramInfo>();
        ProgramInfo? current = null;
        var station = _stations.Get(stationId);
        var cardText = station?.EffectiveCardText ?? Station.DefaultCardText;
        var logo = station is null ? null : _stations.LogoPath(station);

        foreach (var entry in entries)
        {
            if (entry.Kind == EntryKind.Weather && !entry.InBreak)
            {
                current = WeatherProgram(channelId, entry, logo);
                programs.Add(current);
                continue;
            }

            if (entry.Kind != EntryKind.Program || entry.Item is null)
            {
                // Cards, bumper clips and weather in a break are not guide entries; they extend whatever aired before them.
                if (current is not null)
                {
                    current.EndDate = entry.EndUtc;
                }
                else
                {
                    current = OffAir(channelId, entry, cardText);
                    programs.Add(current);
                }

                continue;
            }

            current = ToProgram(channelId, entry, entry.Item);
            programs.Add(current);
        }

        _logger.LogDebug("JellyStations: {Count} program(s) for channel {Channel} in [{Start}, {End})", programs.Count, channelId, startDateUtc, endDateUtc);
        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    private static ProgramInfo ToProgram(string channelId, ScheduleEntry entry, PlayableItem item)
    {
        var p = new ProgramInfo
        {
            Id = entry.ProgramId,
            ChannelId = channelId,
            Name = item.Title,
            EpisodeTitle = item.IsSeries ? item.EpisodeTitle : null,
            Overview = item.Overview,
            StartDate = DateTime.SpecifyKind(entry.StartUtc, DateTimeKind.Utc),
            EndDate = DateTime.SpecifyKind(entry.EndUtc, DateTimeKind.Utc),
            Genres = item.Genres.ToList(),
            IsSeries = item.IsSeries,
            IsMovie = item.IsMovie,
            IsRepeat = true,
            SeasonNumber = item.SeasonNumber,
            EpisodeNumber = item.EpisodeNumber,
            ProductionYear = item.ProductionYear,
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            IsHD = true
        };

        if (item.SeriesId.HasValue)
        {
            p.SeriesId = item.SeriesId.Value.ToString("N");
            p.ShowId = item.SeriesId.Value.ToString("N");
        }

        if (!string.IsNullOrEmpty(item.PrimaryImagePath) && File.Exists(item.PrimaryImagePath))
        {
            p.ImagePath = item.PrimaryImagePath;
            p.HasImage = true;
        }

        if (entry.IsTruncated)
        {
            p.ShortOverview = "Joined in progress / ends early for the next scheduled program.";
        }

        return p;
    }

    /// <summary>A weather program has no library item to take a poster from, so it wears the channel's logo when one is set.</summary>
    private static ProgramInfo WeatherProgram(string channelId, ScheduleEntry entry, string? logoPath)
    {
        return new ProgramInfo
        {
            Id = entry.ProgramId,
            ChannelId = channelId,
            Name = entry.Title,
            Overview = "Current conditions, the next twelve hours and the week ahead, with music.",
            StartDate = DateTime.SpecifyKind(entry.StartUtc, DateTimeKind.Utc),
            EndDate = DateTime.SpecifyKind(entry.EndUtc, DateTimeKind.Utc),
            Genres = new List<string> { "Weather" },
            IsNews = true,
            IsLive = true,
            IsRepeat = false,
            IsHD = true,
            ImagePath = logoPath,
            HasImage = logoPath is not null
        };
    }

    private static ProgramInfo OffAir(string channelId, ScheduleEntry entry, string cardText)
    {
        return new ProgramInfo
        {
            Id = entry.ProgramId,
            ChannelId = channelId,
            Name = cardText,
            Overview = "No program scheduled.",
            StartDate = DateTime.SpecifyKind(entry.StartUtc, DateTimeKind.Utc),
            EndDate = DateTime.SpecifyKind(entry.EndUtc, DateTimeKind.Utc),
            Genres = new List<string>(),
            IsRepeat = true
        };
    }

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var station = RequireStation(channelId);
        var source = StreamSessionManager.BuildMediaSource(station, _stations.EffectiveProfile(station));
        return Task.FromResult(new List<MediaSourceInfo> { source });
    }

    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        var station = RequireStation(channelId);
        var session = await _sessions.AcquireAsync(station, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        var station = RequireStation(channelId);
        var session = await _sessions.AcquireAsync(station, cancellationToken).ConfigureAwait(false);
        return session.MediaSource;
    }

    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        if (TryResolveStationId(id, out var stationId))
        {
            var session = _sessions.Get(stationId);
            if (session is not null)
            {
                session.ConsumerCount--;
                if (session.ConsumerCount <= 0)
                {
                    return session.Close();
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task ResetTuner(string id, CancellationToken cancellationToken)
    {
        if (TryResolveStationId(id, out var stationId))
        {
            _sessions.RestartStation(stationId);
        }

        return Task.CompletedTask;
    }

    private Station RequireStation(string channelId)
    {
        if (!TryParseChannelId(channelId, out var stationId))
        {
            throw new ArgumentException("Invalid JellyStations channel id: " + channelId);
        }

        var station = _stations.Get(stationId);
        if (station is null)
        {
            throw new KeyNotFoundException("JellyStations channel not found: " + channelId);
        }

        if (!station.Enabled)
        {
            throw new InvalidOperationException("JellyStations channel is disabled: " + station.Name);
        }

        if (!SeasonWindows.IsActive(station, DateTime.UtcNow))
        {
            throw new InvalidOperationException("JellyStations channel is off season: " + station.Name);
        }

        return station;
    }

    // Recording is not supported; the guide UI still needs sane defaults.
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<TimerInfo>());

    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
    {
        return Task.FromResult(new SeriesTimerInfo
        {
            PrePaddingSeconds = 0,
            PostPaddingSeconds = 0,
            RecordAnyChannel = false,
            RecordAnyTime = true,
            RecordNewOnly = false,
            Days = new List<DayOfWeek>(),
            Priority = 0
        });
    }

    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => throw new NotImplementedException("JellyStations does not support recording.");

    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => throw new NotImplementedException("JellyStations does not support recording.");

    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => throw new NotImplementedException("JellyStations does not support recording.");

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => throw new NotImplementedException("JellyStations does not support recording.");

    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken) => throw new NotImplementedException("JellyStations does not support recording.");

    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => throw new NotImplementedException("JellyStations does not support recording.");
}
