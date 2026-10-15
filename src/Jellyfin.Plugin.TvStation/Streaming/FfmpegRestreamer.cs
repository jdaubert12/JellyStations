using System.Collections.Concurrent;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>
/// Keeps a station's live MPEG-TS flowing: for each program on the timeline it runs one ffmpeg
/// process from the correct offset, pacing in real time, and hands the bytes to the BroadcastBuffer.
/// </summary>
public sealed class FfmpegRestreamer : IDisposable
{
    private const int MaxLogLines = 50;
    private static readonly TimeSpan MinSegment = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HardKillGrace = TimeSpan.FromSeconds(2);

    private readonly Guid _stationId;
    private readonly string _stationName;
    private readonly string _ffmpegPath;
    private readonly EncodingProfile _profile;
    private readonly GlobalSettings _settings;
    private readonly IScheduleResolver _resolver;
    private readonly Func<string?> _logoPath;
    private readonly string _cardText;
    private readonly BroadcastBuffer _buffer;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _firstBytes = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _recentErrors = new();
    private ProcessRunner? _current;
    private Task? _loop;
    private bool _slateTextSupported = true;
    private double _tsOffsetFloorSeconds;

    public FfmpegRestreamer(
        Guid stationId,
        string stationName,
        string ffmpegPath,
        EncodingProfile profile,
        GlobalSettings settings,
        IScheduleResolver resolver,
        Func<string?> logoPath,
        string cardText,
        BroadcastBuffer buffer,
        ILogger logger)
    {
        _stationId = stationId;
        _stationName = stationName;
        _ffmpegPath = ffmpegPath;
        _profile = profile;
        _settings = settings;
        _resolver = resolver;
        _logoPath = logoPath;
        _cardText = string.IsNullOrWhiteSpace(cardText) ? Station.DefaultCardText : cardText;
        _buffer = buffer;
        _logger = logger;
    }

    public DateTime StartedUtc { get; private set; }

    public ScheduleEntry? CurrentEntry { get; private set; }

    public int? CurrentPid => _current?.HasExited == false ? _current.Pid : null;

    public Task FirstBytes => _firstBytes.Task;

    public IReadOnlyList<string> RecentErrors => _recentErrors.ToArray();

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        StartedUtc = DateTime.UtcNow;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        string? lastFailedProgramId = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var resolved = _resolver.Resolve(_stationId, now);
                if (resolved is null)
                {
                    await RunSlateAsync(TimeSpan.FromSeconds(30), "Station unavailable", ct).ConfigureAwait(false);
                    continue;
                }

                var entry = resolved.Entry;
                var remaining = entry.EndUtc - now;
                if (remaining < MinSegment)
                {
                    await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                    continue;
                }

                CurrentEntry = entry;

                var playable = entry.Kind != EntryKind.Slate
                    && entry.Item is not null
                    && entry.ProgramId != lastFailedProgramId
                    && File.Exists(entry.Item.Path);

                if (!playable)
                {
                    if (entry.Kind != EntryKind.Slate && entry.Item is not null && entry.ProgramId != lastFailedProgramId)
                    {
                        _logger.LogWarning("TvStation [{Station}]: file missing for {Title}: {Path}", _stationName, entry.Item.DisplayTitle, entry.Item.Path);
                        Note("File missing: " + entry.Item.Path);
                    }

                    var text = entry.Kind == EntryKind.Slate ? _cardText : Station.DefaultCardText;
                    await RunSlateAsync(remaining, text, ct, entry.EndUtc).ConfigureAwait(false);
                    continue;
                }

                var item = entry.Item!;
                var seek = resolved.Offset;
                var duration = entry.IsTruncated || entry.EndUtc < entry.StartUtc + item.Runtime ? remaining : (TimeSpan?)null;
                var args = FfmpegArgsBuilder.ForFile(item, seek, duration, NextTsOffset(), _profile);

                _logger.LogInformation(
                    "TvStation [{Station}]: playing {Title} from {Seek} ({Remaining} remaining)",
                    _stationName,
                    item.DisplayTitle,
                    seek.ToString(@"hh\:mm\:ss"),
                    remaining.ToString(@"hh\:mm\:ss"));
                _logger.LogDebug("TvStation ffmpeg {Args}", FfmpegArgsBuilder.ToLogString(args));

                var started = DateTime.UtcNow;
                var exitCode = await RunProcessAsync(args, entry.EndUtc + HardKillGrace, ct).ConfigureAwait(false);
                var ran = DateTime.UtcNow - started;

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var endedEarly = DateTime.UtcNow < entry.EndUtc - MinSegment;
                if (exitCode != 0 && ran < TimeSpan.FromSeconds(5))
                {
                    consecutiveFailures++;
                    lastFailedProgramId = entry.ProgramId;
                    _logger.LogWarning("TvStation [{Station}]: ffmpeg exited with {Code} after {Seconds:0.0}s for {Path}", _stationName, exitCode, ran.TotalSeconds, item.Path);
                    if (consecutiveFailures >= 5)
                    {
                        _logger.LogError("TvStation [{Station}]: 5 consecutive encoder failures; showing a slate for 60s", _stationName);
                        await RunSlateAsync(TimeSpan.FromSeconds(60), "Technical difficulties", ct).ConfigureAwait(false);
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, consecutiveFailures * 2)), ct).ConfigureAwait(false);
                    }
                }
                else if (endedEarly)
                {
                    consecutiveFailures = 0;
                    lastFailedProgramId = entry.ProgramId;
                    _logger.LogInformation("TvStation [{Station}]: {Title} ended early (exit {Code}); filling until scheduled end", _stationName, item.DisplayTitle, exitCode);
                }
                else
                {
                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TvStation [{Station}]: restreamer loop error", _stationName);
                Note("Loop error: " + ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunSlateAsync(TimeSpan duration, string text, CancellationToken ct, DateTime? hardEnd = null)
    {
        if (duration < MinSegment)
        {
            await Task.Delay(duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            return;
        }

        var now = DateTime.UtcNow;
        var showText = _settings.ShowTextOnSlate && _slateTextSupported;
        var logo = _settings.ShowLogoOnSlate ? _logoPath() : null;
        var args = FfmpegArgsBuilder.ForSlate(duration, NextTsOffset(), _profile, _settings.SlateBackgroundColor, showText ? text : null, logo);
        _logger.LogInformation("TvStation [{Station}]: slate for {Duration}", _stationName, duration.ToString(@"hh\:mm\:ss"));

        var started = DateTime.UtcNow;
        var code = await RunProcessAsync(args, (hardEnd ?? now + duration) + HardKillGrace, ct).ConfigureAwait(false);
        if (code != 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(3) && !ct.IsCancellationRequested)
        {
            if (showText || logo is not null)
            {
                // drawtext/overlay may be unavailable in this ffmpeg build; fall back to a plain card.
                _slateTextSupported = false;
                _logger.LogWarning("TvStation [{Station}]: slate with text/logo failed (exit {Code}); falling back to plain slate", _stationName, code);
                var plain = FfmpegArgsBuilder.ForSlate(duration - (DateTime.UtcNow - started), NextTsOffset(), _profile, _settings.SlateBackgroundColor, null, null);
                code = await RunProcessAsync(plain, (hardEnd ?? now + duration) + HardKillGrace, ct).ConfigureAwait(false);
            }

            if (code != 0 && !ct.IsCancellationRequested)
            {
                _logger.LogError("TvStation [{Station}]: slate encoder failed (exit {Code}); pausing 5s", _stationName, code);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs one ffmpeg process to completion (or hard kill), pumping stdout into the buffer.</summary>
    private async Task<int> RunProcessAsync(List<string> args, DateTime hardKillAtUtc, CancellationToken ct)
    {
        ProcessRunner proc;
        try
        {
            proc = ProcessRunner.Start(_ffmpegPath, args, OnStderr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TvStation [{Station}]: cannot start ffmpeg at {Path}", _stationName, _ffmpegPath);
            Note("Cannot start ffmpeg: " + ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return -1;
        }

        _current = proc;
        using (proc)
        {
            var pacer = new TsPacer();
            var pump = PumpAsync(proc.Stdout, pacer, ct);

            // The deadline is evaluated against the wall clock in short steps rather than one long timer,
            // so a host whose timers drift cannot cut a program short.
            var exited = false;
            while (!exited && !ct.IsCancellationRequested)
            {
                var remainingToKill = hardKillAtUtc - DateTime.UtcNow;
                if (remainingToKill <= TimeSpan.Zero)
                {
                    _logger.LogInformation("TvStation [{Station}]: segment overran its slot; stopping encoder", _stationName);
                    proc.Kill();
                    break;
                }

                var step = remainingToKill > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remainingToKill;
                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepCts.CancelAfter(step);
                try
                {
                    await proc.WaitForExitAsync(stepCts.Token).ConfigureAwait(false);
                    exited = true;
                }
                catch (OperationCanceledException)
                {
                    // Step elapsed (or shutdown); re-evaluate against the wall clock.
                }
            }

            if (ct.IsCancellationRequested)
            {
                proc.Kill();
            }

            // ffmpeg finishes encoding ahead of real time; the pump keeps releasing the buffered tail at the
            // paced rate until the pipe is drained. Allow it to run up to the segment's hard deadline.
            var drain = hardKillAtUtc + TimeSpan.FromSeconds(3) - DateTime.UtcNow;
            if (drain < TimeSpan.FromSeconds(1))
            {
                drain = TimeSpan.FromSeconds(1);
            }

            try
            {
                await pump.WaitAsync(drain, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Deadline passed; disposing the process closes the pipe and ends the pump.
            }

            if (pacer.LastPcrSeconds is { } last)
            {
                _tsOffsetFloorSeconds = last + 0.1;
            }

            _current = null;
            return ct.IsCancellationRequested ? 0 : proc.ExitCode;
        }
    }

    /// <summary>Timestamp offset for the next segment: wall-clock based, but never behind what was already sent.</summary>
    private TimeSpan NextTsOffset()
    {
        var wall = (DateTime.UtcNow - StartedUtc).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Max(wall, _tsOffsetFloorSeconds));
    }

    private async Task PumpAsync(Stream stdout, TsPacer pacer, CancellationToken ct)
    {
        var buf = new byte[32 * 1024];
        var aligner = new TsAligner();
        try
        {
            int n;
            while ((n = await stdout.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
            {
                var chunk = aligner.Push(buf.AsSpan(0, n));
                if (chunk.IsEmpty)
                {
                    continue;
                }

                await pacer.PaceAsync(chunk, ct).ConfigureAwait(false);
                _buffer.Write(chunk.Span);
                _firstBytes.TrySetResult();
            }
        }
        catch (Exception)
        {
            // Pipe closed or cancelled.
        }
    }

    private void OnStderr(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Note(line);
        _logger.LogDebug("TvStation ffmpeg [{Station}]: {Line}", _stationName, line);
    }

    private void Note(string line)
    {
        _recentErrors.Enqueue(DateTime.UtcNow.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " " + line);
        while (_recentErrors.Count > MaxLogLines && _recentErrors.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _current?.Kill();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Ignore.
        }

        _firstBytes.TrySetCanceled();
        _cts.Dispose();
    }
}
