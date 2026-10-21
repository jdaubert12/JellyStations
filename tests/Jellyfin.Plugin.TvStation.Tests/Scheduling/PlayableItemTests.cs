using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Streaming;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Scheduling;

public class PlayableItemTests
{
    private static PlayableItem Split()
    {
        var item = new PlayableItem
        {
            ItemId = Guid.NewGuid(),
            Title = "Fellowship",
            Path = "/m/fellowship-part1.mkv",
            RuntimeTicks = TimeSpan.FromMinutes(178).Ticks,
            IsMovie = true,
            Parts =
            {
                new PlayablePart { Path = "/m/fellowship-part1.mkv", RuntimeTicks = TimeSpan.FromMinutes(100).Ticks, AudioStreamIndex = 1 },
                new PlayablePart { Path = "/m/fellowship-part2.mkv", RuntimeTicks = TimeSpan.FromMinutes(78).Ticks, HasAudio = false }
            }
        };
        return item;
    }

    [Fact]
    public void SingleFile_IsItsOwnSegment()
    {
        var item = new PlayableItem { Path = "/m/a.mkv", RuntimeTicks = TimeSpan.FromMinutes(22).Ticks, AudioStreamIndex = 2 };
        Assert.False(item.IsMultiPart);

        var s = item.SegmentAt(TimeSpan.FromMinutes(5));
        Assert.Equal("/m/a.mkv", s.Part.Path);
        Assert.Equal(TimeSpan.FromMinutes(5), s.Seek);
        Assert.Equal(TimeSpan.FromMinutes(17), s.Remaining);
        Assert.Equal(2, s.Part.AudioStreamIndex);
        Assert.True(s.IsLast);

        var past = item.SegmentAt(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.Zero, past.Remaining);
    }

    [Fact]
    public void MultiPart_PicksThePartByOffset()
    {
        var item = Split();
        Assert.True(item.IsMultiPart);
        Assert.Equal(TimeSpan.FromMinutes(178), item.Runtime);

        var first = item.SegmentAt(TimeSpan.FromMinutes(30));
        Assert.Equal(0, first.Index);
        Assert.EndsWith("part1.mkv", first.Part.Path, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(30), first.Seek);
        Assert.Equal(TimeSpan.FromMinutes(70), first.Remaining);
        Assert.False(first.IsLast);

        var boundary = item.SegmentAt(TimeSpan.FromMinutes(100));
        Assert.Equal(1, boundary.Index);
        Assert.Equal(TimeSpan.Zero, boundary.Seek);
        Assert.Equal(TimeSpan.FromMinutes(78), boundary.Remaining);
        Assert.False(boundary.Part.HasAudio);
        Assert.True(boundary.IsLast);

        var second = item.SegmentAt(TimeSpan.FromMinutes(150));
        Assert.Equal(1, second.Index);
        Assert.Equal(TimeSpan.FromMinutes(50), second.Seek);
        Assert.Equal(TimeSpan.FromMinutes(28), second.Remaining);

        var past = item.SegmentAt(TimeSpan.FromMinutes(500));
        Assert.Equal(1, past.Index);
        Assert.Equal(TimeSpan.Zero, past.Remaining);
    }

    [Fact]
    public void FfmpegArgs_UseThePartFileAndItsAudioChoice()
    {
        var item = Split();
        var segment = item.SegmentAt(TimeSpan.FromMinutes(120));
        var args = FfmpegArgsBuilder.ForSource(segment.Part.Path, segment.Part.HasAudio, segment.Part.AudioStreamIndex, segment.Part.HdrTransfer, segment.Seek, null, TimeSpan.Zero, new EncodingProfile());

        Assert.Equal("/m/fellowship-part2.mkv", args[args.IndexOf("-i") + 1]);
        Assert.Equal("1200.000", args[args.IndexOf("-ss") + 1]);
        Assert.Contains("anullsrc=r=48000:cl=stereo", args); // part 2 has no audio track
        Assert.Contains("1:a:0", args);
    }
}
