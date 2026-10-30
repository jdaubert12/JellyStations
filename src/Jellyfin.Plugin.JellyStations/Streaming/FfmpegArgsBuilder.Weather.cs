using System.Globalization;
using System.Text;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Weather.Screens;

namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>The weather channel's ffmpeg run: screens cross-faded on the clock, music, live clock and credit.</summary>
public static partial class FfmpegArgsBuilder
{
    /// <summary>
    /// Arguments that play one <see cref="WeatherSegment"/> as real-time MPEG-TS on stdout. Each screen PNG is
    /// turned into a timed stream inside the graph (loop + setpts + trim, the way the up next card is; a radar
    /// loop comes in as a concat-demuxer list of frames with durations and is filled to the frame rate instead),
    /// the screens are joined with xfade cross-fades at the screen boundaries, and drawtext adds the clock (ffmpeg's
    /// own time expansion with a per-place offset, so it ticks live) and the music credit. The music pieces are
    /// seeked and cut at the demuxer and concatenated; silence when there is none.
    /// </summary>
    public static List<string> ForWeather(WeatherSegment seg, TimeSpan tsOffset, EncodingProfile p)
    {
        if (seg.Slides.Count == 0)
        {
            throw new ArgumentException("A weather segment needs at least one screen.", nameof(seg));
        }

        var args = new List<string>();
        Common(args);
        HardwareInputArgs(args, p, decode: false);

        var fps = p.Fps.ToString(CultureInfo.InvariantCulture);
        foreach (var slide in seg.Slides)
        {
            if (slide.IsSequence)
            {
                args.Add("-f");
                args.Add("concat");
                args.Add("-safe");
                args.Add("0");
            }
            else
            {
                args.Add("-framerate");
                args.Add(fps);
            }

            args.Add("-i");
            args.Add(slide.Path);
        }

        var audioStart = seg.Slides.Count;
        var hasMusic = seg.Audio.Count > 0;
        if (hasMusic)
        {
            foreach (var piece in seg.Audio)
            {
                if (piece.InPoint > 0.01)
                {
                    args.Add("-ss");
                    args.Add(Sec(TimeSpan.FromSeconds(piece.InPoint)));
                }

                args.Add("-t");
                args.Add(Sec(TimeSpan.FromSeconds(piece.Seconds)));
                args.Add("-i");
                args.Add(piece.Path);
            }
        }
        else
        {
            AddSilenceInput(args, TimeSpan.FromSeconds(seg.TotalSeconds));
        }

        var filter = new StringBuilder();
        filter.Append(WeatherVideoGraph(seg, p));
        if (hasMusic)
        {
            filter.Append(';').Append(WeatherAudioGraph(seg, audioStart));
        }

        AddGraphStreams(args, p, filter.ToString(), "v", hasMusic ? "[a]" : audioStart.ToString(CultureInfo.InvariantCulture) + ":a");

        Output(args, p, tsOffset, audioFromGraph: hasMusic);
        return args;
    }

    /// <summary>The video half of the graph, ending in [v].</summary>
    public static string WeatherVideoGraph(WeatherSegment seg, EncodingProfile p)
    {
        var sb = new StringBuilder();
        var n = seg.Slides.Count;
        var fade = n > 1 ? WeatherSegment.FadeSeconds : 0;

        // Each screen as a stream of exactly its on-air length (plus the fade it hands the next screen).
        for (var i = 0; i < n; i++)
        {
            var length = seg.Slides[i].Seconds + (i < n - 1 ? fade : 0);
            var timing = seg.Slides[i].IsSequence ? "fps=" + p.Fps.ToString(CultureInfo.InvariantCulture) : "loop=loop=-1:size=1:start=0";
            sb.Append(CultureInfo.InvariantCulture, $"[{i}:v]scale={p.Width}:{p.Height}:force_original_aspect_ratio=decrease,pad={p.Width}:{p.Height}:(ow-iw)/2:(oh-ih)/2,format=yuv420p,{timing},settb=1/{p.Fps},setpts=N/({p.Fps}*TB),trim=end={N(length)}[s{i}];");
        }

        // Cross-fade each screen into the next at the boundary; the offsets are the cumulative screen lengths.
        var current = "s0";
        double offset = 0;
        for (var i = 1; i < n; i++)
        {
            offset += seg.Slides[i - 1].Seconds;
            sb.Append(CultureInfo.InvariantCulture, $"[{current}][s{i}]xfade=transition=fade:duration={N(fade)}:offset={N(offset)}[x{i}];");
            current = "x" + i;
        }

        // The live clock: one drawtext per distinct offset, switched on for the screens that use it.
        var scale = p.Height / 1080.0;
        var font = EscapeFilterPath(seg.FontPath);
        var clockFormat = Weather.Units.DrawtextClockFormat(seg.TwentyFourHourClock);
        double t = 0;
        var windows = new Dictionary<long, List<(double From, double To)>>();
        foreach (var slide in seg.Slides)
        {
            if (!windows.TryGetValue(slide.ClockOffsetSeconds, out var list))
            {
                windows[slide.ClockOffsetSeconds] = list = new List<(double, double)>();
            }

            list.Add((t, t + slide.Seconds));
            t += slide.Seconds;
        }

        var step = 0;
        foreach (var (clockOffset, list) in windows)
        {
            var enable = string.Join("+", Merge(list).Select(w => Between(w.From, w.To)));
            sb.Append(CultureInfo.InvariantCulture, $"[{current}]drawtext=fontfile={font}:fontsize={Px(WeatherPainter.ClockSize, scale)}:fontcolor=white:x={Px(WeatherPainter.ClockX, scale)}:y={Px(WeatherPainter.ClockY, scale)}:text='%{{pts\\:gmtime\\:{clockOffset}\\:{clockFormat}}}':enable='{enable}'[c{step}];");
            current = "c" + step;
            step++;
        }

        // The music credit, centred in the band, one drawtext per track.
        foreach (var credit in seg.Credits)
        {
            if (credit.To - credit.From < 0.5 || string.IsNullOrWhiteSpace(credit.Text))
            {
                continue;
            }

            sb.Append(CultureInfo.InvariantCulture, $"[{current}]drawtext=fontfile={font}:fontsize={Px(WeatherPainter.CreditSize, scale)}:fontcolor=white@0.85:x=(w-text_w)/2:y={Px(WeatherPainter.CreditY, scale)}:text='{EscapeDrawText(credit.Text)}':enable='{Between(credit.From, credit.To)}'[m{step}];");
            current = "m" + step;
            step++;
        }

        sb.Append(CultureInfo.InvariantCulture, $"[{current}]{SdrGraphToOutput(p)}{EncoderUploadSuffix(p)}[v]");
        return sb.ToString();
    }

    /// <summary>The fade at a real track boundary (out of one track, into the next).</summary>
    public const double TrackFadeSeconds = 0.6;

    /// <summary>The audio half of the graph: every piece resampled to 48 kHz stereo, levelled, concatenated and cut to the segment, ending in [a].</summary>
    public static string WeatherAudioGraph(WeatherSegment seg, int firstInput)
    {
        var sb = new StringBuilder();
        var volume = (Math.Clamp(seg.VolumePercent, 20, 150) / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
        for (var i = 0; i < seg.Audio.Count; i++)
        {
            var a = seg.Audio[i];
            sb.Append(CultureInfo.InvariantCulture, $"[{firstInput + i}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={volume},asetpts=PTS-STARTPTS");
            if (Math.Abs(a.GainDb) >= 0.05)
            {
                sb.Append(CultureInfo.InvariantCulture, $",volume={a.GainDb.ToString("0.0", CultureInfo.InvariantCulture)}dB");
            }

            if (a.FadeIn && a.Seconds > TrackFadeSeconds)
            {
                sb.Append(CultureInfo.InvariantCulture, $",afade=t=in:st=0:d={N(TrackFadeSeconds)}");
            }

            if (a.FadeOut && a.Seconds > TrackFadeSeconds)
            {
                sb.Append(CultureInfo.InvariantCulture, $",afade=t=out:st={N(a.Seconds - TrackFadeSeconds)}:d={N(TrackFadeSeconds)}");
            }

            sb.Append(CultureInfo.InvariantCulture, $"[a{i}];");
        }

        var inputs = string.Concat(Enumerable.Range(0, seg.Audio.Count).Select(i => "[a" + i + "]"));
        sb.Append(CultureInfo.InvariantCulture, $"{inputs}concat=n={seg.Audio.Count}:v=0:a=1[ac];[ac]apad,atrim=0:{N(seg.TotalSeconds)},asetpts=N/SR/TB[a]");
        return sb.ToString();
    }

    /// <summary>Joins touching windows so one drawtext covers consecutive screens of the same place.</summary>
    private static IEnumerable<(double From, double To)> Merge(List<(double From, double To)> windows)
    {
        var result = new List<(double From, double To)>();
        foreach (var w in windows.OrderBy(w => w.From))
        {
            if (result.Count > 0 && Math.Abs(result[^1].To - w.From) < 0.001)
            {
                result[^1] = (result[^1].From, w.To);
            }
            else
            {
                result.Add(w);
            }
        }

        return result;
    }

    private static string Px(float grid, double scale) => Math.Max(1, (int)Math.Round(grid * scale)).ToString(CultureInfo.InvariantCulture);

    /// <summary>A file path as a filter option value: colons and backslashes escaped, no quoting needed.</summary>
    internal static string EscapeFilterPath(string path)
        => path.Replace("\\", "/", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}
