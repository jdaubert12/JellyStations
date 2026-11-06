using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public sealed class InterlaceCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellystations-interlace-" + Guid.NewGuid().ToString("N"));
    private readonly List<InterlaceCheckService.Candidate> _files = new();
    private readonly List<(string Path, double Start, double Seconds)> _sampled = new();

    public InterlaceCheckTests()
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

    private string StoreFile => Path.Combine(_root, "interlace-check.json");

    /// <summary>idet's two summary lines as ffmpeg prints them at the end of a run.</summary>
    private static string Idet(int tff, int bff, int progressive, int undetermined = 0) =>
        $"[Parsed_idet_0 @ 0x55d0c0] Repeated Fields: Neither:   180 Top:     0 Bottom:     0\n"
        + $"[Parsed_idet_0 @ 0x55d0c0] Single frame detection: TFF: {tff,5} BFF: {bff,5} Progressive: {progressive,5} Undetermined: {undetermined,5}\n"
        + $"[Parsed_idet_0 @ 0x55d0c0] Multi frame detection: TFF: {tff + bff,5} BFF:     0 Progressive: {progressive,5} Undetermined: {undetermined,5}\n";

    /// <summary>A service whose "ffmpeg" answers with the text of the file itself: "tff", "bff", "clean", "noisy" or "fail".</summary>
    private InterlaceCheckService Service() => new(StoreFile, () => _files, Sample, NullLogger.Instance);

    private Task<string?> Sample(string path, double start, double seconds, CancellationToken ct)
    {
        _sampled.Add((path, start, seconds));
        return Task.FromResult(File.ReadAllText(path) switch
        {
            "tff" => Idet(108, 0, 72),
            "bff" => Idet(0, 180, 0),
            "noisy" => Idet(59, 76, 45),
            "fail" => null,
            _ => Idet(0, 0, 140, 40)
        });
    }

    private PlayableItem Add(string name, string content, double? fps = 29.97, int? height = 480, bool interlaced = false, double minutes = 22)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        var item = new PlayableItem
        {
            ItemId = Guid.NewGuid(),
            Path = path,
            Title = name,
            RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
            FrameRate = fps,
            VideoHeight = height,
            IsInterlaced = interlaced
        };
        _files.Add(new InterlaceCheckService.Candidate(item, name));
        return item;
    }

    [Fact]
    public void Idet_output_is_summed_over_the_runs()
    {
        var counts = InterlaceCheckService.ParseIdet(Idet(0, 0, 0) + Idet(157, 16, 7) + "frame=  180 fps=0.0\n" + Idet(155, 16, 9, 2));

        Assert.Equal(new InterlaceCheckService.Counts(312, 32, 16, 2), counts);
        Assert.Equal(344, counts.Interlaced);
        Assert.Equal(360, counts.Determined);
        Assert.Equal(default, InterlaceCheckService.ParseIdet(null));
        Assert.Equal(default, InterlaceCheckService.ParseIdet("Conversion failed!"));
    }

    // The counts are what ffmpeg 8.1's idet gave over three 6 s samples of test encodes, all stored as progressive frames.
    [Theory]
    [InlineData(540, 0, 0, 0, "tff")] // 60i video woven into frames
    [InlineData(0, 540, 0, 0, "bff")] // the same, bottom field first
    [InlineData(473, 42, 25, 0, "tff")] // film with 3:2 pulldown baked in
    [InlineData(324, 0, 216, 0, "tff")] // a cartoon drawn on twos, 3:2 pulldown
    [InlineData(183, 0, 112, 245, "tff")] // the same holding still half the time: the still frames give no verdict
    [InlineData(0, 0, 540, 0, null)] // progressive
    [InlineData(0, 0, 354, 186, null)] // a progressive cartoon
    [InlineData(0, 0, 0, 540, null)] // grain, or fine lines scrolling: no verdict at all
    [InlineData(185, 187, 168, 0, null)] // a starved progressive encode: "fields" in both orders
    [InlineData(166, 227, 142, 5, null)] // light grain: the same
    [InlineData(20, 0, 5, 515, null)] // too few frames with a verdict to go by
    [InlineData(40, 0, 400, 100, null)] // a few combed frames in a progressive file (an insert, a bad edit)
    public void Fields_are_found_by_their_share_and_one_field_order(int tff, int bff, int progressive, int undetermined, string? expected)
    {
        Assert.Equal(expected, InterlaceCheckService.Judge(new InterlaceCheckService.Counts(tff, bff, progressive, undetermined)));
    }

    [Theory]
    [InlineData(29.97, 480, false, null, true)]
    [InlineData(25.0, 576, false, null, true)]
    [InlineData(30.0, 1080, false, null, true)]
    [InlineData(null, null, false, null, true)] // size and rate unknown: possible
    [InlineData(23.976, 480, false, null, false)] // film rate: frames already
    [InlineData(59.94, 720, false, null, false)]
    [InlineData(29.97, 2160, false, null, false)]
    [InlineData(29.97, 480, true, null, false)] // flagged: deinterlaced without a look
    [InlineData(29.97, 1080, false, "smpte2084", false)]
    public void Only_files_that_could_hide_fields_are_looked_at(double? fps, int? height, bool interlaced, string? hdr, bool expected)
    {
        var file = new PlayablePart { Path = "/media/a.mkv", FrameRate = fps, VideoHeight = height, IsInterlaced = interlaced, HdrTransfer = hdr };

        Assert.Equal(expected, InterlaceCheckService.IsCandidate(file));
    }

    [Fact]
    public void A_file_is_sampled_in_three_places_and_a_short_one_from_its_start()
    {
        Assert.Equal(new[] { (264d, 6d), (660d, 6d), (1056d, 6d) }, InterlaceCheckService.SamplePlan(TimeSpan.FromMinutes(22)));
        Assert.Equal(new[] { (0d, 18d) }, InterlaceCheckService.SamplePlan(TimeSpan.FromSeconds(45)));
        Assert.Equal(new[] { (0d, 10d) }, InterlaceCheckService.SamplePlan(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { (0d, 18d) }, InterlaceCheckService.SamplePlan(TimeSpan.Zero));
    }

    [Fact]
    public async Task A_run_finds_the_files_with_fields_and_remembers_every_answer()
    {
        var combed = Add("arnold.mkv", "tff");
        var bottom = Add("camcorder.mkv", "bff");
        var clean = Add("clean.mkv", "clean");
        var noisy = Add("noisy.mkv", "noisy");
        var film = Add("film.mkv", "tff", fps: 23.976);
        var service = Service();

        Assert.Null(service.FoundFieldOrder(combed));
        await service.RunAsync();

        Assert.Equal("tff", service.FoundFieldOrder(combed));
        Assert.Equal("bff", service.FoundFieldOrder(bottom));
        Assert.Null(service.FoundFieldOrder(clean));
        Assert.Null(service.FoundFieldOrder(noisy));
        Assert.Null(service.FoundFieldOrder(film));
        Assert.DoesNotContain(_sampled, s => s.Path == film.Path);
        // Two samples without a frame of fields settle a clean file; the others take all three.
        Assert.Equal(2, _sampled.Count(s => s.Path == clean.Path));
        Assert.Equal(3, _sampled.Count(s => s.Path == combed.Path));

        var summary = service.Summarize();
        Assert.Equal((4, 4, 0, 0, 2), (summary.Files, summary.Checked, summary.Failed, summary.Pending, summary.FoundCount));
        Assert.Equal(new[] { "arnold.mkv", "camcorder.mkv" }, summary.Found.Select(f => f.Title));
        Assert.Equal(0.6, summary.Found[0].Share);

        // A restart reads the answers back and samples nothing.
        _sampled.Clear();
        var again = Service();
        await again.RunAsync();
        Assert.Empty(_sampled);
        Assert.Equal("tff", again.FoundFieldOrder(combed));
    }

    [Fact]
    public async Task A_replaced_file_is_looked_at_again()
    {
        var item = Add("show.mkv", "tff");
        var service = Service();
        await service.RunAsync();
        Assert.Equal("tff", service.FoundFieldOrder(item));

        File.WriteAllText(item.Path, "clean, and a different length");
        Assert.Null(service.FoundFieldOrder(item));
        _sampled.Clear();
        await service.RunAsync();

        Assert.NotEmpty(_sampled);
        Assert.Null(service.FoundFieldOrder(item));
        Assert.Equal(0, service.Summarize().FoundCount);
    }

    [Fact]
    public async Task A_file_that_cannot_be_sampled_is_counted_and_left_for_another_day()
    {
        Add("broken.mkv", "fail");
        var gone = Add("gone.mkv", "tff");
        File.Delete(gone.Path);
        var service = Service();

        await service.RunAsync();
        var samples = _sampled.Count;
        await service.RunAsync();

        Assert.Equal(samples, _sampled.Count);
        var summary = service.Summarize();
        Assert.Equal((2, 0, 2, 0), (summary.Files, summary.Checked, summary.Failed, summary.Pending));
    }

    [Fact]
    public async Task A_multi_part_item_is_looked_at_part_by_part()
    {
        var first = Path.Combine(_root, "part1.mkv");
        var second = Path.Combine(_root, "part2.mkv");
        File.WriteAllText(first, "clean");
        File.WriteAllText(second, "tff");
        var item = new PlayableItem
        {
            Path = first,
            Title = "Long Film",
            Parts =
            {
                new PlayablePart { Path = first, RuntimeTicks = TimeSpan.FromMinutes(50).Ticks, FrameRate = 29.97 },
                new PlayablePart { Path = second, RuntimeTicks = TimeSpan.FromMinutes(40).Ticks, FrameRate = 29.97 }
            }
        };
        var service = Service();

        service.Prioritize(item);
        await service.RunAsync();
        // Prioritize starts a run of its own; wait for whichever got the gate.
        for (var i = 0; i < 100 && service.FoundFieldOrder(item.Parts[1]) is null; i++)
        {
            await Task.Delay(20);
        }

        Assert.Null(service.FoundFieldOrder(item.Parts[0]));
        Assert.Equal("tff", service.FoundFieldOrder(item.Parts[1]));
    }

    [Fact]
    public void A_file_found_by_its_picture_is_deinterlaced_with_the_order_found()
    {
        var p = new EncodingProfile { HardwareDecode = string.Empty };

        Assert.Equal("bwdif=mode=send_frame:parity=bff:deint=all", FfmpegArgsBuilder.DeinterlaceFilter(p, interlaced: true, fieldOrder: "bff"));
        Assert.Equal("bwdif=mode=send_frame:parity=tff:deint=all", FfmpegArgsBuilder.DeinterlaceFilter(p, interlaced: true, fieldOrder: "tff"));
        Assert.Equal("bwdif=mode=send_frame:parity=auto:deint=all", FfmpegArgsBuilder.DeinterlaceFilter(p, interlaced: true));
        // Only a listed order reaches the filter text.
        Assert.Equal("bwdif=mode=send_frame:parity=auto:deint=all", FfmpegArgsBuilder.DeinterlaceFilter(p, interlaced: true, fieldOrder: "tff,scale=1:1"));
        Assert.Null(FfmpegArgsBuilder.DeinterlaceFilter(p, interlaced: false, fieldOrder: "tff"));

        var args = FfmpegArgsBuilder.ForSource("/media/show.mkv", true, null, null, TimeSpan.Zero, null, TimeSpan.Zero, p, interlaced: true, fieldOrder: "bff");
        Assert.StartsWith("bwdif=mode=send_frame:parity=bff:deint=all,scale=", args[args.IndexOf("-vf") + 1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("", true)]
    [InlineData("always", false)]
    [InlineData("off", false)]
    public void Only_the_auto_mode_goes_by_the_file(string mode, bool expected)
    {
        Assert.Equal(expected, new EncodingProfile { Deinterlace = mode }.DeinterlacesByFile);
    }

    [Theory]
    [InlineData("30000/1001", 29.97)]
    [InlineData("25/1", 25.0)]
    [InlineData("24", 24.0)]
    [InlineData("0/0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_probed_frame_rate_is_read_from_its_fraction(string? fraction, double? expected)
    {
        Assert.Equal(expected, ClipFolderScanner.Rate(fraction));
    }

    [Fact]
    public void Probe_json_gives_the_height_and_the_frame_rate()
    {
        var entry = new ClipFolderScanner.IndexEntry();
        ClipFolderScanner.ParseProbeJson(
            """{"streams":[{"codec_name":"hevc","codec_type":"video","pix_fmt":"yuv420p","height":480,"r_frame_rate":"30000/1001"}],"format":{"duration":"30"}}""",
            entry);

        Assert.Equal((480, 29.97), (entry.VideoHeight, entry.FrameRate));
        var clip = ClipFolderScanner.ToPlayable("/bumpers/ident.mkv", entry);
        Assert.Equal((480, 29.97), (clip.VideoHeight, clip.FrameRate));
        Assert.True(InterlaceCheckService.IsCandidate(clip));
    }
}
