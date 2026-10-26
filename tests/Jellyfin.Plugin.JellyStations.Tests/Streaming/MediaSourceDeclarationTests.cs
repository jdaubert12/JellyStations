using Jellyfin.Plugin.JellyStations.LiveTv;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Streaming;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

/// <summary>
/// What the plugin tells Jellyfin about a station's stream, and the Jellyfin behaviours it has to live with:
/// Jellyfin flags every plugin channel interlaced unless it probes the stream, caches that probe for good under a
/// key made from the media source id, and parses that id as a Guid when it builds an HLS playlist.
/// </summary>
public class MediaSourceDeclarationTests
{
    [Fact]
    public void Source_IsProbedAndNamesTheContainerTs()
    {
        var source = StreamSessionManager.BuildMediaSource(new Station { Id = Guid.NewGuid(), Name = "S" }, new EncodingProfile());

        Assert.True(source.SupportsProbing);
        Assert.Equal("ts", source.Container); // "mpegts" would make Jellyfin skip aac_adtstoasc on an fMP4 HLS transcode
        Assert.False(source.MediaStreams.Single(s => s.Type == MediaStreamType.Video).IsInterlaced);
    }

    [Fact]
    public void SourceId_IsAGuidThatChangesWithTheOutputFormat_NotWithBitrate()
    {
        var station = new Station { Id = Guid.NewGuid(), Name = "S" };
        var sdr = StreamSessionManager.MediaSourceId(station, new EncodingProfile { Width = 1920, Height = 1080, Fps = 30 });
        var again = StreamSessionManager.MediaSourceId(station, new EncodingProfile { Width = 1920, Height = 1080, Fps = 30 });
        var cheaper = StreamSessionManager.MediaSourceId(station, new EncodingProfile { Width = 1920, Height = 1080, Fps = 30, VideoBitrateKbps = 1500, Preset = "slow" });
        var hdr = StreamSessionManager.MediaSourceId(station, new EncodingProfile { Width = 1920, Height = 1080, Fps = 30, VideoEncoder = "hevc_qsv", OutputRange = EncodingProfile.OutputHdr10 });
        var smaller = StreamSessionManager.MediaSourceId(station, new EncodingProfile { Width = 1280, Height = 720, Fps = 30 });
        var otherStation = StreamSessionManager.MediaSourceId(new Station { Id = Guid.NewGuid(), Name = "T" }, new EncodingProfile { Width = 1920, Height = 1080, Fps = 30 });

        Assert.True(Guid.TryParseExact(sdr, "N", out _)); // Jellyfin's DynamicHlsController does Guid.Parse(mediaSourceId)
        Assert.Equal(sdr, again);
        Assert.Equal(sdr, cheaper);
        Assert.NotEqual(sdr, hdr);
        Assert.NotEqual(sdr, smaller);
        Assert.NotEqual(sdr, otherStation);
        Assert.NotEqual(station.Id.ToString("N"), sdr);
    }

    [Fact]
    public void OutputSignature_NamesCodecSizeRateAndRange()
    {
        Assert.Equal("h264-1920x1080p30-sdr", StreamSessionManager.OutputSignature(new EncodingProfile { Width = 1920, Height = 1080, Fps = 30 }));
        Assert.Equal("hevc-3840x2160p60-hdr10", StreamSessionManager.OutputSignature(new EncodingProfile { Width = 3840, Height = 2160, Fps = 60, VideoEncoder = "hevc_qsv", OutputRange = EncodingProfile.OutputHdr10 }));
    }

    [Fact]
    public void StationResolver_AcceptsChannelIdsAndMediaSourceIds()
    {
        var a = new Station { Id = Guid.NewGuid(), Name = "A", Encoding = new EncodingProfile { VideoEncoder = "libx265" } };
        var b = new Station { Id = Guid.NewGuid(), Name = "B" };
        var stations = new[] { a, b };
        EncodingProfile ProfileOf(Station s) => s.Encoding ?? new EncodingProfile();

        Assert.True(JellyStationsLiveTvService.TryResolveStationId(StreamSessionManager.MediaSourceId(b, ProfileOf(b)), stations, ProfileOf, out var fromSource));
        Assert.Equal(b.Id, fromSource);
        Assert.True(JellyStationsLiveTvService.TryResolveStationId(a.Id.ToString("N"), stations, ProfileOf, out var fromChannel));
        Assert.Equal(a.Id, fromChannel);
        Assert.False(JellyStationsLiveTvService.TryResolveStationId(Guid.NewGuid().ToString("N"), stations, ProfileOf, out _));
        Assert.False(JellyStationsLiveTvService.TryResolveStationId("not-an-id", stations, ProfileOf, out _));
    }
}
