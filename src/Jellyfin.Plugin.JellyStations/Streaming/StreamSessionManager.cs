using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>Owns one StationStreamSession per station, with linger-based teardown and an unread-stream watchdog.</summary>
public sealed class StreamSessionManager : IDisposable
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<Guid, StationStreamSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _lingers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IScheduleResolver _resolver;
    private readonly SettingsRepository _settings;
    private readonly JellyStationsPaths _paths;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Weather.WeatherChannelService? _weather;
    private readonly ILogger<StreamSessionManager> _logger;
    private readonly ConcurrentDictionary<Guid, StationFault> _faults = new();
    private readonly System.Threading.Timer _watchdog;

    public StreamSessionManager(
        IScheduleResolver resolver,
        SettingsRepository settings,
        JellyStationsPaths paths,
        IMediaEncoder mediaEncoder,
        IServerApplicationHost appHost,
        IServerConfigurationManager configuration,
        ILoggerFactory loggerFactory,
        Weather.WeatherChannelService? weather = null)
    {
        _weather = weather;
        _resolver = resolver;
        _settings = settings;
        _paths = paths;
        _mediaEncoder = mediaEncoder;
        _appHost = appHost;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StreamSessionManager>();
        _watchdog = new System.Threading.Timer(_ => SweepUnreadSessions(), null, WatchdogInterval, WatchdogInterval);
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

    /// <summary>The last streaming fault recorded for a station, kept after its session ends until cleared or replaced.</summary>
    public StationFault? LastFault(Guid stationId) => _faults.TryGetValue(stationId, out var f) ? f : null;

    public void ClearFault(Guid stationId) => _faults.TryRemove(stationId, out _);

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
                    throw new InvalidOperationException($"JellyStations: maximum of {settings.MaxConcurrentSessions} concurrent station streams reached.");
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
        _logger.LogInformation("JellyStations [{Station}]: consumer attached (now {Count})", station.Name, session.ConsumerCount);
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
        var profile = ResolveHardware(station.Encoding ?? settings.DefaultEncoding);
        var policy = DecodePolicy(profile);
        var ffmpeg = FfmpegPath;
        var mediaSource = BuildMediaSource(station, profile);
        var logger = _loggerFactory.CreateLogger("Jellyfin.Plugin.JellyStations.Stream." + station.Name);

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
                logger,
                message => _faults[station.Id] = new StationFault(DateTime.UtcNow, message),
                policy,
                station.UpNextCard ?? new UpNextCardConfig(),
                _paths.CardsDir,
                station.TimeZoneId,
                _weather is null ? null : (entry, now, p) => _weather.BuildSegment(station.Id, now, entry, p)),
            logger);

        session.MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + session.UniqueId + "/stream.ts";
        session.Released += (_, _) => OnReleased(session);
        _logger.LogInformation("JellyStations [{Station}]: stream session {Id} created", station.Name, session.UniqueId);
        return session;
    }

    /// <summary>Jellyfin's own Dashboard > Playback > Transcoding hardware settings.</summary>
    public sealed record JellyfinHardware(HardwareAccelerationType Type, string? QsvDevice, string? VaapiDevice, IReadOnlyList<string> DecodingCodecs, bool Hevc10, bool Vp9Ten, bool HevcRext10, bool HevcRext12);

    public JellyfinHardware ReadJellyfinHardware()
    {
        try
        {
            if (_configuration.GetConfiguration("encoding") is EncodingOptions options)
            {
                return new JellyfinHardware(
                    options.HardwareAccelerationType,
                    options.QsvDevice,
                    options.VaapiDevice,
                    options.HardwareDecodingCodecs?.ToList() ?? new List<string>(),
                    options.EnableDecodingColorDepth10Hevc,
                    options.EnableDecodingColorDepth10Vp9,
                    options.EnableDecodingColorDepth10HevcRext,
                    options.EnableDecodingColorDepth12HevcRext);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyStations: could not read Jellyfin's encoding options; hardware decoding stays off for 'auto'");
        }

        return new JellyfinHardware(HardwareAccelerationType.none, null, null, Array.Empty<string>(), true, true, false, false);
    }

    /// <summary>
    /// Which files the resolved decoder is handed: Jellyfin's per-codec checkboxes when the decoder is the one
    /// Jellyfin itself uses (so "Follow Jellyfin" behaves exactly like Jellyfin's transcoder), the permissive
    /// built-in list for a decoder picked by hand that Jellyfin is not using.
    /// </summary>
    public HardwareDecodePolicy DecodePolicy(EncodingProfile resolved) => DecodePolicy(resolved, ReadJellyfinHardware(), OperatingSystem.IsWindows());

    public static HardwareDecodePolicy DecodePolicy(EncodingProfile resolved, JellyfinHardware jellyfin, bool isWindows)
    {
        var kind = resolved.HardwareDecode ?? string.Empty;
        if (kind.Length == 0)
        {
            return HardwareDecodePolicy.Software;
        }

        var jellyfinKind = HardwareDecodeResolver.Resolve(new EncodingProfile(), jellyfin.Type, null, null, isWindows).HardwareDecode;
        return string.Equals(kind, jellyfinKind, StringComparison.Ordinal)
            ? new HardwareDecodePolicy(kind, jellyfin.DecodingCodecs, jellyfin.Hevc10, jellyfin.Vp9Ten, jellyfin.HevcRext10, jellyfin.HevcRext12, fromJellyfin: true)
            : HardwareDecodePolicy.Permissive(kind);
    }

    /// <summary>"auto" hardware decoding and an empty device follow Jellyfin's Dashboard > Playback > Transcoding settings.</summary>
    public EncodingProfile ResolveHardware(EncodingProfile profile)
    {
        var jellyfin = ReadJellyfinHardware();
        return HardwareDecodeResolver.Resolve(profile, jellyfin.Type, jellyfin.QsvDevice, jellyfin.VaapiDevice, OperatingSystem.IsWindows());
    }

    /// <summary>
    /// Stops any session nobody has read for longer than the idle-stop setting. Jellyfin only reports a viewer gone
    /// when the client says so or its connection to the server closes, so an app that was killed, a TV switched off
    /// on a channel or a dropped network can leave the viewer count stuck at 1 and ffmpeg encoding for nobody.
    /// The HTTP reader count tells the truth within seconds of the socket dying.
    /// </summary>
    public void SweepUnreadSessions()
    {
        var idleStop = _settings.Get().IdleStopSeconds;
        if (idleStop <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Values.ToList())
        {
            if (session.IsDisposed)
            {
                continue;
            }

            var unreadFor = UnreadFor(session.ReadersIdleSinceUtc, now);
            if (unreadFor < TimeSpan.FromSeconds(idleStop))
            {
                continue;
            }

            _logger.LogWarning(
                "JellyStations [{Station}]: nobody has read the stream for {Seconds:0}s although {Count} viewer(s) are still counted; stopping the encoder",
                session.StationName,
                unreadFor.TotalSeconds,
                session.ConsumerCount);
            Remove(session.StationId);
        }
    }

    /// <summary>How long a stream has had no reader; zero while someone is reading.</summary>
    public static TimeSpan UnreadFor(DateTime? idleSinceUtc, DateTime nowUtc)
    {
        if (idleSinceUtc is null)
        {
            return TimeSpan.Zero;
        }

        var unread = nowUtc - idleSinceUtc.Value;
        return unread < TimeSpan.Zero ? TimeSpan.Zero : unread;
    }

    private void OnReleased(StationStreamSession session)
    {
        if (session.IsDisposed)
        {
            return;
        }

        var linger = TimeSpan.FromSeconds(Math.Max(0, _settings.Get().LingerSeconds));
        _logger.LogInformation("JellyStations [{Station}]: last consumer left; lingering {Seconds}s", session.StationName, linger.TotalSeconds);
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
            _logger.LogInformation("JellyStations [{Station}]: stream session {Id} stopped", session.StationName, session.UniqueId);
            session.Dispose();
        }
    }

    /// <summary>The media source description shared by PlaybackInfo and the opened stream.</summary>
    public static MediaSourceInfo BuildMediaSource(Station station, EncodingProfile profile)
    {
        var channelId = station.Id.ToString("N");
        var hdr = profile.IsHdrOutput;
        var hevc = profile.IsHevcEncoder;
        // HEVC levels as ffprobe reports them (4.1 = 123, 5.1 = 153); H.264 "4.1" as 41.
        var level = hevc
            ? (profile.Width * (long)profile.Height * profile.Fps > 1920L * 1080 * 30 ? 153 : 123)
            : double.TryParse(profile.H264Level, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var l) ? (double?)Math.Round(l * 10) : null;
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
            // Not probed: Jellyfin caches a live channel's probe per channel for good, so a station switched to
            // HDR10 (or back) would keep being announced with its old codec and range. The declaration below is
            // exact, and skipping the probe also drops Jellyfin's probe delay from every tune-in.
            SupportsProbing = false,
            IgnoreDts = true,
            ReadAtNativeFramerate = false,
            Bitrate = (profile.VideoBitrateKbps + profile.AudioBitrateKbps) * 1000,
            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    // What the restreamer produces, declared so Jellyfin's direct-play decision (and its HDR range
                    // classification, which it derives from the transfer) is right before it probes the stream.
                    Type = MediaStreamType.Video,
                    Index = -1,
                    Codec = profile.OutputCodec,
                    Width = profile.Width,
                    Height = profile.Height,
                    AverageFrameRate = profile.Fps,
                    RealFrameRate = profile.Fps,
                    BitRate = profile.VideoBitrateKbps * 1000,
                    IsInterlaced = false,
                    Profile = hdr ? "Main 10" : hevc ? "Main" : profile.H264Profile,
                    Level = level,
                    PixelFormat = hdr ? "yuv420p10le" : "yuv420p",
                    BitDepth = hdr ? 10 : 8,
                    ColorPrimaries = hdr ? "bt2020" : "bt709",
                    ColorTransfer = hdr ? "smpte2084" : "bt709",
                    ColorSpace = hdr ? "bt2020nc" : "bt709",
                    ColorRange = "tv"
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
        _watchdog.Dispose();
        StopAll();
        _gate.Dispose();
    }
}
