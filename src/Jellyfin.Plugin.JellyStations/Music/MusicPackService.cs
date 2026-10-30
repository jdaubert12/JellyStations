using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Weather;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Music;

/// <summary>
/// The music pack catalogue (embedded <c>Assets/music-packs.json</c>) and the downloader that installs an automatic pack into
/// <c>data/JellyStations/music/&lt;folder&gt;/</c>: one file at a time from the artist's site, written to a temporary name, checked
/// against the published SHA-256 and renamed, with a CREDITS.txt beside the tracks. Files already present and intact are skipped,
/// so a cancelled or failed run picks up where it stopped. One download runs at a time.
/// </summary>
public sealed class MusicPackService : IDisposable
{
    private const string ResourceName = "Jellyfin.Plugin.JellyStations.Assets.music-packs.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private readonly JellyStationsPaths _paths;
    private readonly IHttpClientFactory _http;
    private readonly IServiceProvider _services;
    private readonly ILogger<MusicPackService> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, RunState> _runs = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private string? _runningId;
    private int _disposed;

    public MusicPackService(JellyStationsPaths paths, IHttpClientFactory http, IServiceProvider services, ILogger<MusicPackService> logger)
    {
        _paths = paths;
        _http = http;
        _services = services;
        _logger = logger;
        Packs = LoadCatalog();
    }

    public IReadOnlyList<MusicPack> Packs { get; }

    public MusicPack? Get(string id) => Packs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the embedded catalogue; a broken catalogue is logged and yields no packs rather than failing startup.</summary>
    public static IReadOnlyList<MusicPack> LoadCatalog(Assembly? assembly = null)
    {
        using var stream = (assembly ?? Assembly.GetExecutingAssembly()).GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("Embedded music pack catalogue missing: " + ResourceName);
        return JsonSerializer.Deserialize<List<MusicPack>>(stream, JsonOptions) ?? new List<MusicPack>();
    }

    public List<MusicPackStatusDto> StatusAll() => Packs.Select(Status).ToList();

    public MusicPackStatusDto Status(MusicPack pack)
    {
        var folder = _paths.ResolveMusicFolder(pack.Folder);
        var present = CountPresent(pack, folder);
        var dto = new MusicPackStatusDto
        {
            Id = pack.Id,
            Name = pack.Name,
            Description = pack.Description,
            Folder = pack.Folder,
            FolderPath = folder,
            Artist = pack.Artist,
            Licence = pack.Licence,
            LicenceUrl = pack.LicenceUrl,
            Attribution = pack.Attribution,
            Manual = pack.Manual,
            Links = pack.Links,
            TrackCount = pack.Tracks.Count,
            TotalBytes = pack.TotalBytes,
            TotalSeconds = pack.TotalSeconds,
            Present = present,
            Installed = pack.Manual ? present > 0 : pack.Tracks.Count > 0 && present == pack.Tracks.Count
        };

        lock (_lock)
        {
            if (_runs.TryGetValue(pack.Id, out var run))
            {
                dto.Running = run.Running;
                dto.Downloaded = run.Downloaded;
                dto.DownloadedBytes = run.Bytes;
                dto.Error = run.Error;
                dto.FinishedUtc = run.FinishedUtc;
            }
        }

        return dto;
    }

    /// <summary>Starts installing a pack in the background; returns the status at once. A run already going is left alone.</summary>
    public MusicPackStatusDto Install(MusicPack pack)
    {
        if (pack.Manual)
        {
            throw new InvalidOperationException("This pack is downloaded by hand; the plugin cannot fetch it.");
        }

        lock (_lock)
        {
            if (_runningId is not null)
            {
                if (!string.Equals(_runningId, pack.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Another pack is downloading; wait for it to finish or cancel it.");
                }

                return Status(pack);
            }

            _runningId = pack.Id;
            _cts = new CancellationTokenSource();
            _runs[pack.Id] = new RunState { Running = true };
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(pack, ct), CancellationToken.None);
        }

        return Status(pack);
    }

    public MusicPackStatusDto Cancel(MusicPack pack)
    {
        lock (_lock)
        {
            if (string.Equals(_runningId, pack.Id, StringComparison.OrdinalIgnoreCase))
            {
                _cts?.Cancel();
            }
        }

        return Status(pack);
    }

    /// <summary>Count of pack files present and complete, by size (the hash was checked when they were written).</summary>
    private static int CountPresent(MusicPack pack, string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        if (pack.Manual)
        {
            return Directory.EnumerateFiles(folder).Count(f => MusicFolderScanner.IsAudioFile(f));
        }

        var n = 0;
        foreach (var t in pack.Tracks)
        {
            var info = new FileInfo(Path.Combine(folder, t.FileName));
            if (info.Exists && info.Length == t.Bytes)
            {
                n++;
            }
        }

        return n;
    }

    private async Task RunAsync(MusicPack pack, CancellationToken ct)
    {
        var folder = _paths.ResolveMusicFolder(pack.Folder);
        string? error = null;
        try
        {
            Directory.CreateDirectory(folder);
            var client = _http.CreateClient("JellyStations.Packs");
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-JellyStations/" + WeatherHttp.Version);

            foreach (var track in pack.Tracks)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(folder, track.FileName);
                if (await IsIntactAsync(dest, track, ct).ConfigureAwait(false))
                {
                    Progress(pack.Id, track.Bytes);
                    continue;
                }

                await DownloadAsync(client, track, dest, ct).ConfigureAwait(false);
                Progress(pack.Id, track.Bytes);
            }

            File.WriteAllText(Path.Combine(folder, "CREDITS.txt"), Credits(pack), Encoding.UTF8);
            _logger.LogInformation("JellyStations music: installed pack {Pack} ({Count} tracks) into {Folder}", pack.Name, pack.Tracks.Count, folder);
            RecompileUsers(folder);
        }
        catch (OperationCanceledException)
        {
            error = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyStations music: pack {Pack} failed", pack.Name);
            error = ex.Message;
        }

        lock (_lock)
        {
            if (_runs.TryGetValue(pack.Id, out var run))
            {
                run.Running = false;
                run.Error = error;
                run.FinishedUtc = DateTime.UtcNow;
            }

            _runningId = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Progress(string packId, long bytes)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(packId, out var run))
            {
                run.Downloaded++;
                run.Bytes += bytes;
            }
        }
    }

    private static async Task<bool> IsIntactAsync(string path, MusicPackTrack track, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != track.Bytes)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return HashesMatch(hash, track.Sha256);
    }

    private static async Task DownloadAsync(HttpClient client, MusicPackTrack track, string dest, CancellationToken ct)
    {
        var temp = dest + ".part";
        try
        {
            using var response = await client.GetAsync(track.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] hash;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > track.Bytes)
                    {
                        // The catalogue says how long the file is; stop as soon as the server sends more rather than filling the disk.
                        throw new InvalidDataException($"\"{track.Title}\" is longer than the published file ({track.Bytes} bytes); the source may have changed.");
                    }

                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                hash = sha.GetHashAndReset();
            }

            var length = new FileInfo(temp).Length;
            if (length != track.Bytes || !HashesMatch(hash, track.Sha256))
            {
                throw new InvalidDataException($"\"{track.Title}\" did not match the published file (got {length} bytes, expected {track.Bytes}); the source may have changed.");
            }

            File.Move(temp, dest, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // Leftover .part files are harmless; the scanner ignores them.
            }
        }
    }

    internal static bool HashesMatch(byte[] hash, string expectedHex)
        => string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>The credits file written beside the tracks: licence, how to credit, and every track with its source page.</summary>
    public static string Credits(MusicPack pack)
    {
        var sb = new StringBuilder();
        sb.Append(pack.Name).Append(" (JellyStations music pack \"").Append(pack.Id).AppendLine("\")");
        sb.AppendLine(pack.Description);
        sb.AppendLine();
        sb.Append("Licence: ").Append(pack.Licence).Append(" - ").AppendLine(pack.LicenceUrl);
        sb.Append("Credit: ").AppendLine(pack.Attribution);
        sb.AppendLine();
        foreach (var t in pack.Tracks)
        {
            sb.Append(t.Title).Append(" - ").Append(t.Artist).Append("  ").Append(TimeSpan.FromSeconds(t.Seconds).ToString(@"m\:ss")).AppendLine();
            sb.Append("    ").AppendLine(string.IsNullOrEmpty(t.SourcePage) ? t.Url : t.SourcePage);
        }

        return sb.ToString();
    }

    /// <summary>Recompiles every weather station that plays this folder, so the new tracks join the timeline without a nightly wait.</summary>
    private void RecompileUsers(string folder)
    {
        try
        {
            var repo = _services.GetRequiredService<StationRepository>();
            var stations = _services.GetRequiredService<StationService>();
            foreach (var station in repo.GetAll().Where(s => WeatherSources.IsChannel(s) && s.Weather.MusicEnabled))
            {
                if (MusicResolver.Folders(station.Weather.MusicFolder).Any(f => PathsEqual(_paths.ResolveMusicFolder(f), folder)))
                {
                    stations.Recompile(station.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations music: recompile after pack install failed");
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_lock)
        {
            _cts?.Cancel();
        }
    }

    private sealed class RunState
    {
        public bool Running { get; set; }

        public int Downloaded { get; set; }

        public long Bytes { get; set; }

        public string? Error { get; set; }

        public DateTime? FinishedUtc { get; set; }
    }
}
