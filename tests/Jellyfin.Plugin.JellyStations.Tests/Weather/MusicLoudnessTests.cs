using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Weather;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

public class MusicLoudnessTests
{
    private const string Stderr = @"Input #0, mp3, from '/music/a.mp3':
  Duration: 00:03:51.12, start: 0.025057, bitrate: 320 kb/s
  Stream #0:0: Audio: mp3 (mp3float), 44100 Hz, stereo, fltp, 320 kb/s
Output #0, null, to 'pipe:':
[Parsed_loudnorm_0 @ 0x55d0c0]
{
	""input_i"" : ""-11.73"",
	""input_tp"" : ""0.42"",
	""input_lra"" : ""6.80"",
	""input_thresh"" : ""-21.87"",
	""output_i"" : ""-17.65"",
	""output_tp"" : ""-1.00"",
	""output_lra"" : ""5.90"",
	""output_thresh"" : ""-27.78"",
	""normalization_type"" : ""dynamic"",
	""target_offset"" : ""-0.35""
}
";

    [Fact]
    public void ParseLoudnorm_ReadsTheIntegratedLoudnessAndTruePeak()
    {
        var entry = new MusicLoudnessService.Entry();
        Assert.True(MusicLoudnessService.ParseLoudnorm(Stderr, entry));
        Assert.Equal(-11.73, entry.Lufs);
        Assert.Equal(0.42, entry.TruePeakDb);

        Assert.False(MusicLoudnessService.ParseLoudnorm("Input #0... no summary", new MusicLoudnessService.Entry()));

        // Silence measures -inf: not a measurement.
        var silent = Stderr.Replace("\"-11.73\"", "\"-inf\"", StringComparison.Ordinal);
        Assert.False(MusicLoudnessService.ParseLoudnorm(silent, new MusicLoudnessService.Entry()));
    }

    [Fact]
    public void Gain_BringsTheTrackToTheTarget_CappedByThePeakAndTheLimits()
    {
        // A hot master: -11.7 LUFS → -6.3 dB to reach -18; the peak (0.4 dBTP) would allow -1.4, so the loudness rules.
        Assert.Equal(-6.3, MusicLoudnessService.Gain(-11.73, 0.42));

        // A quiet dynamic recording: -28 LUFS wants +10 dB, but its peak at -3 dBTP allows only +2.
        Assert.Equal(2.0, MusicLoudnessService.Gain(-28, -3));

        // Without a peak reading the boost is still capped.
        Assert.Equal(MusicLoudnessService.MaxBoostDb, MusicLoudnessService.Gain(-40, null));
        Assert.Equal(-MusicLoudnessService.MaxCutDb, MusicLoudnessService.Gain(10, null));
        Assert.Equal(0, MusicLoudnessService.Gain(double.NegativeInfinity, null));
        Assert.Equal(0, MusicLoudnessService.Gain(-18, -1));
    }

    [Fact]
    public void AudioPiece_FadesOnlyAtRealTrackBoundaries()
    {
        var track = new MusicTrack { Id = Guid.NewGuid(), Path = "/music/a.mp3", RuntimeTicks = TimeSpan.FromSeconds(200).Ticks, Title = "A" };
        var start = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

        // The whole track inside one segment: both fades.
        var whole = WeatherChannelService.AudioPiece(new MusicPiece(track, TimeSpan.Zero, TimeSpan.FromSeconds(200), start), -3.2);
        Assert.True(whole.FadeIn);
        Assert.True(whole.FadeOut);
        Assert.Equal(-3.2, whole.GainDb);

        // Cut at the segment end: starts here, does not end here.
        var head = WeatherChannelService.AudioPiece(new MusicPiece(track, TimeSpan.Zero, TimeSpan.FromSeconds(120), start), 0);
        Assert.True(head.FadeIn);
        Assert.False(head.FadeOut);

        // The segment starts mid-track and the track ends inside it.
        var tail = WeatherChannelService.AudioPiece(new MusicPiece(track, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(80), start), 0);
        Assert.False(tail.FadeIn);
        Assert.True(tail.FadeOut);

        // A middle slice: no fades at all.
        var middle = WeatherChannelService.AudioPiece(new MusicPiece(track, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(100), start), 0);
        Assert.False(middle.FadeIn);
        Assert.False(middle.FadeOut);
    }
}
