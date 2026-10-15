using System.Collections.Concurrent;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>Owns one StationStreamSession per station, with linger-based teardown.</summary>
public sealed class StreamSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, StationStreamSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _lingers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IScheduleResolver _resolver;
    private readonly SettingsRepository _settings;
    private readonly TvStationPaths _paths;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerApplicationHost _appHost;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<StreamSessionManager> _logger;

    public StreamSessionManager(
        IScheduleResolver resolver,
        SettingsRepository settings,
        TvStationPaths paths,
        IMediaEncoder mediaEncoder,
        IServerApplicationHost appHost,
        ILoggerFactory loggerFactory)
    {
        _resolver = resolver;
        _settings = settings;
        _paths = paths;
        _mediaEncoder = mediaEncoder;
        _appHost = appHost;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StreamSessionManager>();
    }

    public string FfmpegPath
    {
        get
        {
            var o = _settings.Get().FfmpegPathOverride;
            return string.IsNullOrWhiteSpace(o) ? _mediaEncoder.EncoderPath : o;
        }
    }

    public IReadOnlyCollection<StationStreamSession> Sessions => _sessions.Values.ToList();

    public StationStreamSession? Get(Guid stationId) => _sessions.TryGetValue(stationId, out var s) ? s : null;

    /// <summary>Returns the running session for a station, starting one if needed, and counts a new consumer.</summary>
    public async Task<StationStreamSession> AcquireAsync(Station station, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        StationStreamSession session;
        var created = false;
        try
        {
            if (_lingers.TryRemove(station.Id, out var linger))
            {
                linger.Cancel();
                linger.Dispose();
            }

            if (!_sessions.TryGetValue(station.Id, out session!) || session.IsDisposed)
            {
                var settings = _settings.Get();
                var active = _sessions.Values.Count(s => !s.IsDisposed);
                if (active >= Math.Max(1, settings.MaxConcurrentSessions))
                {
                    throw new InvalidOperationException($"TvStation: maximum of {settings.MaxConcurrentSessions} concurrent station streams reached.");
                }

                session = CreateSession(station, settings);
                _sessions[station.Id] = session;
                created = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (created)
        {
            try
            {
                await session.Open(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Remove(station.Id);
                throw;
            }
        }

        session.ConsumerCount = Math.Max(0, session.ConsumerCount) + 1;
        _logger.LogInformation("TvStation [{Station}]: consumer attached (now {Count})", station.Name, session.ConsumerCount);
        return session;
    }

    public void RestartStation(Guid stationId)
    {
        if (_sessions.TryGetValue(stationId, out var s) && !s.IsDisposed)
        {
            s.RestartEncoder();
        }
    }

    public void StopStation(Guid stationId) => Remove(stationId);

    public void StopAll()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            Remove(id);
        }
    }

    private StationStreamSession CreateSession(Station station, GlobalSettings settings)
    {
        var profile = station.Encoding ?? settings.DefaultEncoding;
        var ffmpeg = FfmpegPath;
        var mediaSource = BuildMediaSource(station, profile);
        var logger = _loggerFactory.CreateLogger("Jellyfin.Plugin.TvStation.Stream." + station.Name);

        StationStreamSession? session = null;
        session = new StationStreamSession(
            station.Id,
            station.Name,
            mediaSource,
            buffer => new FfmpegRestreamer(
                station.Id,
                station.Name,
                ffmpeg,
                profile,
                settings,
                _resolver,
                () => string.IsNullOrEmpty(station.LogoFileName) ? null : _paths.LogoFile(station.LogoFileName),
                station.LogoOverlay ?? new LogoOverlayConfig(),
                station.EffectiveCardText,
                buffer,
                logger),
            logger);

        session.MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + session.UniqueId + "/stream.ts";
        session.Released += (_, _) => OnReleased(session);
        _logger.LogInformation("TvStation [{Station}]: stream session {Id} created", station.Name, session.UniqueId);
        return session;
    }

    private void OnReleased(StationStreamSession session)
    {
        var linger = TimeSpan.FromSeconds(Math.Max(0, _settings.Get().LingerSeconds));
        _logger.LogInformation("TvStation [{Station}]: last consumer left; lingering {Seconds}s", session.StationName, linger.TotalSeconds);
        session.ConsumerCount = 0;

        var cts = new CancellationTokenSource();
        var previous = _lingers.AddOrUpdate(session.StationId, cts, (_, old) =>
        {
            old.Cancel();
            old.Dispose();
            return cts;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(linger, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_lingers.TryRemove(session.StationId, out var current) && ReferenceEquals(current, cts))
            {
                if (_sessions.TryGetValue(session.StationId, out var s) && ReferenceEquals(s, session) && s.ConsumerCount <= 0)
                {
                    Remove(session.StationId);
                }
            }
        });
    }

    private void Remove(Guid stationId)
    {
        if (_lingers.TryRemove(stationId, out var linger))
        {
            linger.Cancel();
            linger.Dispose();
        }

        if (_sessions.TryRemove(stationId, out var session))
        {
            _logger.LogInformation("TvStation [{Station}]: stream session {Id} stopped", session.StationName, session.UniqueId);
            session.Dispose();
        }
    }

    /// <summary>The media source description shared by PlaybackInfo and the opened stream.</summary>
    public static MediaSourceInfo BuildMediaSource(Station station, EncodingProfile profile)
    {
        var channelId = station.Id.ToString("N");
        return new MediaSourceInfo
        {
            Id = channelId,
            Name = station.Name,
            Protocol = MediaProtocol.Http,
            Container = "mpegts",
            Path = string.Empty,
            IsRemote = false,
            IsInfiniteStream = true,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true,
            IgnoreDts = true,
            ReadAtNativeFramerate = false,
            Bitrate = (profile.VideoBitrateKbps + profile.AudioBitrateKbps) * 1000,
            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = -1,
                    Codec = "h264",
                    Width = profile.Width,
                    Height = profile.Height,
                    AverageFrameRate = profile.Fps,
                    RealFrameRate = profile.Fps,
                    BitRate = profile.VideoBitrateKbps * 1000,
                    IsInterlaced = false,
                    Profile = profile.H264Profile,
                    PixelFormat = "yuv420p"
                },
                new()
                {
                    Type = MediaStreamType.Audio,
                    Index = -1,
                    Codec = "aac",
                    Channels = 2,
                    SampleRate = 48000,
                    BitRate = profile.AudioBitrateKbps * 1000,
                    IsDefault = true
                }
            }
        };
    }

    public void Dispose()
    {
        StopAll();
        _gate.Dispose();
    }
}
