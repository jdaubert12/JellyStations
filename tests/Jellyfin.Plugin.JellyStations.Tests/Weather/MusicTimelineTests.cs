using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

public class MusicTimelineTests
{
    private static MusicTrack Track(string title, double minutes) => new()
    {
        Id = Items.DeterministicGuid("track-" + title),
        Path = "/music/" + title + ".mp3",
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        Title = title,
        Artist = "Someone",
        Source = "Folder"
    };

    private static readonly DateTime Anchor = new(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc);

    private static MusicTimeline Timeline(params MusicTrack[] tracks) => new(tracks, Anchor, Items.StationA, 3);

    [Fact]
    public void Empty_PlaysNothing()
    {
        var t = Timeline();
        Assert.True(t.IsEmpty);
        Assert.Null(t.At(Anchor));
        Assert.Empty(t.Between(Anchor, Anchor.AddHours(1)));
    }

    [Fact]
    public void At_FindsTheTrackAndOffset()
    {
        var t = Timeline(Track("a", 3), Track("b", 4), Track("c", 5));
        var order = t.Order(0);
        var firstLen = TimeSpan.FromMinutes(new[] { 3, 4, 5 }[order[0]]);

        var piece = t.At(Anchor.AddSeconds(30))!;
        Assert.Equal(TimeSpan.FromSeconds(30), piece.InPoint);
        Assert.Equal(firstLen - TimeSpan.FromSeconds(30), piece.Duration);
        Assert.Equal(Anchor.AddSeconds(30), piece.StartUtc);

        var second = t.At(Anchor + firstLen + TimeSpan.FromSeconds(1))!;
        Assert.Equal(TimeSpan.FromSeconds(1), second.InPoint);
        Assert.NotEqual(piece.Track.Id, second.Track.Id);
    }

    [Fact]
    public void Between_TilesTheWindow_AcrossRounds()
    {
        var t = Timeline(Track("a", 3), Track("b", 4), Track("c", 5));
        var from = Anchor.AddMinutes(10);
        var to = from.AddMinutes(9); // crosses the 12-minute round boundary

        var pieces = t.Between(from, to);

        Assert.Equal(from, pieces[0].StartUtc);
        Assert.Equal(to, pieces[^1].EndUtc);
        for (var i = 1; i < pieces.Count; i++)
        {
            Assert.Equal(pieces[i - 1].EndUtc, pieces[i].StartUtc);
            Assert.Equal(TimeSpan.Zero, pieces[i].InPoint); // every piece after the first starts a track
        }

        Assert.Equal(TimeSpan.FromMinutes(9), TimeSpan.FromTicks(pieces.Sum(p => p.Duration.Ticks)));
    }

    [Fact]
    public void Deterministic_ForStationAndRevision()
    {
        var tracks = new[] { Track("a", 3), Track("b", 4), Track("c", 5), Track("d", 2) };
        var x = new MusicTimeline(tracks, Anchor, Items.StationA, 3);
        var y = new MusicTimeline(tracks, Anchor, Items.StationA, 3);
        var z = new MusicTimeline(tracks, Anchor, Items.StationA, 4);

        var at = Anchor.AddHours(5).AddMinutes(7);
        Assert.Equal(x.At(at), y.At(at));
        Assert.True(Enumerable.Range(0, 20).Any(r => !x.Order(r).SequenceEqual(z.Order(r))));
    }

    [Fact]
    public void Rounds_ArePermutations_AndNeverRepeatTheLastTrackFirst()
    {
        var t = Timeline(Track("a", 3), Track("b", 4), Track("c", 5), Track("d", 2), Track("e", 1));
        for (var r = 0; r < 50; r++)
        {
            var order = t.Order(r);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order.OrderBy(i => i));
            Assert.NotEqual(t.Order(r - 1)[^1], order[0]);
        }
    }

    [Fact]
    public void TracksWithoutRuntime_AreSkipped()
    {
        var bad = Track("bad", 3);
        bad.RuntimeTicks = 0;
        var t = Timeline(bad, Track("ok", 2));
        Assert.Equal(1, t.Count);
        Assert.Equal("ok", t.At(Anchor)!.Track.Title);
    }
}
