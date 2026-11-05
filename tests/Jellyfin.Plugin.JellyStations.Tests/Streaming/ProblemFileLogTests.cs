using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

public sealed class ProblemFileLogTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid StationA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellystations-problems-" + Guid.NewGuid().ToString("N"));

    public ProblemFileLogTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A temp folder left behind is not worth failing a test over.
        }
    }

    private string StoreFile => Path.Combine(_root, "problem-files.json");

    private ProblemFileLog Log() => new(StoreFile, NullLogger.Instance);

    private string Media(string name, string content = "video")
    {
        var path = Path.GetFullPath(Path.Combine(_root, "media", name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_report_lists_the_file_with_its_station_and_survives_a_restart()
    {
        var path = Media("a.mkv");
        Log().Report(path, "Movie A", ProblemKind.WouldNotPlay, "ffmpeg exit 1: Invalid data found when processing input", StationA, "Films", T0);

        var file = Assert.Single(Log().List());
        Assert.Equal(path, file.Path);
        Assert.Equal("Movie A", file.Title);
        Assert.Equal(ProblemKind.WouldNotPlay, file.Kind);
        Assert.Equal("ffmpeg exit 1: Invalid data found when processing input", file.Detail);
        Assert.Equal(StationA, file.StationId);
        Assert.Equal("Films", file.StationName);
        Assert.Equal(T0, file.FirstUtc);
        Assert.Equal(1, file.Count);
        Assert.Equal(new FileInfo(path).Length, file.Length);
    }

    [Fact]
    public void A_second_report_counts_up_keeps_the_first_date_and_shows_the_latest_problem()
    {
        var path = Media("a.mkv");
        var log = Log();
        log.Report(path, "Movie A", ProblemKind.EndedEarly, "first", StationA, "Films", T0);
        log.Report(path, "Movie A", ProblemKind.WouldNotPlay, "second", null, "Classics", T0.AddHours(3));

        var file = Assert.Single(log.List());
        Assert.Equal(2, file.Count);
        Assert.Equal(T0, file.FirstUtc);
        Assert.Equal(T0.AddHours(3), file.LastUtc);
        Assert.Equal(ProblemKind.WouldNotPlay, file.Kind);
        Assert.Equal("second", file.Detail);
        Assert.Equal("Classics", file.StationName);
    }

    [Fact]
    public void The_list_has_the_latest_trouble_first_and_a_title_from_the_file_name_when_none_is_given()
    {
        var log = Log();
        log.Report(Media("old.mkv"), "Old", ProblemKind.EndedEarly, null, null, null, T0);
        log.Report(Media("new clip.mp4"), " ", ProblemKind.EndedEarly, null, null, null, T0.AddMinutes(5));

        Assert.Equal(new[] { "new clip", "Old" }, log.List().Select(f => f.Title).ToArray());
    }

    [Fact]
    public void A_long_or_multi_line_detail_is_cut_to_one_line()
    {
        var log = Log();
        log.Report(Media("a.mkv"), "A", ProblemKind.WouldNotPlay, "line one\nline two " + new string('x', 600), null, null, T0);

        var detail = Assert.Single(log.List()).Detail!;
        Assert.DoesNotContain('\n', detail);
        Assert.StartsWith("line one line two", detail);
        Assert.True(detail.Length <= 401);
    }

    [Fact]
    public void The_detail_drops_the_memory_addresses_ffmpeg_prints()
    {
        var log = Log();
        log.Report(Media("a.mkv"), "A", ProblemKind.DecodeErrors, "34 lines, the first: [vist#0:0/h264 @ 0x792f32eb4480] [dec:h264 @ 0x792F32E0E3C0] corrupt decoded frame", null, null, T0);

        Assert.Equal("34 lines, the first: [vist#0:0/h264] [dec:h264] corrupt decoded frame", Assert.Single(log.List()).Detail);
    }

    [Fact]
    public void A_clean_play_of_part_of_a_file_only_settles_one_that_could_not_be_opened()
    {
        var missing = Media("missing.mkv");
        var cut = Media("cut.mkv");
        var log = Log();
        log.Report(missing, "Missing", ProblemKind.CannotOpen, "File not found", null, null, T0);
        log.Report(cut, "Cut", ProblemKind.EndedEarly, "Stopped", null, null, T0);

        log.PlayedClean(missing, whole: false);
        log.PlayedClean(cut, whole: false);
        Assert.Equal(cut, Assert.Single(log.List()).Path);

        log.PlayedClean(cut, whole: true);
        Assert.Empty(log.List());
        Assert.Empty(Log().List());
    }

    [Fact]
    public void A_report_is_taken_back_only_when_it_is_of_that_kind()
    {
        var path = Media("a.mkv");
        var log = Log();
        log.Report(path, "A", ProblemKind.DecodeErrors, "damage", null, null, T0);

        log.Retract(path, ProblemKind.HardwareDecode);
        Assert.Single(log.List());

        log.Report(path, "A", ProblemKind.HardwareDecode, "qsv", null, null, T0);
        log.Retract(path, ProblemKind.HardwareDecode);
        Assert.Empty(log.List());
    }

    [Fact]
    public void A_folder_scan_lists_an_unreadable_file_once_and_drops_it_when_it_reads_fine()
    {
        var bad = Media("bad.mp4");
        var good = Media("good.mp4");
        var log = Log();

        log.FolderScanned(new[] { bad }, new[] { good }, T0);
        log.FolderScanned(new[] { bad, bad }, new[] { good }, T0.AddDays(1));

        var file = Assert.Single(log.List());
        Assert.Equal(bad, file.Path);
        Assert.Equal("bad", file.Title);
        Assert.Equal(ProblemKind.Unreadable, file.Kind);
        Assert.Null(file.StationId);
        Assert.Equal(1, file.Count);
        Assert.Equal(T0, file.FirstUtc);
        Assert.Equal(T0.AddDays(1), file.LastUtc);

        log.FolderScanned(Array.Empty<string>(), new[] { bad, good }, T0.AddDays(2));
        Assert.Empty(log.List());
    }

    [Fact]
    public void A_folder_scan_leaves_trouble_seen_on_air_alone()
    {
        var path = Media("clip.mp4");
        var log = Log();
        log.Report(path, "Clip", ProblemKind.DecodeErrors, "damage", StationA, "Films", T0);

        log.FolderScanned(new[] { path }, Array.Empty<string>(), T0.AddHours(1));
        Assert.Equal(ProblemKind.DecodeErrors, Assert.Single(log.List()).Kind);

        log.FolderScanned(Array.Empty<string>(), new[] { path }, T0.AddHours(2));
        Assert.Equal(ProblemKind.DecodeErrors, Assert.Single(log.List()).Kind);
    }

    [Fact]
    public void A_file_is_dismissed_by_its_id_and_the_list_can_be_cleared()
    {
        var a = Media("a.mkv");
        var b = Media("b.mkv");
        var log = Log();
        log.Report(a, "A", ProblemKind.EndedEarly, null, null, null, T0);
        log.Report(b, "B", ProblemKind.EndedEarly, null, null, null, T0);

        Assert.False(log.Dismiss("not-an-id"));
        Assert.True(log.Dismiss(ProblemFileLog.IdFor(a).ToUpperInvariant()));
        Assert.Equal(b, Assert.Single(log.List()).Path);
        Assert.Equal(32, ProblemFileLog.IdFor(a).Length);

        log.Clear();
        Assert.Empty(Log().List());
    }

    [Fact]
    public void A_recheck_drops_files_that_were_removed_or_replaced_and_keeps_the_ones_still_as_they_were()
    {
        var removed = Media("removed.mkv");
        var replaced = Media("replaced.mkv", "old bytes");
        var same = Media("same.mkv");
        var log = Log();
        foreach (var path in new[] { removed, replaced, same })
        {
            log.Report(path, null, ProblemKind.WouldNotPlay, "ffmpeg exit 1", null, null, T0);
        }

        File.Delete(removed);
        File.WriteAllText(replaced, "a re-encoded file of another size");

        Assert.Equal(2, log.Recheck());
        Assert.Equal(same, Assert.Single(log.List()).Path);
        Assert.Equal(0, log.Recheck());
        Assert.Equal(same, Assert.Single(Log().List()).Path);
    }

    [Fact]
    public void A_recheck_keeps_a_file_that_cannot_be_opened_until_it_can()
    {
        var path = Path.Combine(_root, "media", "later.mkv");
        var log = Log();
        log.Report(path, "Later", ProblemKind.CannotOpen, "File not found: " + path, null, null, T0);

        Assert.Equal(0, log.Recheck());
        Assert.Null(Assert.Single(log.List()).Length);

        Media("later.mkv");
        Assert.Equal(1, log.Recheck());
        Assert.Empty(log.List());
    }

    [Fact]
    public void The_list_stops_at_its_limit_and_the_files_not_seen_for_longest_go()
    {
        var log = Log();
        for (var i = 0; i < ProblemFileLog.MaxFiles + 3; i++)
        {
            log.Report(Path.Combine(_root, "media", "f" + i + ".mkv"), null, ProblemKind.CannotOpen, null, null, null, T0.AddMinutes(i));
        }

        var files = log.List();
        Assert.Equal(ProblemFileLog.MaxFiles, files.Count);
        Assert.DoesNotContain(files, f => f.Title is "f0" or "f1" or "f2");
        Assert.Contains(files, f => f.Title == "f3");
    }

    [Fact]
    public void A_blank_path_is_ignored_and_a_damaged_store_starts_over()
    {
        File.WriteAllText(StoreFile, "{ not json");
        var log = Log();
        log.Report(" ", "Nothing", ProblemKind.CannotOpen, null);
        log.PlayedClean(null, whole: true);
        log.Retract(null, ProblemKind.CannotOpen);
        Assert.Empty(log.List());

        log.Report(Media("a.mkv"), "A", ProblemKind.EndedEarly, null);
        Assert.Single(Log().List());
    }

    [Fact]
    public void Every_kind_has_words_for_the_page()
    {
        foreach (var kind in Enum.GetValues<ProblemKind>())
        {
            Assert.NotEqual(kind.ToString(), ProblemFileLog.Describe(kind));
        }
    }

    [Fact]
    public void A_clip_folder_scan_puts_its_unreadable_files_on_the_list_with_their_paths()
    {
        var bumpers = Path.Combine(_root, "bumpers");
        var bad = Media("idents/bad.mp4", "bad");
        Media("idents/good.mp4", "30");
        var log = Log();
        var scanner = new ClipFolderScanner(
            Path.Combine(_root, "clip-index.json"),
            bumpers,
            file => File.ReadAllText(file) == "bad"
                ? new ClipFolderScanner.IndexEntry()
                : new ClipFolderScanner.IndexEntry { RuntimeTicks = TimeSpan.FromSeconds(30).Ticks, HasVideo = true },
            NullLogger.Instance,
            log);

        var folder = Path.Combine(_root, "media", "idents");
        Assert.Single(scanner.Scan(folder).Clips);
        var file = Assert.Single(log.List());
        Assert.Equal(bad, file.Path);
        Assert.Equal(ProblemKind.Unreadable, file.Kind);

        // Fixed in place: the next scan probes it again, finds a clip, and the list lets go of it.
        File.WriteAllText(bad, "45 seconds");
        File.SetLastWriteTimeUtc(bad, DateTime.UtcNow.AddMinutes(1));
        var scannerAfter = new ClipFolderScanner(Path.Combine(_root, "clip-index.json"), bumpers, _ => new ClipFolderScanner.IndexEntry { RuntimeTicks = 1, HasVideo = true }, NullLogger.Instance, log);
        Assert.Equal(2, scannerAfter.Scan(folder).Clips.Count);
        Assert.Empty(log.List());
    }

    [Theory]
    [InlineData("[h264 @ 0x55] error while decoding MB 45 12, bytestream -7")]
    [InlineData("[h264 @ 0x55] concealing 1200 DC, 1200 AC, 1200 MV errors in P frame")]
    [InlineData("[mpegts @ 0x1] Packet corrupt (stream = 0, dts = 8100000).")]
    [InlineData("[in#0/matroska,webm @ 0x1] corrupt input packet in stream 0")]
    [InlineData("[vist#0:0/h264 @ 0x1] Error submitting packet to decoder: Invalid data found when processing input")]
    [InlineData("[h264 @ 0x55] Invalid NAL unit size (1920 > 512).")]
    [InlineData("[mpeg2video @ 0x9] ac-tex damaged at 12 20")]
    [InlineData("[matroska,webm @ 0x1] File ended prematurely at pos. 1048576 (0x100000)")]
    [InlineData("[mov,mov,mp4,m4a,3gp,3g2,mj2 @ 0x1] stream 0, offset 0x5f5e10: partial file")]
    public void Lines_about_bad_data_count_as_damage(string line)
    {
        Assert.True(FfmpegStderr.IsDamage(line));
    }

    [Theory]
    [InlineData("[mpegts @ 0x1] frame size not set")]
    [InlineData("Unsupported codec with id 98314 for input stream 3")]
    [InlineData("[matroska,webm @ 0x1] Could not find codec parameters for stream 2 (Subtitle: hdmv_pgs_subtitle): unspecified size")]
    [InlineData("[vost#0:0/libx264 @ 0x2] Past duration 0.999992 too large")]
    [InlineData("[mpegts @ 0x3] Application provided invalid, non monotonically increasing dts to muxer in stream 1: 100 >= 100")]
    [InlineData("[swscaler @ 0x4] deprecated pixel format used, make sure you did set range correctly")]
    public void Ordinary_warnings_are_not_damage(string line)
    {
        Assert.False(FfmpegStderr.IsDamage(line));
    }

    [Fact]
    public void Damage_is_counted_once_the_seek_has_settled_and_never_on_a_card()
    {
        const string Line = "[h264 @ 0x55] error while decoding MB 45 12, bytestream -7";
        var program = new SegmentTelemetry("Movie", isSlate: false, string.Empty, "libx264", toneMapping: false, sourceIsHdr: false, overlay: false);
        var start = program.StartedUtc;

        program.Observe(Line, start.AddSeconds(1));
        program.Observe(Line, start + FfmpegStderr.SeekSettle - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, program.Damage);
        Assert.Null(program.FirstDamage);

        program.Observe("  " + Line + "  ", start + FfmpegStderr.SeekSettle);
        program.Observe("[h264 @ 0x55] concealing 99 DC, 99 AC, 99 MV errors in P frame", start.AddMinutes(10));
        program.Observe("[mpegts @ 0x1] frame size not set", start.AddMinutes(10));
        Assert.Equal(2, program.Damage);
        Assert.Equal(Line, program.FirstDamage);
        Assert.Equal(5, program.Warnings);

        var card = new SegmentTelemetry("Card", isSlate: true, string.Empty, "libx264", toneMapping: false, sourceIsHdr: false, overlay: false);
        card.Observe(Line, card.StartedUtc.AddMinutes(1));
        Assert.Equal(0, card.Damage);
    }

    [Fact]
    public void An_early_end_says_where_it_stopped_and_how_much_was_owed()
    {
        Assert.Equal(
            "Stopped about 0:41:07 in, 0:03:53 short of its listed length: the file is shorter than Jellyfin has it, or cut off",
            FfmpegRestreamer.EndedEarlyDetail(new TimeSpan(0, 3, 53), new TimeSpan(0, 41, 7), 0, null));
        Assert.Equal(
            "Stopped about 1:02:00 in, 0:00:30 short of its listed length (ffmpeg exit 187): [matroska] File ended prematurely",
            FfmpegRestreamer.EndedEarlyDetail(TimeSpan.FromSeconds(30.4), new TimeSpan(1, 2, 0), 187, "[matroska] File ended prematurely"));
    }
}
