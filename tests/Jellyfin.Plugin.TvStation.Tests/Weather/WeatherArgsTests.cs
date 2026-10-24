using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Streaming;
using Jellyfin.Plugin.TvStation.Weather;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Weather;

public class WeatherArgsTests
{
    private static readonly DateTime Start = new(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc);

    private static WeatherSegment Segment(bool music, int slides = 3)
    {
        var list = new List<WeatherSlide>();
        for (var i = 0; i < slides; i++)
        {
            list.Add(new WeatherSlide($"/data/weather/screens/s{i}.png", 10, 1761228000 + (i == 1 ? -21600 : -14400)));
        }

        return new WeatherSegment
        {
            StartUtc = Start,
            EndUtc = Start.AddSeconds(10 * slides),
            Slides = list,
            Audio = music ? new[] { new WeatherAudio("/music/a.mp3", 42.5, 18), new WeatherAudio("/music/b.flac", 0, 12) } : Array.Empty<WeatherAudio>(),
            Credits = music ? new[] { new WeatherCredit("Song One – Artist", 0, 18), new WeatherCredit("Song: Two", 18, 30) } : Array.Empty<WeatherCredit>(),
            FontPath = "/data/TvStation/assets/Inter-SemiBold.ttf",
            TwentyFourHourClock = false,
            VolumePercent = 80
        };
    }

    private static EncodingProfile Profile() => new() { Width = 1280, Height = 720, Fps = 30 };

    private static string Filter(List<string> args) => args[args.IndexOf("-filter_complex") + 1];

    [Fact]
    public void Slides_AreTimedStreams_CrossFadedAtBoundaries()
    {
        var args = FfmpegArgsBuilder.ForWeather(Segment(music: false), TimeSpan.FromSeconds(100), Profile());
        var f = Filter(args);

        Assert.Equal(3, args.Count(a => a == "-framerate"));
        Assert.Contains("[0:v]scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,format=yuv420p,loop=loop=-1:size=1:start=0,settb=1/30,setpts=N/(30*TB),trim=end=10.5[s0]", f);
        Assert.Contains("trim=end=10.5[s1]", f);
        Assert.Contains("trim=end=10[s2]", f); // the last screen has nothing to fade into
        Assert.Contains("[s0][s1]xfade=transition=fade:duration=0.5:offset=10[x1]", f);
        Assert.Contains("[x1][s2]xfade=transition=fade:duration=0.5:offset=20[x2]", f);
        Assert.EndsWith("format=yuv420p," + FfmpegArgsBuilder.SdrTags + "[v]", f);
        Assert.Contains("-output_ts_offset", args);
        Assert.Equal("100.000", args[args.IndexOf("-output_ts_offset") + 1]);
    }

    [Fact]
    public void Clock_IsDrawnPerOffset_WithMergedWindows()
    {
        var f = Filter(FfmpegArgsBuilder.ForWeather(Segment(music: false), TimeSpan.Zero, Profile()));

        // Screens 0 and 2 share an offset (two windows), screen 1 has its own.
        Assert.Contains(@"text='%{pts\:gmtime\:1761213600\:%-I\\\:%M %p}':enable='between(t,0,10)+between(t,20,30)'", f);
        Assert.Contains(@"text='%{pts\:gmtime\:1761206400\:%-I\\\:%M %p}':enable='between(t,10,20)'", f);
        Assert.Contains("fontfile=/data/TvStation/assets/Inter-SemiBold.ttf:fontsize=36:fontcolor=white:x=53:y=659", f); // 54, 80, 988 on the 1080 grid × 2/3
    }

    [Fact]
    public void NoMusic_UsesSilence_AndTheUsualAudioFilter()
    {
        var args = FfmpegArgsBuilder.ForWeather(Segment(music: false), TimeSpan.Zero, Profile());

        Assert.Contains("anullsrc=r=48000:cl=stereo", args);
        Assert.Equal("3:a", args[args.IndexOf("-map", args.IndexOf("[v]")) + 1]);
        Assert.Contains("-af", args);
        Assert.DoesNotContain("concat=", Filter(args));
    }

    [Fact]
    public void Music_IsSeekedCutLevelledAndConcatenated_WithCredits()
    {
        var args = FfmpegArgsBuilder.ForWeather(Segment(music: true), TimeSpan.Zero, Profile());
        var f = Filter(args);

        var a = args.IndexOf("/music/a.mp3");
        Assert.Equal("-ss", args[a - 5]);
        Assert.Equal("42.500", args[a - 4]);
        Assert.Equal("-t", args[a - 3]);
        Assert.Equal("18.000", args[a - 2]);
        var b = args.IndexOf("/music/b.flac");
        Assert.Equal("-t", args[b - 3]); // no -ss at the start of a track
        Assert.Contains("[3:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0.80,asetpts=PTS-STARTPTS[a0]", f);
        Assert.Contains("[a0][a1]concat=n=2:v=0:a=1[ac];[ac]apad,atrim=0:30,asetpts=N/SR/TB[a]", f);
        Assert.Equal("[a]", args[args.IndexOf("-map", args.IndexOf("[v]")) + 1]);
        Assert.DoesNotContain("-af", args); // the audio comes out of the graph
        Assert.Contains("text='Song One – Artist':enable='between(t,0,18)'", f);
        Assert.Contains(@"text='Song\: Two':enable='between(t,18,30)'", f);
    }

    [Fact]
    public void OneSlide_HasNoCrossFade()
    {
        var f = Filter(FfmpegArgsBuilder.ForWeather(Segment(music: false, slides: 1), TimeSpan.Zero, Profile()));
        Assert.DoesNotContain("xfade", f);
        Assert.Contains("trim=end=10[s0]", f);
        Assert.Contains("[s0]drawtext", f);
    }

    [Fact]
    public void Vaapi_UploadsTheComposedFrame()
    {
        var p = Profile();
        p.VideoEncoder = "h264_vaapi";
        var args = FfmpegArgsBuilder.ForWeather(Segment(music: false), TimeSpan.Zero, p);
        Assert.EndsWith(",format=nv12,hwupload[v]", Filter(args));
        Assert.DoesNotContain("-pix_fmt", args);
    }

    [Fact]
    public void CreditText_SaysNowPlaying_WithOrWithoutArtist()
    {
        Assert.Equal("Now playing: Local Forecast – Kevin MacLeod", WeatherChannelService.CreditText(new MusicTrack { Title = "Local Forecast", Artist = "Kevin MacLeod" }));
        Assert.Equal("Now playing: Local Forecast", WeatherChannelService.CreditText(new MusicTrack { Title = "Local Forecast", Artist = " " }));
    }
}
