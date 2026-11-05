using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Streaming;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Services;

/// <summary>
/// The files that gave trouble on air or in a folder scan, kept across restarts so they can be found and fixed.
/// The encoder's own log lives only as long as its session; this list is what is left when the stream has stopped.
/// A file leaves it when it plays through cleanly, when it has been replaced or removed, or when it is dismissed.
/// </summary>
public sealed class ProblemFileLog
{
    /// <summary>The most files remembered; past it the ones not seen for longest go.</summary>
    public const int MaxFiles = 500;

    // "[dec:h264 @ 0x792f32e0e3c0]": the address says nothing to a person and differs on every run.
    private static readonly System.Text.RegularExpressions.Regex Address = new(@" @ 0x[0-9a-fA-F]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly string _file;
    private readonly ILogger _logger;
    private Dictionary<string, ProblemFile>? _files;

    public ProblemFileLog(JellyStationsPaths paths, ILogger<ProblemFileLog> logger)
        : this(paths.ProblemFilesFile, logger)
    {
    }

    /// <summary>For tests: a log over any file.</summary>
    public ProblemFileLog(string file, ILogger logger)
    {
        _file = file;
        _logger = logger;
    }

    /// <summary>The id a file goes by in the API: a hash of its path, so no path travels in a URL.</summary>
    public static string IdFor(string path) => MusicFolderScanner.IdFor(path).ToString("N");

    /// <summary>The problem in a few words, for the Status page.</summary>
    public static string Describe(ProblemKind kind) => kind switch
    {
        ProblemKind.CannotOpen => "Could not be opened",
        ProblemKind.WouldNotPlay => "ffmpeg could not play it",
        ProblemKind.EndedEarly => "Stopped before its listed end",
        ProblemKind.DecodeErrors => "Played with decode errors",
        ProblemKind.HardwareDecode => "The GPU decoder failed on it; it played with software decoding",
        ProblemKind.Unreadable => "Could not be read as a video in a folder scan",
        _ => kind.ToString()
    };

    /// <summary>Every file on the list, the latest trouble first.</summary>
    public List<ProblemFile> List()
    {
        lock (_lock)
        {
            return Load().Values.OrderByDescending(f => f.LastUtc).ThenBy(f => f.Path, StringComparer.Ordinal).Select(Copy).ToList();
        }
    }

    /// <summary>Records trouble with a file on air. A file already listed keeps its first date and counts once more.</summary>
    public void Report(string? path, string? title, ProblemKind kind, string? detail, Guid? stationId = null, string? stationName = null, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        var (length, written) = Stat(path);
        lock (_lock)
        {
            var files = Load();
            if (!files.TryGetValue(path, out var entry))
            {
                entry = new ProblemFile { Path = path, FirstUtc = now };
                files[path] = entry;
            }

            entry.Title = string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileNameWithoutExtension(path) : title;
            entry.Kind = kind;
            entry.Detail = Shorten(detail);
            entry.StationId = stationId;
            entry.StationName = stationName;
            entry.LastUtc = now;
            entry.Count++;
            entry.Length = length;
            entry.LastWriteUtc = written;
            Trim(files);
            Save(files);
        }
    }

    /// <summary>
    /// A file played with nothing to report. That settles a file that could not be opened whatever part of it played;
    /// any other problem is only settled by the whole file, since a viewer who tunes in past the damage plays cleanly too.
    /// </summary>
    public void PlayedClean(string? path, bool whole)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_lock)
        {
            var files = Load();
            if (files.TryGetValue(path, out var entry) && (whole || entry.Kind == ProblemKind.CannotOpen) && entry.Kind != ProblemKind.Unreadable)
            {
                files.Remove(path);
                Save(files);
            }
        }
    }

    /// <summary>Takes back a report of one kind that turned out not to be the file's fault; another kind of entry stays.</summary>
    public void Retract(string? path, ProblemKind kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_lock)
        {
            var files = Load();
            if (files.TryGetValue(path, out var entry) && entry.Kind == kind)
            {
                files.Remove(path);
                Save(files);
            }
        }
    }

    /// <summary>
    /// What a clip folder scan found: files ffprobe could not read go on the list (once, however many stations share
    /// the folder and however often it is scanned), and files it reads fine come off it if a scan had put them there.
    /// </summary>
    public void FolderScanned(IReadOnlyCollection<string> unreadable, IReadOnlyCollection<string> readable, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var stats = unreadable.Distinct(StringComparer.Ordinal).ToDictionary(p => p, Stat, StringComparer.Ordinal);
        lock (_lock)
        {
            var files = Load();
            var changed = false;
            foreach (var path in unreadable)
            {
                if (files.TryGetValue(path, out var entry) && entry.Kind != ProblemKind.Unreadable)
                {
                    // Trouble on air says more than "ffprobe could not read it"; leave that entry as it is.
                    continue;
                }

                if (entry is null)
                {
                    entry = new ProblemFile
                    {
                        Path = path,
                        Title = System.IO.Path.GetFileNameWithoutExtension(path),
                        Kind = ProblemKind.Unreadable,
                        FirstUtc = now,
                        Count = 1
                    };
                    files[path] = entry;
                }

                entry.LastUtc = now;
                (entry.Length, entry.LastWriteUtc) = stats[path];
                changed = true;
            }

            foreach (var path in readable)
            {
                if (files.TryGetValue(path, out var entry) && entry.Kind == ProblemKind.Unreadable)
                {
                    files.Remove(path);
                    changed = true;
                }
            }

            if (changed)
            {
                Trim(files);
                Save(files);
            }
        }
    }

    /// <summary>Removes one file by its id; false when it is not on the list.</summary>
    public bool Dismiss(string id)
    {
        lock (_lock)
        {
            var files = Load();
            var path = files.Keys.FirstOrDefault(p => string.Equals(IdFor(p), id, StringComparison.OrdinalIgnoreCase));
            if (path is null)
            {
                return false;
            }

            files.Remove(path);
            Save(files);
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            var files = Load();
            if (files.Count > 0)
            {
                files.Clear();
                Save(files);
            }
        }
    }

    /// <summary>
    /// Drops the files that are no longer the ones that gave trouble: removed, replaced (another size or date), or, for
    /// one that could not be opened, readable again. Touches every listed file, so it runs when asked, not on a timer.
    /// </summary>
    public int Recheck()
    {
        List<ProblemFile> snapshot;
        lock (_lock)
        {
            snapshot = Load().Values.Select(Copy).ToList();
        }

        var settled = snapshot.Where(IsSettled).ToList();
        if (settled.Count == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            var files = Load();
            var removed = 0;
            foreach (var was in settled)
            {
                // Only the entry that was looked at: trouble reported in the meantime is news.
                if (files.TryGetValue(was.Path, out var entry) && entry.LastUtc == was.LastUtc && files.Remove(was.Path))
                {
                    removed++;
                }
            }

            if (removed > 0)
            {
                Save(files);
            }

            return removed;
        }
    }

    private static bool IsSettled(ProblemFile entry)
    {
        if (entry.Kind == ProblemKind.CannotOpen)
        {
            return FileProbe.Describe(entry.Path) is null;
        }

        var (length, written) = Stat(entry.Path);
        if (length is null)
        {
            // Gone (or its folder is): if a station still lists it, it comes back as a file that cannot be opened.
            return true;
        }

        return entry.Length is not null && (entry.Length != length || entry.LastWriteUtc != written);
    }

    /// <summary>A file's size and date, or nulls when it is missing or cannot be looked at.</summary>
    private static (long? Length, DateTime? LastWriteUtc) Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (null, null);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>One line of a sensible length, without ffmpeg's memory addresses: it can print a whole filter graph in an error.</summary>
    private static string? Shorten(string? detail)
    {
        var text = Text.TrimOrNull(detail);
        if (text is null)
        {
            return null;
        }

        text = Address.Replace(text.ReplaceLineEndings(" "), string.Empty);
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    private static void Trim(Dictionary<string, ProblemFile> files)
    {
        if (files.Count <= MaxFiles)
        {
            return;
        }

        foreach (var old in files.Values.OrderBy(f => f.LastUtc).Take(files.Count - MaxFiles).Select(f => f.Path).ToList())
        {
            files.Remove(old);
        }
    }

    private static ProblemFile Copy(ProblemFile f) => new()
    {
        Path = f.Path,
        Title = f.Title,
        Kind = f.Kind,
        Detail = f.Detail,
        StationId = f.StationId,
        StationName = f.StationName,
        FirstUtc = f.FirstUtc,
        LastUtc = f.LastUtc,
        Count = f.Count,
        Length = f.Length,
        LastWriteUtc = f.LastWriteUtc
    };

    private Dictionary<string, ProblemFile> Load()
    {
        if (_files is not null)
        {
            return _files;
        }

        var files = new Dictionary<string, ProblemFile>(StringComparer.Ordinal);
        try
        {
            var stored = JsonFileStore.Read<ProblemFileStore>(_file);
            if (stored is { Format: ProblemFileStore.CurrentFormat })
            {
                foreach (var f in stored.Files.Where(f => !string.IsNullOrWhiteSpace(f.Path)))
                {
                    files[f.Path] = f;
                }
            }
        }
        catch (Exception)
        {
            // A damaged file starts the list over; it is a convenience, not a record anything depends on.
        }

        _files = files;
        return files;
    }

    private void Save(Dictionary<string, ProblemFile> files)
    {
        try
        {
            JsonFileStore.Write(_file, new ProblemFileStore { Files = files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList() });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyStations: could not save the problem file list");
        }
    }
}
