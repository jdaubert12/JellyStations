using System.Net.Mime;
using System.Reflection;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Streaming;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyStations.Api;

[ApiController]
[Route("JellyStations/Settings")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsRepository _settings;
    private readonly StreamSessionManager _sessions;
    private readonly StationService _stations;

    public SettingsController(SettingsRepository settings, StreamSessionManager sessions, StationService stations)
    {
        _settings = settings;
        _sessions = sessions;
        _stations = stations;
    }

    [HttpGet]
    public ActionResult<GlobalSettings> Get() => Ok(_settings.Get());

    [HttpPut]
    public ActionResult<GlobalSettings> Put([FromBody] GlobalSettings settings)
    {
        settings.DefaultEncoding ??= new EncodingProfile();
        settings.LogoOverlay ??= new LogoOverlayConfig();
        settings.LogoOverlay.Normalize();
        settings.UpNextCard ??= new UpNextCardConfig();
        settings.UpNextCard.Normalize();
        settings.Suggestions ??= new SuggestionThresholds();
        settings.Suggestions.Normalize();
        var errors = StationService.ValidateEncoding(settings.DefaultEncoding);
        if (settings.LingerSeconds < 0 || settings.LingerSeconds > 600)
        {
            errors.Add("Linger must be between 0 and 600 seconds.");
        }

        if (settings.IdleStopSeconds != 0 && (settings.IdleStopSeconds < 30 || settings.IdleStopSeconds > 3600))
        {
            errors.Add("Stop an unwatched stream after must be 0 (off) or between 30 and 3600 seconds.");
        }

        if (settings.MaxGuideDays < 1 || settings.MaxGuideDays > 30)
        {
            errors.Add("Max guide days must be between 1 and 30.");
        }

        if (settings.MaxConcurrentSessions < 1 || settings.MaxConcurrentSessions > 64)
        {
            errors.Add("Max concurrent sessions must be between 1 and 64.");
        }

        if (!string.IsNullOrWhiteSpace(settings.FfmpegPathOverride) && !System.IO.File.Exists(settings.FfmpegPathOverride))
        {
            errors.Add("ffmpeg override path does not exist on the server.");
        }

        if (errors.Count > 0)
        {
            return BadRequest(new ValidationErrorDto { Errors = errors });
        }

        settings.ExcludedTags = StationService.NormalizeTags(settings.ExcludedTags);
        var previous = _settings.Get();
        // Templates have their own endpoints; the settings page never edits them.
        settings.BlockTemplates = previous.BlockTemplates ?? new List<SlotBlock>();
        settings.StationTemplates = previous.StationTemplates ?? new List<StationTemplate>();
        settings.WeekLayouts = previous.WeekLayouts ?? new List<WeekLayout>();
        settings.HouseholdUserIds = (settings.HouseholdUserIds ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();
        _settings.Save(settings);
        if (!JsonFileStore.SameJson(previous.DefaultEncoding, settings.DefaultEncoding))
        {
            _sessions.StopAll();
        }
        else
        {
            // A changed default bug or card restarts only the streams that follow it; stations with their own keep going.
            var logoChanged = !JsonFileStore.SameJson(previous.LogoOverlay, settings.LogoOverlay);
            var cardChanged = !JsonFileStore.SameJson(previous.UpNextCard, settings.UpNextCard);
            foreach (var station in _stations.GetAll())
            {
                if ((logoChanged && station.LogoOverlay is null) || (cardChanged && station.UpNextCard is null))
                {
                    _sessions.StopStation(station.Id);
                }
            }
        }

        var householdChanged = !(previous.HouseholdUserIds ?? new List<Guid>()).OrderBy(u => u).SequenceEqual(settings.HouseholdUserIds.OrderBy(u => u));
        if (householdChanged || !previous.ExcludedTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).SequenceEqual(settings.ExcludedTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            _stations.RecompileAll();
        }

        return Ok(settings);
    }

    /// <summary>
    /// Drops every station's own logo bug (kind = LogoOverlay) or "Up next" card (kind = UpNextCard) so all
    /// stations follow the plugin-wide default. Returns how many stations changed.
    /// </summary>
    [HttpPost("ClearOverrides/{kind}")]
    public ActionResult<ClearOverridesResultDto> ClearOverrides(OverlayKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            return BadRequest(new ValidationErrorDto { Errors = new List<string> { "Unknown overlay kind." } });
        }

        return Ok(new ClearOverridesResultDto { Changed = _stations.ClearOverlayOverrides(kind) });
    }
}

[ApiController]
[Route("JellyStations/Guide")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class GuideController : ControllerBase
{
    private readonly GuideRefreshService _guide;

    public GuideController(GuideRefreshService guide)
    {
        _guide = guide;
    }

    [HttpPost("Refresh")]
    public IActionResult Refresh() => _guide.TriggerNow() ? Accepted() : StatusCode(503, ValidationErrorDto.Of("The Live TV guide refresh task is not available."));
}

[ApiController]
[Route("JellyStations")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class StatusController : ControllerBase
{
    private readonly StreamSessionManager _sessions;
    private readonly JellyStationsPaths _paths;
    private readonly StationService _stations;

    public StatusController(StreamSessionManager sessions, JellyStationsPaths paths, StationService stations)
    {
        _sessions = sessions;
        _paths = paths;
        _stations = stations;
    }

    /// <summary>Per-station health from the last compile: empty sources, under a day of unique content, items skipped.</summary>
    [HttpGet("Health")]
    public ActionResult<HealthReportDto> Health()
    {
        var all = _stations.GetAll();
        var stations = all
            .Select(s => StationHealth.Build(s, _stations.GetCompiled(s.Id), _stations.WeatherChannelFor(s), profile: s.Encoding))
            .ToList();
        // A weak default is one thing to fix in Settings, not a warning on every station that follows it.
        var onDefault = all.Count(s => s.Encoding is null);
        return Ok(new HealthReportDto
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            Stations = stations,
            StationsWithWarnings = stations.Count(s => !s.Healthy),
            DefaultEncodingWarnings = onDefault > 0 ? _stations.DefaultProfile().Concerns() : new List<string>(),
            StationsOnDefaultEncoding = onDefault
        });
    }

    [HttpGet("Status")]
    public ActionResult<StatusDto> Status()
    {
        var ffmpeg = _sessions.FfmpegPath;
        return Ok(new StatusDto
        {
            FfmpegPath = ffmpeg,
            FfmpegExists = System.IO.File.Exists(ffmpeg),
            DataPath = _paths.Root,
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?",
            ServerTimeUtc = DateTime.UtcNow.ToString("O"),
            Sessions = _sessions.Sessions.Where(s => !s.IsDisposed).Select(s => new SessionStatusDto
            {
                StationId = s.StationId,
                StationName = s.StationName,
                UniqueId = s.UniqueId,
                Consumers = s.ConsumerCount,
                Readers = s.ReaderCount,
                StartedUtc = s.CreatedUtc,
                BytesStreamed = s.BytesStreamed,
                EncoderPid = s.EncoderPid,
                Current = s.CurrentEntry is null ? null : Map.Entry(s.CurrentEntry, DateTime.UtcNow - s.CurrentEntry.StartUtc),
                RecentLog = s.RecentErrors.ToList(),
                Encoder = EncoderStatus(s.Encoder)
            }).ToList()
        });
    }

    internal static EncoderStatusDto? EncoderStatus(EncoderSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var verdict = StreamHealth.Assess(snapshot, DateTime.UtcNow);
        return new EncoderStatusDto
        {
            ProcessRunning = snapshot.ProcessRunning,
            IsSlate = snapshot.IsSlate,
            SegmentStartedUtc = snapshot.SegmentStartedUtc,
            RequestedDecoder = snapshot.RequestedDecoder,
            Decoder = verdict.Decoder,
            DecoderDetail = snapshot.DecoderFallback ?? snapshot.DecoderSkipReason,
            GpuDevice = snapshot.GpuDevice,
            VideoEncoder = snapshot.VideoEncoder,
            EncoderIsHardware = EncoderSnapshot.IsHardwareEncoder(snapshot.VideoEncoder),
            HdrOutput = snapshot.HdrOutput,
            ToneMapping = snapshot.ToneMapping,
            SourceIsHdr = snapshot.SourceIsHdr,
            Overlay = snapshot.Overlay,
            CpuCores = snapshot.CpuCores,
            AheadSeconds = snapshot.AheadSeconds,
            MinAheadSeconds = snapshot.MinAheadSeconds,
            SecondsSinceOutput = snapshot.SecondsSinceOutput,
            Warnings = snapshot.Warnings,
            LastWarning = snapshot.LastWarning,
            State = verdict.State,
            Problems = verdict.Problems.ToList()
        };
    }

    [HttpGet("TimeZones")]
    public ActionResult<IEnumerable<string>> TimeZones()
    {
        return Ok(TimeZoneInfo.GetSystemTimeZones().Select(t => t.Id).OrderBy(t => t, StringComparer.Ordinal).ToList());
    }
}

/// <summary>The files that gave trouble on air or in a folder scan, for the Status page.</summary>
[ApiController]
[Route("JellyStations/ProblemFiles")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class ProblemFilesController : ControllerBase
{
    private readonly ProblemFileLog _problems;

    public ProblemFilesController(ProblemFileLog problems)
    {
        _problems = problems;
    }

    /// <summary>
    /// The list, latest trouble first. With <paramref name="recheck"/> the files are looked at first and the ones that
    /// were removed, replaced, or can be opened again are dropped; that touches the disk, so the page asks for it
    /// when it opens and not on its timer.
    /// </summary>
    [HttpGet]
    public ActionResult<ProblemFilesDto> Get([FromQuery] bool recheck = false)
    {
        var settled = recheck ? _problems.Recheck() : 0;
        return Ok(new ProblemFilesDto
        {
            Settled = settled,
            Files = _problems.List().Select(f => new ProblemFileDto
            {
                Id = ProblemFileLog.IdFor(f.Path),
                Path = f.Path,
                Title = f.Title,
                Kind = f.Kind,
                Problem = ProblemFileLog.Describe(f.Kind),
                Detail = f.Detail,
                StationId = f.StationId,
                StationName = f.StationName,
                FirstUtc = DateTime.SpecifyKind(f.FirstUtc, DateTimeKind.Utc),
                LastUtc = DateTime.SpecifyKind(f.LastUtc, DateTimeKind.Utc),
                Count = f.Count
            }).ToList()
        });
    }

    /// <summary>Takes one file off the list. It comes back if it gives trouble again.</summary>
    [HttpDelete("{id}")]
    public IActionResult Dismiss([FromRoute] string id) => _problems.Dismiss(id) ? NoContent() : NotFound();

    [HttpDelete]
    public IActionResult Clear()
    {
        _problems.Clear();
        return NoContent();
    }
}

/// <summary>The background look at each file's picture for interlacing its flags do not show: how far it has got and what it found.</summary>
[ApiController]
[Route("JellyStations/PictureCheck")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class PictureCheckController : ControllerBase
{
    private readonly InterlaceCheckService _check;

    public PictureCheckController(InterlaceCheckService check)
    {
        _check = check;
    }

    /// <summary>Counts from memory and the files found; nothing on disk is touched.</summary>
    [HttpGet]
    public ActionResult<InterlaceCheckService.Summary> Get() => Ok(_check.Summarize());
}

[ApiController]
[Route("JellyStations/Hardware")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class HardwareController : ControllerBase
{
    private readonly StreamSessionManager _sessions;
    private readonly StationService _stations;
    private readonly SettingsRepository _settings;
    private readonly IScheduleResolver _resolver;
    private readonly InterlaceCheckService _interlace;

    public HardwareController(StreamSessionManager sessions, StationService stations, SettingsRepository settings, IScheduleResolver resolver, InterlaceCheckService interlace)
    {
        _interlace = interlace;
        _sessions = sessions;
        _stations = stations;
        _settings = settings;
        _resolver = resolver;
    }

    /// <summary>Jellyfin's hardware setting, what the default profile resolves to, the host's GPU nodes and what this ffmpeg build offers.</summary>
    [HttpGet]
    public async Task<ActionResult<HardwareInfoDto>> Get([FromQuery] bool refresh, CancellationToken ct)
    {
        var jellyfin = _sessions.ReadJellyfinHardware();
        var configured = _settings.Get().DefaultEncoding;
        var profile = _sessions.ResolveHardware(configured);
        var ffmpegPath = _sessions.FfmpegPath;
        var caps = System.IO.File.Exists(ffmpegPath)
            ? await HardwareProbe.CapabilitiesAsync(ffmpegPath, refresh, ct).ConfigureAwait(false)
            : new FfmpegCapabilities { Error = "ffmpeg not found at " + ffmpegPath };

        var dto = new HardwareInfoDto
        {
            JellyfinAcceleration = jellyfin.Type.ToString(),
            JellyfinQsvDevice = jellyfin.QsvDevice,
            JellyfinVaapiDevice = jellyfin.VaapiDevice,
            DefaultDecoder = profile.HardwareDecode,
            DefaultDevice = profile.HardwareDevice,
            DefaultEncoder = profile.VideoEncoder,
            Os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other",
            CpuCores = Environment.ProcessorCount,
            CanInspectProcesses = ProcessInspector.CanInspect,
            DrmNodes = ProcessInspector.DrmNodes().ToList(),
            FfmpegPath = ffmpegPath,
            FfmpegVersion = caps.Version,
            Hwaccels = caps.Hwaccels.ToList(),
            H264Encoders = caps.H264Encoders.ToList(),
            HevcEncoders = caps.HevcEncoders.ToList(),
            HasTonemap = caps.HasZscale && caps.HasTonemap,
            FfmpegError = caps.Error
        };
        dto.Notes = Notes(dto, configured.HardwareDecode);
        return Ok(dto);
    }

    /// <summary>Plain-language consequences of the picture above, so the dashboard can say what to fix.</summary>
    internal static List<string> Notes(HardwareInfoDto d, string configuredDecode)
    {
        var notes = new List<string>();
        var follow = string.Equals(configuredDecode, EncodingProfile.HardwareDecodeAuto, StringComparison.OrdinalIgnoreCase);
        if (d.DefaultDecoder.Length == 0)
        {
            notes.Add(follow
                ? "Jellyfin's transcoding acceleration is \"" + d.JellyfinAcceleration + "\", so \"Follow Jellyfin\" means software decoding. Set Dashboard > Playback > Transcoding to your GPU, or pick a decoder on the encoding profile."
                : "The default encoding profile decodes in software. Heavy 4K HEVC sources need a hardware decoder.");
        }
        else if (d.FfmpegError is null && !d.Hwaccels.Contains(d.DefaultDecoder, StringComparer.OrdinalIgnoreCase))
        {
            notes.Add("This ffmpeg build has no \"" + d.DefaultDecoder + "\" hwaccel; it offers " + (d.Hwaccels.Count == 0 ? "none" : string.Join(", ", d.Hwaccels)) + ".");
        }

        var drm = d.DefaultDecoder is "qsv" or "vaapi";
        if (d.Os == "linux" && drm && d.DrmNodes.Count == 0)
        {
            notes.Add("No /dev/dri device nodes are visible to Jellyfin. Pass the GPU through to the container or VM (Docker: --device /dev/dri).");
        }

        if (d.Os == "linux" && drm && d.DefaultDevice.Length > 0 && d.DrmNodes.Count > 0 && !d.DrmNodes.Contains(d.DefaultDevice, StringComparer.Ordinal))
        {
            notes.Add("The configured device " + d.DefaultDevice + " is not among the host's nodes (" + string.Join(", ", d.DrmNodes) + ").");
        }

        var encoders = d.H264Encoders.Concat(d.HevcEncoders).ToList();
        if (d.FfmpegError is null && encoders.Count > 0 && !encoders.Contains(d.DefaultEncoder, StringComparer.OrdinalIgnoreCase))
        {
            notes.Add("The encoder \"" + d.DefaultEncoder + "\" is not in this ffmpeg build; it has " + string.Join(", ", encoders) + ".");
        }

        if (d.FfmpegError is null && !d.HasTonemap)
        {
            notes.Add("This ffmpeg build lacks the zscale or tonemap filter, so HDR programs play without tone mapping.");
        }

        return notes;
    }

    /// <summary>Runs a few seconds of a station's current program through the real decode/filter/encode chain and reports what ffmpeg did.</summary>
    [HttpPost("Test")]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ValidationErrorDto))]
    public async Task<ActionResult<HardwareTestResult>> Test([FromBody] HardwareTestRequestDto? request, CancellationToken ct)
    {
        request ??= new HardwareTestRequestDto();
        var ffmpegPath = _sessions.FfmpegPath;
        if (!System.IO.File.Exists(ffmpegPath))
        {
            return BadRequest(ValidationErrorDto.Of("ffmpeg not found at " + ffmpegPath));
        }

        var pick = PickFile(request.StationId);
        if (pick is null)
        {
            return BadRequest(ValidationErrorDto.Of(request.StationId.HasValue ? "That station has no playable file to test with; refresh its content first." : "No station has a playable file to test with yet."));
        }

        var (station, part, seek) = pick.Value;
        var profile = (station.Encoding ?? _settings.Get().DefaultEncoding).Clone();
        if (request.HardwareDecode is not null)
        {
            profile.HardwareDecode = request.HardwareDecode.Trim().ToLowerInvariant();
        }

        if (request.HardwareDevice is not null)
        {
            profile.HardwareDevice = request.HardwareDevice.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.VideoEncoder))
        {
            profile.VideoEncoder = request.VideoEncoder.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.OutputRange))
        {
            profile.OutputRange = request.OutputRange.Trim().ToLowerInvariant();
        }

        var errors = StationService.ValidateEncoding(profile);
        if (errors.Count > 0)
        {
            return BadRequest(new ValidationErrorDto { Errors = errors });
        }

        profile = _sessions.ResolveHardware(profile);
        var seconds = Math.Clamp(request.Seconds ?? 5, 2, 15);
        string? skipReason = null;
        if (profile.HardwareDecode.Length > 0)
        {
            skipReason = _sessions.DecodePolicy(profile).WhyNot(part);
            if (skipReason is not null)
            {
                // Exactly what a program segment would do: this file goes to the software decoder.
                profile.HardwareDecode = string.Empty;
            }
        }

        // As on air: a file the picture check found fields in is deinterlaced in the auto mode.
        var foundFields = profile.DeinterlacesByFile ? _interlace.FoundFieldOrder(part) : null;
        var result = await HardwareProbe.TestAsync(ffmpegPath, profile, part.Path, part.HdrTransfer, part.VideoCodec, seek, seconds, ct, part.IsInterlaced || foundFields is not null, foundFields).ConfigureAwait(false);
        if (skipReason is not null)
        {
            result = result.WithDecoderSkipped(request.HardwareDecode ?? (station.Encoding ?? _settings.Get().DefaultEncoding).HardwareDecode, skipReason);
        }

        return Ok(result);
    }

    /// <summary>The file now airing on the station (at its live offset), else the first file the compile found, for one or every station.</summary>
    private (Station Station, PlayablePart Part, TimeSpan Seek)? PickFile(Guid? stationId)
    {
        var now = DateTime.UtcNow;
        IEnumerable<Station> candidates = stationId.HasValue
            ? (_stations.Get(stationId.Value) is { } one ? new[] { one } : Array.Empty<Station>())
            : _stations.GetAll().Where(s => s.Enabled);

        foreach (var station in candidates)
        {
            var resolved = _resolver.Resolve(station.Id, now);
            if (resolved?.Entry.Item is { } airing)
            {
                var segment = airing.SegmentAt(resolved.Offset);
                if (System.IO.File.Exists(segment.Part.Path))
                {
                    return (station, segment.Part, segment.Seek);
                }
            }

            var compiled = _stations.GetCompiled(station.Id);
            if (compiled is null)
            {
                continue;
            }

            var first = compiled.ProgramItems
                .Select(i => i.SegmentAt(TimeSpan.Zero).Part)
                .FirstOrDefault(p => System.IO.File.Exists(p.Path));
            if (first is not null)
            {
                // A minute in, so the test does not sit on studio logos; never past a quarter of the file.
                var seek = TimeSpan.FromSeconds(Math.Min(60, first.Runtime.TotalSeconds / 4));
                return (station, first, seek);
            }
        }

        return null;
    }
}
