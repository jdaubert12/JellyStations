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
    private readonly LogoOverlayConfig _overlay;
    private readonly UpNextCardConfig _upNext;
    private readonly string _cardsDir;
    private readonly TimeZoneInfo _zone;
    private readonly string _cardText;
    private readonly BroadcastBuffer _buffer;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _firstBytes = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _recentErrors = new();
    private readonly Action<string>? _onFault;
    private readonly HardwareDecodePolicy _policy;
    private readonly HashSet<string> _softwarePrograms = new(StringComparer.Ordinal);
    private string? _lastStderr;
    private ProcessRunner? _current;
    private TsPacer? _pacer;
    private SegmentTelemetry? _telemetry;
    private long _lastOutputTicks;
    private int _cpuPid;
    private TimeSpan? _cpuTime;
    private DateTime _cpuSampledUtc;
    private double? _cpuCores;
    private Task? _loop;
    private bool _slateTextSupported = true;
    private bool _overlaySupported = true;
    private bool _upNextSupported = true;
    private bool _hardwareDecodeSupported = true;
    private bool _tonemapSupported = true;
    private double _tsOffsetFloorSeconds;

    public FfmpegRestreamer(
        Guid stationId,
        string stationName,
        string ffmpegPath,
        EncodingProfile profile,
        GlobalSettings settings,
        IScheduleResolver resolver,
        Func<string?> logoPath,
        LogoOverlayConfig overlay,
        string cardText,
        BroadcastBuffer buffer,
        ILogger logger,
        Action<string>? onFault = null,
        HardwareDecodePolicy? policy = null,
        UpNextCardConfig? upNext = null,
        string? cardsDir = null,
        string? timeZoneId = null)
    {
        _upNext = upNext ?? new UpNextCardConfig();
        _cardsDir = cardsDir ?? Path.Combine(Path.GetTempPath(), "TvStation", "cards");
        _zone = UpNextCard.Zone(timeZoneId);
        _onFault = onFault;
        _policy = policy ?? (string.IsNullOrEmpty(profile.HardwareDecode) ? HardwareDecodePolicy.Software : HardwareDecodePolicy.Permissive(profile.HardwareDecode));
        _stationId = stationId;
        _stationName = stationName;
        _ffmpegPath = ffmpegPath;
        _profile = profile;
        _settings = settings;
        _resolver = resolver;
        _logoPath = logoPath;
        _overlay = overlay;
        _cardText = string.IsNullOrWhiteSpace(cardText) ? Station.DefaultCardText : cardText;
        _buffer = buffer;
        _logger = logger;
    }

    public DateTime StartedUtc { get; private set; }

    public ScheduleEntry? CurrentEntry { get; private set; }

    public int? CurrentPid => _current?.HasExited == false ? _current.Pid : null;

    public Task FirstBytes => _firstBytes.Task;

    public IReadOnlyList<string> RecentErrors => _recentErrors.ToArray();

    /// <summary>What the encoder is doing right now, for the dashboard. Sampling CPU needs two calls a little apart.</summary>
    public EncoderSnapshot Snapshot()
    {
        var proc = _current;
        var running = proc is { HasExited: false };
        var telemetry = _telemetry;
        var pacer = _pacer;
        var lastOutput = Volatile.Read(ref _lastOutputTicks);
        var requested = _profile.HardwareDecode ?? string.Empty;
        return new EncoderSnapshot
        {
            ProcessRunning = running,
            Title = telemetry?.Title,
            IsSlate = telemetry?.IsSlate ?? false,
            SegmentStartedUtc = telemetry?.StartedUtc,
            RequestedDecoder = requested,
            ActiveDecoder = telemetry?.HardwareDecode ?? (_hardwareDecodeSupported ? requested : string.Empty),
            DecoderDisabled = !_hardwareDecodeSupported && requested.Length > 0,
            DecoderFallback = telemetry?.DecoderFallback,
            DecoderSkipReason = telemetry?.DecoderSkipReason,
            DecoderSkipAfterFailure = telemetry?.DecoderSkipAfterFailure ?? false,
            GpuDevice = running ? ProcessInspector.OpenGpuDevice(proc!.Pid) : null,
            CanInspectDevices = ProcessInspector.CanInspect,
            VideoEncoder = _profile.VideoEncoder,
            ToneMapping = telemetry?.ToneMapping ?? false,
            SourceIsHdr = telemetry?.SourceIsHdr ?? false,
            Overlay = telemetry?.Overlay ?? false,
            CpuCores = SampleCpu(running ? proc : null),
            AheadSeconds = pacer?.Margin?.TotalSeconds,
            MinAheadSeconds = pacer?.MinMargin?.TotalSeconds,
            SecondsSinceOutput = running && lastOutput > 0 ? (DateTime.UtcNow - new DateTime(lastOutput, DateTimeKind.Utc)).TotalSeconds : null,
            Warnings = telemetry?.Warnings ?? 0,
            LastWarning = telemetry?.LastWarning
        };
    }

    /// <summary>CPU cores the current ffmpeg has used since the previous sample of the same process; the first sample has no answer.</summary>
    private double? SampleCpu(ProcessRunner? proc)
    {
        if (proc is null)
        {
            _cpuPid = 0;
            _cpuTime = null;
            _cpuCores = null;
            return null;
        }

        var now = DateTime.UtcNow;
        var time = proc.ProcessorTime;
        var pid = proc.Pid;
        if (time.HasValue && _cpuTime.HasValue && _cpuPid == pid)
        {
            var wall = (now - _cpuSampledUtc).TotalSeconds;
            if (wall < 0.5)
            {
                return _cpuCores;
            }

            _cpuCores = Math.Max(0, (time.Value - _cpuTime.Value).TotalSeconds / wall);
        }
        else
        {
            _cpuCores = null;
        }

        _cpuPid = pid;
        _cpuTime = time;
        _cpuSampledUtc = now;
        return _cpuCores;
    }

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

                // A multi-part item (Blu-ray split into part1/part2) plays as consecutive segments on one clock:
                // the part the offset falls in now, then the loop comes back here for the next part.
                var segment = entry.Item?.SegmentAt(resolved.Offset);
                string? openProblem = null;
                var playable = entry.Kind != EntryKind.Slate
                    && entry.Item is not null
                    && segment is not null
                    && entry.ProgramId != lastFailedProgramId
                    && (openProblem = FileProbe.Describe(segment.Part.Path)) is null;

                if (!playable)
                {
                    if (openProblem is not null && entry.Item is not null)
                    {
                        _logger.LogWarning("TvStation [{Station}]: cannot open {Title}: {Reason}", _stationName, entry.Item.DisplayTitle, openProblem);
                        Fault(entry.Item.DisplayTitle + ": " + openProblem);
                    }

                    var text = entry.Kind == EntryKind.Slate ? _cardText : Station.DefaultCardText;
                    await RunSlateAsync(remaining, text, ct, entry.EndUtc).ConfigureAwait(false);
                    continue;
                }

                var item = entry.Item!;
                var part = segment!;
                var seek = part.Seek;
                // Cut the file when the schedule ends before it does; otherwise play the file (this part) to its end.
                var cut = remaining < part.Remaining;
                var duration = cut ? remaining : (TimeSpan?)null;
                var segmentEnd = now + (cut ? remaining : part.Remaining);
                var overlay = OverlayInput();
                var profile = EffectiveProfile();
                var upNext = await UpNextInputAsync(entry, cut ? remaining : part.Remaining, part.IsLast || cut, profile, ct).ConfigureAwait(false);
                string? softwareReason = null;
                var softwareAfterFailure = false;
                if (profile.HardwareDecode.Length > 0)
                {
                    if (_softwarePrograms.Contains(entry.ProgramId))
                    {
                        softwareReason = profile.HardwareDecode + " failed on this program";
                        softwareAfterFailure = true;
                    }
                    else
                    {
                        softwareReason = _policy.WhyNot(part.Part);
                    }

                    if (softwareReason is not null)
                    {
                        profile = profile.Clone();
                        profile.HardwareDecode = string.Empty;
                    }
                }

                var hardwareDecode = profile.HardwareDecode.Length > 0;
                var tonemap = part.Part.IsHdr && _tonemapSupported;
                var args = FfmpegArgsBuilder.ForSource(part.Part.Path, part.Part.HasAudio, part.Part.AudioStreamIndex, tonemap ? part.Part.HdrTransfer : null, seek, duration, NextTsOffset(), profile, overlay, part.Part.VideoCodec, upNext);

                _logger.LogInformation(
                    "TvStation [{Station}]: playing {Title}{Part} from {Seek} ({Remaining} remaining){Hdr}{Hw}{Card}",
                    _stationName,
                    item.DisplayTitle,
                    item.IsMultiPart ? " part " + (part.Index + 1) + "/" + item.Parts.Count : string.Empty,
                    seek.ToString(@"hh\:mm\:ss"),
                    remaining.ToString(@"hh\:mm\:ss"),
                    tonemap ? ", tone mapping HDR to SDR" : string.Empty,
                    hardwareDecode ? ", " + profile.HardwareDecode + " decoding" : softwareReason is null ? string.Empty : ", software decoding (" + softwareReason + ")",
                    upNext is null ? string.Empty : ", up next card at " + string.Join(", ", upNext.Windows.Select(w => w.From.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s")));
                _logger.LogDebug("TvStation ffmpeg {Args}", FfmpegArgsBuilder.ToLogString(args));
                _telemetry = new SegmentTelemetry(item.DisplayTitle, isSlate: false, profile.HardwareDecode, profile.VideoEncoder, tonemap, part.Part.IsHdr, overlay is not null)
                {
                    DecoderSkipReason = softwareReason,
                    DecoderSkipAfterFailure = softwareAfterFailure
                };

                var started = DateTime.UtcNow;
                var exitCode = await RunProcessAsync(args, entry.EndUtc + HardKillGrace, ct).ConfigureAwait(false);
                var ran = DateTime.UtcNow - started;

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // Early means before this segment's planned end: a part finishing on time is not a failure.
                var endedEarly = DateTime.UtcNow < segmentEnd - MinSegment;
                if (exitCode != 0 && ran < TimeSpan.FromSeconds(5) && hardwareDecode)
                {
                    // The GPU decoders do not fall back to software by themselves. A device that cannot be opened
                    // (missing driver, wrong node) fails every file, so that disables hardware decoding for the
                    // session; anything else is taken as this file being beyond the decoder and only this program
                    // is replayed in software.
                    var detail = _telemetry?.FirstError ?? _lastStderr;
                    var deviceProblem = _telemetry?.DecoderFallback is not null || _softwarePrograms.Count > 0;
                    if (deviceProblem)
                    {
                        _hardwareDecodeSupported = false;
                        _logger.LogWarning("TvStation [{Station}]: ffmpeg exited with {Code} with {Kind} decoding; continuing with software decoding", _stationName, exitCode, profile.HardwareDecode);
                        Fault(profile.HardwareDecode + " hardware decoding failed on " + item.DisplayTitle + " (ffmpeg exit " + exitCode + "); software decoding until the stream restarts" + (detail is null ? string.Empty : ": " + detail));
                    }
                    else
                    {
                        _softwarePrograms.Add(entry.ProgramId);
                        _logger.LogWarning("TvStation [{Station}]: ffmpeg exited with {Code} with {Kind} decoding on {Title}; replaying it with software decoding", _stationName, exitCode, profile.HardwareDecode, item.DisplayTitle);
                        Fault(profile.HardwareDecode + " could not decode " + item.DisplayTitle + " (ffmpeg exit " + exitCode + "); this program plays with software decoding" + (detail is null ? string.Empty : ": " + detail));
                    }

                    continue;
                }

                if (exitCode != 0 && ran < TimeSpan.FromSeconds(5) && tonemap)
                {
                    // zscale/tonemap missing from this ffmpeg build: play the HDR file untouched (tagged SDR) rather than show a card.
                    _tonemapSupported = false;
                    _logger.LogWarning("TvStation [{Station}]: ffmpeg exited with {Code} while tone mapping; continuing without tone mapping", _stationName, exitCode);
                    Fault("HDR tone mapping failed (ffmpeg exit " + exitCode + "); HDR programs play without it until the stream restarts" + (_lastStderr is null ? string.Empty : ": " + _lastStderr));
                    continue;
                }

                if (exitCode != 0 && ran < TimeSpan.FromSeconds(5) && upNext is not null)
                {
                    // The card overlay is the newest thing in the graph: drop it for this session and replay.
                    _upNextSupported = false;
                    _logger.LogWarning("TvStation [{Station}]: ffmpeg exited with {Code} with the up next card; continuing without it", _stationName, exitCode);
                    Fault("Up next card failed (ffmpeg exit " + exitCode + "); disabled until the stream restarts" + (_lastStderr is null ? string.Empty : ": " + _lastStderr));
                    continue;
                }

                if (exitCode != 0 && ran < TimeSpan.FromSeconds(5) && overlay is not null)
                {
                    // The overlay filter (or the logo file) is the likely culprit: drop the overlay for this session and replay.
                    _overlaySupported = false;
                    _logger.LogWarning("TvStation [{Station}]: ffmpeg exited with {Code} with the logo overlay; continuing without it", _stationName, exitCode);
                    Fault("Logo overlay failed (ffmpeg exit " + exitCode + "); disabled until the stream restarts");
                    continue;
                }

                if (exitCode != 0 && ran < TimeSpan.FromSeconds(5))
                {
                    consecutiveFailures++;
                    lastFailedProgramId = entry.ProgramId;
                    _logger.LogWarning("TvStation [{Station}]: ffmpeg exited with {Code} after {Seconds:0.0}s for {Path}", _stationName, exitCode, ran.TotalSeconds, item.Path);
                    Fault(item.DisplayTitle + ": ffmpeg exited with code " + exitCode + " after " + ran.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                        + "s (" + part.Part.Path + ")" + (_lastStderr is null ? string.Empty : ": " + _lastStderr));
                    if (consecutiveFailures >= 5)
                    {
                        _logger.LogError("TvStation [{Station}]: 5 consecutive encoder failures; showing a slate for 60s", _stationName);
                        Fault("5 consecutive encoder failures; showing a slate for 60s");
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

                    // The file ran out a hair before the clock (metadata runtime vs. real length): wait it out rather
                    // than re-spawning ffmpeg for a fraction of a second until the offset moves on.
                    var gap = segmentEnd - DateTime.UtcNow;
                    if (gap > TimeSpan.FromMilliseconds(200))
                    {
                        await Task.Delay(gap, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TvStation [{Station}]: restreamer loop error", _stationName);
                Fault("Restreamer error: " + ex.Message);
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

    /// <summary>The station's profile, minus hardware decoding once it has failed in this session.</summary>
    private EncodingProfile EffectiveProfile()
    {
        if (_hardwareDecodeSupported || _profile.HardwareDecode.Length == 0)
        {
            return _profile;
        }

        var software = _profile.Clone();
        software.HardwareDecode = string.Empty;
        return software;
    }

    /// <summary>The logo bug to draw over programs, or null when off, unsupported, or the logo file is missing.</summary>
    private FfmpegArgsBuilder.LogoOverlayInput? OverlayInput()
    {
        if (!_overlay.Enabled || !_overlaySupported)
        {
            return null;
        }

        var path = _logoPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        return new FfmpegArgsBuilder.LogoOverlayInput(path, _overlay.Corner, _overlay.Opacity, _overlay.HeightPercent);
    }

    /// <summary>
    /// The "Up next" card for the segment about to play, rendered to a PNG, or null when the card is off, the
    /// segment does not end the program, the program is too short, nothing playable follows, or rendering failed.
    /// </summary>
    private async Task<FfmpegArgsBuilder.UpNextCardInput?> UpNextInputAsync(ScheduleEntry entry, TimeSpan segmentLength, bool endsProgram, EncodingProfile profile, CancellationToken ct)
    {
        if (!_upNext.Enabled || !_upNextSupported || !endsProgram || entry.Kind != EntryKind.Program)
        {
            return null;
        }

        var windows = UpNextCard.Windows(segmentLength, _upNext);
        if (windows.Count == 0)
        {
            return null;
        }

        var next = UpNextCard.NextProgram(_resolver.Enumerate(_stationId, entry.EndUtc, entry.EndUtc + UpNextCard.Lookahead), entry.EndUtc);
        if (next is null)
        {
            return null;
        }

        var spec = UpNextCard.Spec(next, _upNext, _zone, profile);
        var prefix = _stationId.ToString("N") + "-";
        var path = Path.Combine(_cardsDir, prefix + new DateTimeOffset(entry.StartUtc, TimeSpan.Zero).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
        try
        {
            Directory.CreateDirectory(_cardsDir);
            // The previous program's ffmpeg has exited, so its card is no longer being read.
            foreach (var old in Directory.EnumerateFiles(_cardsDir, prefix + "*.png"))
            {
                if (!string.Equals(old, path, StringComparison.Ordinal))
                {
                    File.Delete(old);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TvStation [{Station}]: could not tidy the cards folder", _stationName);
        }

        if (await RenderCardAsync(spec, path, ct).ConfigureAwait(false))
        {
            return new FfmpegArgsBuilder.UpNextCardInput(path, _upNext.Corner, windows) { Animation = _upNext.Animation };
        }

        if (spec.ThumbnailPath is not null && await RenderCardAsync(spec with { ThumbnailPath = null }, path, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("TvStation [{Station}]: the artwork for {Title} could not be drawn on the up next card; card drawn without it", _stationName, next.Item!.DisplayTitle);
            return new FfmpegArgsBuilder.UpNextCardInput(path, _upNext.Corner, windows) { Animation = _upNext.Animation };
        }

        _upNextSupported = false;
        _logger.LogWarning("TvStation [{Station}]: ffmpeg could not render the up next card; disabled until the stream restarts: {Error}", _stationName, _lastCardError);
        Fault("Up next card could not be rendered; disabled until the stream restarts" + (_lastCardError is null ? string.Empty : ": " + _lastCardError));
        return null;
    }

    private string? _lastCardError;

    /// <summary>Runs ffmpeg to write the card image; true when it exits cleanly and the file is there.</summary>
    private async Task<bool> RenderCardAsync(UpNextCardSpec spec, string path, CancellationToken ct)
    {
        var args = FfmpegArgsBuilder.ForUpNextCard(spec, path);
        _logger.LogDebug("TvStation ffmpeg {Args}", FfmpegArgsBuilder.ToLogString(args));
        try
        {
            File.Delete(path);
            using var proc = ProcessRunner.Start(_ffmpegPath, args, line => _lastCardError = line.Trim());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                proc.Kill();
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                _lastCardError = "ffmpeg did not finish within 20 s";
                return false;
            }

            return proc.ExitCode == 0 && File.Exists(path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastCardError = ex.Message;
            return false;
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
        _telemetry = new SegmentTelemetry(text, isSlate: true, string.Empty, _profile.VideoEncoder, toneMapping: false, sourceIsHdr: false, overlay: logo is not null);

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
                Fault("Slate encoder failed (ffmpeg exit " + code + ")" + (_lastStderr is null ? string.Empty : ": " + _lastStderr));
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
            Fault("Cannot start ffmpeg at " + _ffmpegPath + ": " + ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return -1;
        }

        _current = proc;
        using (proc)
        {
            var pacer = new TsPacer();
            _pacer = pacer;
            Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
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
                Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
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

        _lastStderr = line.Trim();
        _telemetry?.Observe(line);
        Note(line);
        _logger.LogDebug("TvStation ffmpeg [{Station}]: {Line}", _stationName, line);
    }

    /// <summary>Records a fault in the recent log and reports it to the session manager, which keeps the last one per station.</summary>
    private void Fault(string message)
    {
        Note(message);
        _onFault?.Invoke(message);
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
