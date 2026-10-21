using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>
/// One live stream per station, shared by every viewer. Implements Jellyfin's ILiveStream so the
/// server serves our MPEG-TS at /LiveTv/LiveStreamFiles/{UniqueId}/stream.ts.
/// </summary>
public sealed class StationStreamSession : ILiveStream
{
    private readonly object _lock = new();
    private readonly Func<BroadcastBuffer, FfmpegRestreamer> _restreamerFactory;
    private readonly ILogger _logger;
    private BroadcastBuffer _buffer = new();
    private FfmpegRestreamer? _restreamer;
    private bool _disposed;

    public StationStreamSession(
        Guid stationId,
        string stationName,
        MediaSourceInfo mediaSource,
        Func<BroadcastBuffer, FfmpegRestreamer> restreamerFactory,
        ILogger logger)
    {
        StationId = stationId;
        StationName = stationName;
        MediaSource = mediaSource;
        OriginalStreamId = mediaSource.Id;
        UniqueId = "tvs" + Guid.NewGuid().ToString("N");
        _restreamerFactory = restreamerFactory;
        _logger = logger;
    }

    /// <summary>Raised when the server reports that the last consumer has left.</summary>
    public event EventHandler? Released;

    public Guid StationId { get; }

    public string StationName { get; }

    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    public int ConsumerCount { get; set; }

    public string OriginalStreamId { get; set; }

    public string TunerHostId => "TvStation";

    public bool EnableStreamSharing => true;

    public MediaSourceInfo MediaSource { get; set; }

    public string UniqueId { get; }

    public ScheduleEntry? CurrentEntry => _restreamer?.CurrentEntry;

    public int? EncoderPid => _restreamer?.CurrentPid;

    public IReadOnlyList<string> RecentErrors => _restreamer?.RecentErrors ?? Array.Empty<string>();

    /// <summary>What the encoder is doing right now (decoder, CPU, real-time margin), or null before it started.</summary>
    public EncoderSnapshot? Encoder => _restreamer?.Snapshot();

    public int ReaderCount => _buffer.ConsumerCount;

    public long BytesStreamed => _buffer.TotalBytes;

    public bool IsDisposed => _disposed;

    public async Task Open(CancellationToken openCancellationToken)
    {
        FfmpegRestreamer restreamer;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_restreamer is null)
            {
                _restreamer = _restreamerFactory(_buffer);
                _restreamer.Start();
            }

            restreamer = _restreamer;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(openCancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await restreamer.FirstBytes.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!openCancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("TvStation: the encoder for '" + StationName + "' produced no data within 20 seconds. Check ffmpeg path and media file access.");
        }
    }

    /// <summary>Called by the server when ConsumerCount reaches zero. We linger instead of stopping.</summary>
    public Task Close()
    {
        Released?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Stream GetStream()
    {
        return _buffer.Subscribe();
    }

    /// <summary>Stops the current encoder and starts a fresh one at the live position; readers keep their stream.</summary>
    public void RestartEncoder()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _restreamer?.Dispose();
            _buffer.ClearPreRoll();
            _restreamer = _restreamerFactory(_buffer);
            _restreamer.Start();
        }

        _logger.LogInformation("TvStation [{Station}]: encoder restarted", StationName);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _restreamer?.Dispose();
            _restreamer = null;
            _buffer.Complete();
        }
    }
}
