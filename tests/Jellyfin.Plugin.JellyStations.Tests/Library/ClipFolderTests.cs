using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public sealed class ClipFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellystations-clips-" + Guid.NewGuid().ToString("N"));
    private int _probes;

    public ClipFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bumpers"));
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

    private string IndexFile => Path.Combine(_root, "clip-index.json");

    private string BumpersDir => Path.Combine(_root, "bumpers");

    /// <summary>A scanner whose "ffprobe" reads the clip's length in seconds from the file's own text; "bad" is unreadable.</summary>
    private ClipFolderScanner Scanner() => new(IndexFile, BumpersDir, Probe, NullLogger.Instance);

    private ClipFolderScanner.IndexEntry Probe(string file)
    {
        Interlocked.Increment(ref _probes);
        var text = File.ReadAllText(file);
        if (text == "bad")
        {
            return new ClipFolderScanner.IndexEntry();
        }

        if (text == "throw")
        {
            throw new TimeoutException("ffprobe did not finish");
        }

        return new ClipFolderScanner.IndexEntry
        {
            RuntimeTicks = TimeSpan.FromSeconds(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).Ticks,
            HasVideo = true,
            HasAudio = true,
            VideoCodec = "h264"
        };
    }

    private string Clip(string relative, string content)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Probe_json_gives_the_length_and_the_video_facts()
    {
        const string Json = """
            {
              "streams": [
                { "codec_name": "mjpeg", "codec_type": "video", "pix_fmt": "yuvj420p", "disposition": { "attached_pic": 1 } },
                { "codec_name": "H264", "profile": "High", "codec_type": "video", "pix_fmt": "yuv420p", "field_order": "progressive", "color_transfer": "bt709", "bits_per_raw_sample": "8", "disposition": { "attached_pic": 0 } },
                { "codec_name": "aac", "codec_type": "audio", "disposition": { "attached_pic": 0 } }
              ],
              "format": { "duration": "15.500000" }
            }
            """;
        var entry = new ClipFolderScanner.IndexEntry();

        ClipFolderScanner.ParseProbeJson(Json, entry);

        Assert.True(entry.Playable());
        Assert.Equal(TimeSpan.FromSeconds(15.5).Ticks, entry.RuntimeTicks);
        Assert.True(entry.HasAudio);
        Assert.Equal(("h264", "High", "yuv420p", 8), (entry.VideoCodec, entry.VideoProfile, entry.PixelFormat, entry.VideoBitDepth));
        Assert.False(entry.IsInterlaced);
        Assert.Null(entry.HdrTransfer);
    }

    [Fact]
    public void Probe_json_reads_hdr_interlacing_and_silence()
    {
        var hdr = new ClipFolderScanner.IndexEntry();
        ClipFolderScanner.ParseProbeJson(
            """{"streams":[{"codec_name":"hevc","profile":"Main 10","codec_type":"video","pix_fmt":"yuv420p10le","color_transfer":"smpte2084"}],"format":{"duration":"8.0"}}""",
            hdr);
        Assert.Equal(("hevc", "Main 10", 10, "smpte2084"), (hdr.VideoCodec, hdr.VideoProfile, hdr.VideoBitDepth, hdr.HdrTransfer));
        Assert.False(hdr.HasAudio);
        Assert.True(hdr.Playable());

        var interlaced = new ClipFolderScanner.IndexEntry();
        ClipFolderScanner.ParseProbeJson(
            """{"streams":[{"codec_name":"mpeg2video","codec_type":"video","pix_fmt":"yuv420p","field_order":"tt"}],"format":{"duration":"30"}}""",
            interlaced);
        Assert.True(interlaced.IsInterlaced);
    }

    [Fact]
    public void A_file_without_a_picture_or_a_length_is_not_a_clip()
    {
        var audioOnly = new ClipFolderScanner.IndexEntry();
        ClipFolderScanner.ParseProbeJson("""{"streams":[{"codec_name":"mp3","codec_type":"audio"}],"format":{"duration":"200"}}""", audioOnly);
        Assert.False(audioOnly.Playable());

        var coverOnly = new ClipFolderScanner.IndexEntry();
        ClipFolderScanner.ParseProbeJson("""{"streams":[{"codec_name":"png","codec_type":"video","disposition":{"attached_pic":1}}],"format":{"duration":"200"}}""", coverOnly);
        Assert.False(coverOnly.Playable());

        var noLength = new ClipFolderScanner.IndexEntry();
        ClipFolderScanner.ParseProbeJson("""{"streams":[{"codec_name":"h264","codec_type":"video"}],"format":{}}""", noLength);
        Assert.False(noLength.Playable());

        var garbage = new ClipFolderScanner.IndexEntry();
        ClipFolderScanner.ParseProbeJson("moov atom not found", garbage);
        Assert.False(garbage.Playable());
    }

    [Fact]
    public void The_ffmpeg_banner_is_enough_to_play_a_file()
    {
        const string Banner = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from '/media/bumps/ident.mp4':
              Duration: 00:00:21.45, start: 0.000000, bitrate: 2130 kb/s
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1280x720, 2000 kb/s, 30 fps (default)
              Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 128 kb/s (default)
            At least one output file must be specified
            """;
        var entry = new ClipFolderScanner.IndexEntry();

        ClipFolderScanner.ParseFfmpegInfo(Banner, entry);

        Assert.True(entry.Playable());
        Assert.Equal(TimeSpan.FromSeconds(21.45).Ticks, entry.RuntimeTicks);
        Assert.True(entry.HasAudio);
        Assert.Equal("h264", entry.VideoCodec);
    }

    [Theory]
    [InlineData(10.0, "yuv420p", 10)]
    [InlineData(null, "yuv420p", 8)]
    [InlineData(null, "yuv420p10le", 10)]
    [InlineData(null, "yuv422p12be", 12)]
    [InlineData(null, "p010le", 10)]
    [InlineData(null, "nv12", 8)]
    [InlineData(null, null, null)]
    public void Bit_depth_comes_from_the_probe_or_the_pixel_format(double? stated, string? pixelFormat, int? expected)
        => Assert.Equal(expected, ClipFolderScanner.BitDepth(stated, pixelFormat));

    [Fact]
    public void A_scan_lists_the_video_files_under_the_folder_and_reports_the_unreadable()
    {
        var a = Clip("clips/b-ident.mp4", "10");
        var b = Clip("clips/sub/A-promo.mkv", "45");
        Clip("clips/readme.txt", "10");
        Clip("clips/cover.jpg", "10");
        Clip("clips/broken.mp4", "bad");
        Clip("clips/hangs.mov", "throw");
        Clip("elsewhere/other.mp4", "10");

        var scan = Scanner().Scan(Path.Combine(_root, "clips"));

        Assert.True(scan.Exists);
        Assert.Equal(4, scan.Files);
        Assert.Equal(new[] { "b-ident", "A-promo" }, scan.Clips.Select(c => c.Title));
        Assert.Equal(new[] { a, b }, scan.Clips.Select(c => c.Path));
        Assert.Equal(TimeSpan.FromSeconds(45).Ticks, scan.Clips[1].RuntimeTicks);
        Assert.All(scan.Clips, c => Assert.True(c.IsMovie && c.HasAudio && c.VideoCodec == "h264"));
        Assert.Equal(new[] { "broken.mp4", "hangs.mov" }, scan.Unreadable.Select(u => u.Name).OrderBy(n => n));
        Assert.All(scan.Unreadable, u => Assert.Equal(ExclusionReason.Unreadable, u.Reason));
    }

    [Fact]
    public void Files_are_probed_once_until_they_change()
    {
        var file = Clip("clips/ident.mp4", "10");
        Clip("clips/promo.mp4", "20");
        var folder = Path.Combine(_root, "clips");

        var first = Scanner().Scan(folder);
        Assert.Equal(2, _probes);

        // A new scanner reads the saved index: nothing is probed again, and the ids hold.
        var second = Scanner().Scan(folder);
        Assert.Equal(2, _probes);
        Assert.Equal(first.Clips.Select(c => c.ItemId), second.Clips.Select(c => c.ItemId));
        Assert.Equal(ClipFolderScanner.IdFor(file), second.Clips[0].ItemId);

        // A replaced file (new size) is probed again; the other is not.
        File.WriteAllText(file, "12.5");
        var third = Scanner().Scan(folder);
        Assert.Equal(3, _probes);
        Assert.Equal(TimeSpan.FromSeconds(12.5).Ticks, third.Clips[0].RuntimeTicks);
    }

    [Fact]
    public void A_name_without_a_path_is_a_folder_under_the_bumpers_folder()
    {
        Clip("bumpers/idents/one.mp4", "5");
        var scanner = Scanner();

        Assert.Equal(Path.Combine(BumpersDir, "idents"), scanner.Resolve(" idents "));
        Assert.Single(scanner.Scan("idents").Clips);
        Assert.Null(scanner.Resolve("  "));

        var missing = scanner.Scan("nothing-here");
        Assert.False(missing.Exists);
        Assert.Empty(missing.Clips);
        Assert.Null(scanner.Files(null));
    }

    [Fact]
    public void A_station_compiles_its_bumpers_from_a_folder()
    {
        var ident = Clip("clips/ident.mp4", "10");
        Clip("clips/promo.mp4", "45");
        Clip("clips/short-film.mp4", "900");
        Clip("clips/broken.mp4", "bad");
        var folder = Path.Combine(_root, "clips");
        var resolver = new LibraryContentResolver(null!, NullLogger<LibraryContentResolver>.Instance, Scanner());
        var compiler = new StationCompiler(resolver, NullLogger<StationCompiler>.Instance);
        var station = new Station
        {
            Id = Guid.NewGuid(),
            Name = "Folder bumps",
            Bumpers = new BumperConfig
            {
                Enabled = true,
                MaxClipMinutes = 5,
                // The same folder twice (a trailing separator is still the same files) adds nothing.
                Sources =
                {
                    new ContentSource { Kind = SourceKind.Folder, Path = folder },
                    new ContentSource { Kind = SourceKind.Folder, Path = folder + Path.DirectorySeparatorChar }
                },
                PreRoll = new BumperRollConfig { Enabled = true }
            }
        };

        var compiled = compiler.Compile(station, new EncodingProfile());

        // The 15-minute file is over the clip cap; the unreadable one is reported.
        Assert.Equal(new[] { "ident", "promo" }, compiled.Bumpers.Select(b => b.Title));
        Assert.Equal(compiled.Bumpers.Select(b => b.ItemId), compiled.PreRoll.Select(b => b.ItemId));
        Assert.Empty(compiled.PostRoll);
        Assert.Contains(compiled.Excluded, e => e.Name == "broken.mp4" && e.Reason == ExclusionReason.Unreadable);
        var first = compiled.Sources[0];
        Assert.Equal(("Bumpers", "Folder \"" + folder + "\"", SourceKind.Folder, 4, 3), (first.Section, first.Label, first.Kind, first.Found, first.Kept));
        Assert.False(first.IsEmpty);
        Assert.False(compiled.Sources[1].IsEmpty);

        // A clip excluded on the station stays off it.
        station.ExcludedItemIds.Add(ClipFolderScanner.IdFor(ident));
        Assert.Equal(new[] { "promo" }, compiler.Compile(station, new EncodingProfile()).Bumpers.Select(b => b.Title));
    }

    [Fact]
    public void A_missing_folder_is_an_empty_source()
    {
        var resolver = new LibraryContentResolver(null!, NullLogger<LibraryContentResolver>.Instance, Scanner());
        var compiler = new StationCompiler(resolver, NullLogger<StationCompiler>.Instance);
        var station = new Station { Bumpers = new BumperConfig { Enabled = true, Sources = { new ContentSource { Kind = SourceKind.Folder, Path = Path.Combine(_root, "gone") } } } };

        var compiled = compiler.Compile(station, new EncodingProfile());

        Assert.Empty(compiled.Bumpers);
        Assert.True(Assert.Single(compiled.Sources).IsEmpty);
    }

    [Fact]
    public void A_folder_is_break_material_only_and_needs_a_path()
    {
        var folder = new ContentSource { Kind = SourceKind.Folder, Path = "/media/bumps" };
        var good = new Station { Bumpers = new BumperConfig { Sources = { folder }, PostRoll = new BumperRollConfig { Sources = { folder } } } };
        Assert.Empty(StationService.FolderSourceErrors(good));

        var bad = new Station
        {
            Pool = new PoolConfig { Sources = { folder } },
            Bumpers = new BumperConfig { PreRoll = new BumperRollConfig { Sources = { new ContentSource { Kind = SourceKind.Folder, Path = " " } } } }
        };
        bad.Slots.Blocks.Add(new SlotBlock { Name = "Morning", Sources = { folder } });

        var errors = StationService.FolderSourceErrors(bad);

        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("Pool:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Block \"Morning\":", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Pre-roll:", StringComparison.Ordinal));
    }
}
