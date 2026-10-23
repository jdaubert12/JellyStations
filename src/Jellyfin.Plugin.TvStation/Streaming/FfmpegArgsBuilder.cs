using System.Globalization;
using System.Text;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>
/// Builds ffmpeg argument lists for program and slate segments. Output is not paced by ffmpeg (-re);
/// the restreamer paces it from the TS PCR clock and back-pressures ffmpeg through the pipe.
/// Every segment is tagged BT.709 SDR; HDR sources are tone mapped so no PQ/HLG pixels or HDR
/// side data reach the stream (an HDR tag is what makes clients re-encode the channel).
/// </summary>
public static partial class FfmpegArgsBuilder
{
    /// <summary>The default DRM render node when a VA-API or QSV device is not configured anywhere.</summary>
    public const string DefaultDrmDevice = "/dev/dri/renderD128";

    /// <summary>A logo to draw over the video: image path plus placement.</summary>
    public sealed record LogoOverlayInput(string Path, LogoCorner Corner, int Opacity, int HeightPercent);

    /// <summary>A rendered "Up next" card (PNG with alpha) to draw in a corner during one or more windows of the segment, in seconds from its start.</summary>
    public sealed record UpNextCardInput(string Path, LogoCorner Corner, IReadOnlyList<(double From, double To)> Windows)
    {
        /// <summary>How long the fade or slide takes, at each end of every window.</summary>
        public const double AnimationSeconds = 0.4;

        public UpNextCardInput(string path, LogoCorner corner, double showFrom, double showTo)
            : this(path, corner, new[] { (showFrom, showTo) })
        {
        }

        public UpNextCardAnimation Animation { get; init; } = UpNextCardAnimation.None;

        public bool Fades => Animation is UpNextCardAnimation.Fade or UpNextCardAnimation.FadeAndSlide;

        public bool Slides => Animation is UpNextCardAnimation.Slide or UpNextCardAnimation.FadeAndSlide;

        /// <summary>Where the looped card stream ends when fading: the end of the last window, never past the program.</summary>
        public string CardStreamEnd => N(Windows.Max(w => w.To));

        /// <summary>The overlay filter's enable expression: on while t is inside any window.</summary>
        public string EnableExpression => string.Join("+", Windows.Select(w => "between(t," + N(w.From) + "," + N(w.To) + ")"));

        /// <summary>
        /// 0 while the card is fully in place, rising to 1 at the very start and end of each window: the fraction of
        /// the card still off screen. Evaluated per frame by the overlay filter (its x expression).
        /// </summary>
        public string SlideExpression => string.Join("+", Windows.Select(w =>
            "between(t," + N(w.From) + "," + N(w.To) + ")*max(max(0,1-(t-" + N(w.From) + ")/" + N(AnimationSeconds) + "),max(0,1-(" + N(w.To) + "-t)/" + N(AnimationSeconds) + "))"));

        /// <summary>
        /// sendcmd commands that drive the card's alpha (a colorchannelmixer instance named "fade"): up over the first
        /// AnimationSeconds of each window, down over the last. TI is sendcmd's 0..1 position inside the interval.
        /// </summary>
        public string FadeCommands => string.Join(";", Windows.Select(w =>
            N(w.From) + "-" + N(w.From + AnimationSeconds) + " [expr] colorchannelmixer@fade aa TI, [leave] colorchannelmixer@fade aa 1;"
            + N(w.To - AnimationSeconds) + "-" + N(w.To) + " [expr] colorchannelmixer@fade aa 1-TI, [leave] colorchannelmixer@fade aa 0"));

        private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Arguments to play an item's (first) file from an offset, normalized to the profile, as real-time MPEG-TS on stdout.</summary>
    public static List<string> ForFile(PlayableItem item, TimeSpan seek, TimeSpan? duration, TimeSpan tsOffset, EncodingProfile p, LogoOverlayInput? overlay = null, UpNextCardInput? upNext = null)
        => ForSource(item.Path, item.HasAudio, item.AudioStreamIndex, item.HdrTransfer, seek, duration, tsOffset, p, overlay, item.VideoCodec, upNext);

    /// <summary>Arguments to play one file (an item, or one part of a multi-part item) from an offset.</summary>
    /// <param name="hdrTransfer">The source's HDR transfer ("smpte2084" or "arib-std-b67") to tone map from; null or empty for SDR.</param>
    /// <param name="videoCodec">The file's video codec as Jellyfin names it (h264, hevc, ...): QSV decoders are chosen per codec.</param>
    /// <param name="upNext">An "Up next" card to draw near the end of the segment, or null.</param>
    public static List<string> ForSource(string path, bool hasAudio, int? audioStreamIndex, string? hdrTransfer, TimeSpan seek, TimeSpan? duration, TimeSpan tsOffset, EncodingProfile p, LogoOverlayInput? overlay = null, string? videoCodec = null, UpNextCardInput? upNext = null)
    {
        var args = new List<string>();
        Common(args);
        HardwareInputArgs(args, p, decode: true, videoCodec);
        AddSplit(args, p.ExtraInputArgs);

        if (seek > TimeSpan.FromMilliseconds(500))
        {
            args.Add("-ss");
            args.Add(Sec(seek));
        }

        args.Add("-i");
        args.Add(path);

        var needSilence = !hasAudio;
        if (needSilence)
        {
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add("anullsrc=r=48000:cl=stereo");
        }

        var nextIndex = needSilence ? 2 : 1;
        var logoIndex = -1;
        if (overlay is not null)
        {
            // A single-frame image; the overlay filter repeats its last frame for the whole program.
            logoIndex = nextIndex++;
            args.Add("-i");
            args.Add(overlay.Path);
        }

        var cardIndex = -1;
        if (upNext is not null)
        {
            cardIndex = nextIndex++;
            args.Add("-i");
            args.Add(upNext.Path);
        }

        if (duration.HasValue && duration.Value > TimeSpan.Zero)
        {
            args.Add("-t");
            args.Add(Sec(duration.Value));
        }

        if (needSilence)
        {
            args.Add("-shortest");
        }

        var audioMap = needSilence ? "1:a:0" : "0:a:" + (audioStreamIndex ?? 0).ToString(CultureInfo.InvariantCulture) + "?";
        if (overlay is null && upNext is null)
        {
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add(audioMap);
            args.Add("-vf");
            args.Add(VideoFilter(p, hdrTransfer) + EncoderUploadSuffix(p));
        }
        else
        {
            args.Add("-filter_complex");
            args.Add(ComposeFilter(p, hdrTransfer, overlay, logoIndex, upNext, cardIndex));
            args.Add("-map");
            args.Add("[v]");
            args.Add("-map");
            args.Add(audioMap);
            if (!p.IsVaapiEncoder)
            {
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }
        }

        Output(args, p, tsOffset);
        return args;
    }

    /// <summary>Normalized video with the logo scaled, faded and pinned to a corner with a small margin.</summary>
    public static string OverlayFilter(EncodingProfile p, LogoOverlayInput overlay, int logoIndex, string? hdrTransfer = null)
        => ComposeFilter(p, hdrTransfer, overlay, logoIndex, null, -1);

    /// <summary>
    /// Normalized video with, in order, the logo bug and the "Up next" card drawn over it. The card is a finished
    /// image: it only needs placing and switching on for its window (the overlay filter's enable expression, in
    /// seconds since the segment started; -output_ts_offset is applied later, at the muxer).
    /// </summary>
    public static string ComposeFilter(EncodingProfile p, string? hdrTransfer, LogoOverlayInput? overlay, int logoIndex, UpNextCardInput? upNext, int cardIndex)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[0:v]{VideoFilter(p, hdrTransfer)}[base]");
        var current = "base";
        if (overlay is not null)
        {
            var height = Math.Max(8, p.Height * Math.Clamp(overlay.HeightPercent, 3, 25) / 100);
            var alpha = (Math.Clamp(overlay.Opacity, 10, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            sb.Append(CultureInfo.InvariantCulture, $";[{logoIndex}:v]scale=-1:{height},format=rgba,colorchannelmixer=aa={alpha}[logo];[{current}][logo]overlay={CornerPosition(p, overlay.Corner)}:format=yuv420[ov1]");
            current = "ov1";
        }

        if (upNext is not null)
        {
            // Fade: the still is turned into a frame-per-tick stream inside the graph (loop + setpts, ended by trim
            // just after the last window so the stream does not outlive the program) and sendcmd steers its alpha
            // frame by frame. Looping the PNG as a second demuxed input (-loop 1) instead drew the card seconds
            // late on the live stream, though never in a standalone run, so the card never leaves the graph.
            // Slide: the overlay's x expression parks the card off the nearest side edge and brings it in. The
            // enable window still gates both.
            var cardChain = upNext.Fades
                ? $"format=rgba,loop=loop=-1:size=1:start=0,settb=1/{p.Fps},setpts=N/({p.Fps}*TB),trim=end={upNext.CardStreamEnd},sendcmd=c='{upNext.FadeCommands}',colorchannelmixer@fade=aa=0"
                : "format=rgba";
            var position = upNext.Slides ? SlidingPosition(p, upNext) : CornerPosition(p, upNext.Corner);
            sb.Append(CultureInfo.InvariantCulture, $";[{cardIndex}:v]{cardChain}[card];[{current}][card]overlay={position}:format=yuv420:enable='{upNext.EnableExpression}'[ov2]");
            current = "ov2";
        }

        // A VA-API encoder needs the composed frame on the GPU; everyone else takes it as is.
        var graph = sb.ToString();
        var last = graph.LastIndexOf("[" + current + "]", StringComparison.Ordinal);
        graph = graph[..last];
        return p.IsVaapiEncoder ? graph + "[ov];[ov]" + EncoderUploadSuffix(p).TrimStart(',') + "[v]" : graph + "[v]";
    }

    /// <summary>overlay x:y for a corner, with a small margin that scales with the picture.</summary>
    private static string CornerPosition(EncodingProfile p, LogoCorner corner)
    {
        var margin = Margin(p);
        return corner switch
        {
            LogoCorner.TopLeft => $"{margin}:{margin}",
            LogoCorner.BottomLeft => $"{margin}:H-h-{margin}",
            LogoCorner.BottomRight => $"W-w-{margin}:H-h-{margin}",
            _ => $"W-w-{margin}:{margin}"
        };
    }

    /// <summary>The corner position with x pushed off the nearest side edge by the card's slide fraction.</summary>
    private static string SlidingPosition(EncodingProfile p, UpNextCardInput card)
    {
        var margin = Margin(p);
        var left = card.Corner is LogoCorner.TopLeft or LogoCorner.BottomLeft;
        var x = left
            ? $"'{margin}-(w+{margin})*({card.SlideExpression})'"
            : $"'W-w-{margin}+(w+{margin})*({card.SlideExpression})'";
        var y = card.Corner is LogoCorner.TopLeft or LogoCorner.TopRight ? margin.ToString(CultureInfo.InvariantCulture) : $"H-h-{margin}";
        return x + ":" + y;
    }

    private static int Margin(EncodingProfile p) => Math.Max(4, p.Height / 36);

    /// <summary>
    /// Arguments that render one "Up next" card to a PNG with alpha: a translucent dark panel, the next program's
    /// artwork on the left and three or four lines of text, drawn by ffmpeg itself (drawtext, as the slate uses) so
    /// no image library is needed. Runs for a fraction of a second, once per program.
    /// </summary>
    public static List<string> ForUpNextCard(UpNextCardSpec card, string outputPath)
    {
        var args = new List<string>();
        Common(args, "error");
        args.Add("-y");
        var alpha = (card.Opacity / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
        args.Add("-f");
        args.Add("lavfi");
        args.Add("-i");
        args.Add(string.Create(CultureInfo.InvariantCulture, $"color=c=black@{alpha}:s={card.Width}x{card.Height}:r=1:d=1"));
        var withThumb = card.ThumbnailPath is not null;
        if (withThumb)
        {
            args.Add("-i");
            args.Add(card.ThumbnailPath!);
        }

        var h = card.Height;
        var pad = UpNextCard.Pad(h);
        var textX = UpNextCard.TextX(h, withThumb);
        var sb = new StringBuilder();
        sb.Append("[0:v]format=rgba[bg]");
        var current = "bg";
        if (withThumb)
        {
            var boxW = UpNextCard.ThumbBoxWidth(h);
            var boxH = h - 2 * pad;
            sb.Append(CultureInfo.InvariantCulture, $";[1:v]scale={boxW}:{boxH}:force_original_aspect_ratio=decrease,format=rgba[thumb];[bg][thumb]overlay={pad}+({boxW}-w)/2:{pad}+({boxH}-h)/2[c0]");
            current = "c0";
        }

        var y = pad;
        var n = 1;
        void Line(string text, int size, string color)
        {
            sb.Append(CultureInfo.InvariantCulture, $";[{current}]drawtext=text='{EscapeDrawText(text)}':x={textX}:y={y}:fontsize={size}:fontcolor={color}[c{n}]");
            current = "c" + n;
            n++;
            y += size * 5 / 4;
        }

        Line(card.Label, UpNextCard.LabelSize(h), "white@0.75");
        Line(card.Title, UpNextCard.TitleSize(h), "white");
        if (!string.IsNullOrEmpty(card.Subtitle))
        {
            Line(card.Subtitle, UpNextCard.SmallSize(h), "white@0.85");
        }

        if (!string.IsNullOrEmpty(card.Times))
        {
            Line(card.Times, UpNextCard.SmallSize(h), "white@0.85");
        }

        args.Add("-filter_complex");
        args.Add(sb.ToString());
        args.Add("-map");
        args.Add("[" + current + "]");
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-update");
        args.Add("1");
        args.Add("-pix_fmt");
        args.Add("rgba");
        args.Add("-f");
        args.Add("image2");
        args.Add(outputPath);
        return args;
    }

    /// <summary>Arguments for a generated slate (color card with optional text and logo).</summary>
    public static List<string> ForSlate(TimeSpan duration, TimeSpan tsOffset, EncodingProfile p, string background, string? text, string? logoPath)
    {
        var args = new List<string>();
        Common(args);
        HardwareInputArgs(args, p, decode: false);

        args.Add("-f");
        args.Add("lavfi");
        args.Add("-i");
        args.Add($"color=c={NormalizeColor(background)}:s={p.Width}x{p.Height}:r={p.Fps}");
        args.Add("-f");
        args.Add("lavfi");
        args.Add("-i");
        args.Add("anullsrc=r=48000:cl=stereo");

        var hasLogo = !string.IsNullOrEmpty(logoPath) && File.Exists(logoPath);
        if (hasLogo)
        {
            args.Add("-loop");
            args.Add("1");
            args.Add("-i");
            args.Add(logoPath!);
        }

        args.Add("-t");
        args.Add(Sec(duration));

        var filter = new StringBuilder();
        var videoLabel = "0:v";
        if (hasLogo)
        {
            filter.Append(CultureInfo.InvariantCulture, $"[2:v]scale=-1:{p.Height / 4}[logo];[0:v][logo]overlay=(W-w)/2:(H-h)/2-{p.Height / 10}[v0]");
            videoLabel = "v0";
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            if (filter.Length > 0)
            {
                filter.Append(';');
            }

            var y = hasLogo ? "(h*0.62)" : "(h-text_h)/2";
            filter.Append(CultureInfo.InvariantCulture, $"[{videoLabel}]drawtext=text='{EscapeDrawText(text)}':fontcolor=white:fontsize={Math.Max(16, p.Height / 18)}:x=(w-text_w)/2:y={y}[v1]");
            videoLabel = "v1";
        }

        if (filter.Length > 0)
        {
            filter.Append(CultureInfo.InvariantCulture, $";[{videoLabel}]format=yuv420p,{SdrTags}{EncoderUploadSuffix(p)}[v2]");
            videoLabel = "v2";
        }

        if (filter.Length > 0)
        {
            args.Add("-filter_complex");
            args.Add(filter.ToString());
            args.Add("-map");
            args.Add("[" + videoLabel + "]");
        }
        else
        {
            args.Add("-map");
            args.Add("0:v");
        }

        args.Add("-map");
        args.Add("1:a");

        if (filter.Length == 0)
        {
            args.Add("-vf");
            args.Add("format=yuv420p," + SdrTags + EncoderUploadSuffix(p));
        }
        else if (!p.IsVaapiEncoder)
        {
            args.Add("-pix_fmt");
            args.Add("yuv420p");
        }

        Output(args, p, tsOffset);
        return args;
    }

    /// <summary>
    /// Hardware setup before the input: the decoder (<see cref="EncodingProfile.HardwareDecode"/>, already resolved
    /// from "auto") and, for a VA-API encoder, the device its frames are uploaded to. Decoded frames stay in system
    /// memory so the software filter chain applies unchanged. VA-API, CUDA, D3D11VA and VideoToolbox are hwaccels on
    /// the native decoders (no -hwaccel_output_format: ffmpeg downloads the frames, and falls back to software when
    /// the accelerator cannot take a file). QSV is different: "-hwaccel qsv" picks the <c>*_qsv</c> decoders and, in
    /// ffmpeg 7, leaves their output on GPU surfaces the software filters cannot read ("Impossible to convert between
    /// the formats"). So the <c>*_qsv</c> decoder is named outright for the file's codec, on a QSV device created up
    /// front, which makes it decode to system memory. That decoder never falls back to software, which is why
    /// <see cref="HardwareDecodePolicy"/> screens each file first.
    /// </summary>
    /// <param name="videoCodec">The file's video codec as Jellyfin names it; only QSV needs it.</param>
    public static void HardwareInputArgs(List<string> args, EncodingProfile p, bool decode, string? videoCodec = null)
    {
        var kind = decode ? (p.HardwareDecode ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
        var device = (p.HardwareDevice ?? string.Empty).Trim();
        var drm = device.Length > 0 ? device : DefaultDrmDevice;

        switch (kind)
        {
            case "qsv":
                if (device.Length > 0)
                {
                    // Creates the QSV device "__qsv_device" on that DRM node (Linux) or adapter index (Windows).
                    args.Add("-qsv_device");
                    args.Add(device);
                }
                else
                {
                    args.Add("-init_hw_device");
                    args.Add("qsv=qs");
                }

                // The decoder finds the one QSV device by type; the h264_qsv encoder shares it.
                var decoder = QsvDecoder(videoCodec);
                if (decoder is not null)
                {
                    args.Add("-c:v");
                    args.Add(decoder);
                }

                break;
            case "vaapi":
                args.Add("-vaapi_device");
                args.Add(drm);
                args.Add("-hwaccel");
                args.Add("vaapi");
                break;
            case "cuda":
            case "d3d11va":
                args.Add("-hwaccel");
                args.Add(kind);
                if (device.Length > 0)
                {
                    args.Add("-hwaccel_device");
                    args.Add(device);
                }

                break;
            case "videotoolbox":
                args.Add("-hwaccel");
                args.Add("videotoolbox");
                break;
            default:
                break;
        }

        if (p.IsVaapiEncoder && kind != "vaapi")
        {
            args.Add("-vaapi_device");
            args.Add(drm);
        }
    }

    /// <summary>The Intel Quick Sync decoder for a codec as Jellyfin names it, or null when there is none (or the codec is unknown).</summary>
    public static string? QsvDecoder(string? videoCodec) => HardwareDecodePolicy.NormalizeCodec(videoCodec) switch
    {
        "h264" => "h264_qsv",
        "hevc" => "hevc_qsv",
        "mpeg2video" => "mpeg2_qsv",
        "vc1" => "vc1_qsv",
        "vp8" => "vp8_qsv",
        "vp9" => "vp9_qsv",
        "av1" => "av1_qsv",
        _ => null
    };

    private static void Common(List<string> args, string loglevel = "warning")
    {
        args.AddRange(new[] { "-hide_banner", "-loglevel", loglevel, "-nostdin", "-nostats" });
    }

    /// <summary>
    /// A hardware check: decode, filter and encode <paramref name="seconds"/> of a file exactly as a program segment
    /// would be, as fast as the machine can, with ffmpeg at verbose level so it names the decoder it attached, and no
    /// output but ffmpeg's own final statistics (speed=... tells how far from real time the machine is).
    /// </summary>
    public static List<string> ForProbe(string path, string? hdrTransfer, EncodingProfile p, int seconds, TimeSpan seek, string? videoCodec = null)
    {
        var args = new List<string>();
        Common(args, "verbose");
        HardwareInputArgs(args, p, decode: true, videoCodec);
        AddSplit(args, p.ExtraInputArgs);
        if (seek > TimeSpan.FromMilliseconds(500))
        {
            args.Add("-ss");
            args.Add(Sec(seek));
        }

        args.Add("-i");
        args.Add(path);
        args.Add("-t");
        args.Add(Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture));
        args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");
        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-vf");
        args.Add(VideoFilter(p, hdrTransfer) + EncoderUploadSuffix(p));
        VideoCodec(args, p);
        args.Add("-f");
        args.Add("null");
        args.Add("-");
        return args;
    }

    /// <summary>Frame tags every segment leaves the graph with. ffmpeg 7+ encoders take color metadata from the frames, not from -color_* options.</summary>
    public const string SdrTags = "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv";

    /// <summary>
    /// Scale (letterboxed) to the profile, constant frame rate, 8-bit 4:2:0, tagged BT.709. An HDR source is first
    /// tone mapped to BT.709 (the zscale/tonemap chain Jellyfin uses for software tone mapping) and stripped of its
    /// HDR side data. The HDR input properties are stated outright so zscale does not depend on the file's tags.
    /// </summary>
    public static string VideoFilter(EncodingProfile p, string? hdrTransfer = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"scale={p.Width}:{p.Height}:force_original_aspect_ratio=decrease:flags=bicubic");
        if (!string.IsNullOrEmpty(hdrTransfer))
        {
            sb.Append(CultureInfo.InvariantCulture, $",setparams=color_primaries=bt2020:color_trc={hdrTransfer}:colorspace=bt2020nc");
            sb.Append(",format=yuv420p10le,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0:peak=100,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,sidedata=mode=delete");
        }

        sb.Append(CultureInfo.InvariantCulture, $",pad={p.Width}:{p.Height}:(ow-iw)/2:(oh-ih)/2,fps={p.Fps},format=yuv420p,{SdrTags}");
        return sb.ToString();
    }

    /// <summary>Filters that hand the finished frame to the encoder: VA-API encoders only take frames already on the GPU.</summary>
    private static string EncoderUploadSuffix(EncodingProfile p) => p.IsVaapiEncoder ? ",format=nv12,hwupload" : string.Empty;

    /// <summary>The video encoder and its rate control, shared by program segments and the hardware check.</summary>
    private static void VideoCodec(List<string> args, EncodingProfile p)
    {
        var gop = Math.Max(1, p.Fps * Math.Max(1, p.GopSeconds));
        args.Add("-c:v");
        args.Add(p.VideoEncoder);
        if (string.Equals(p.VideoEncoder, "libx264", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-preset");
            args.Add(p.Preset);
            args.Add("-profile:v");
            args.Add(p.H264Profile);
            args.Add("-level");
            args.Add(p.H264Level);
            args.Add("-x264-params");
            args.Add("scenecut=0:open-gop=0");
        }

        // Always declare SDR BT.709: the filter chain delivers that, and a leaked BT.2020/PQ tag makes clients transcode.
        args.Add("-color_primaries");
        args.Add("bt709");
        args.Add("-color_trc");
        args.Add("bt709");
        args.Add("-colorspace");
        args.Add("bt709");
        args.Add("-color_range");
        args.Add("tv");

        args.Add("-b:v");
        args.Add(p.VideoBitrateKbps + "k");
        args.Add("-maxrate");
        args.Add((int)(p.VideoBitrateKbps * 1.1) + "k");
        args.Add("-bufsize");
        args.Add(p.VideoBitrateKbps * 2 + "k");
        // One IDR frame, with SPS/PPS, every GopSeconds: that is where a client joining mid-program, or Jellyfin's
        // own remux of the channel, can start. libx264 and the VA-API encoders honour -force_key_frames with IDR
        // frames; QSV, NVENC and AMF turn a forced key frame into a plain I frame without headers that no demuxer
        // treats as a key frame (Jellyfin: "Unable to force key frames using these encoders, set key frames by
        // GOP"), so they get the GOP only, whose I frames are IDR.
        args.Add("-g");
        args.Add(gop.ToString(CultureInfo.InvariantCulture));
        args.Add("-keyint_min");
        args.Add(gop.ToString(CultureInfo.InvariantCulture));
        if (ForcedKeyFramesAreIdr(p.VideoEncoder))
        {
            args.Add("-force_key_frames");
            args.Add("expr:gte(t,n_forced*" + Math.Max(1, p.GopSeconds).ToString(CultureInfo.InvariantCulture) + ")");
        }

        if (string.Equals(p.VideoEncoder, "libx264", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-sc_threshold");
            args.Add("0");
        }
    }

    /// <summary>True for the encoders whose forced key frames are IDR frames with headers (libx264 and VA-API); false for QSV, NVENC and AMF.</summary>
    public static bool ForcedKeyFramesAreIdr(string encoder) =>
        encoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase) || encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase);

    /// <param name="audioFromGraph">True when the audio stream comes out of -filter_complex, which rules out -af.</param>
    private static void Output(List<string> args, EncodingProfile p, TimeSpan tsOffset, bool audioFromGraph = false)
    {
        VideoCodec(args, p);

        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add(p.AudioBitrateKbps + "k");
        args.Add("-ar");
        args.Add("48000");
        args.Add("-ac");
        args.Add("2");
        if (!audioFromGraph)
        {
            args.Add("-af");
            args.Add("aresample=async=1:first_pts=0");
        }

        args.Add("-sn");
        args.Add("-dn");
        args.Add("-map_metadata");
        args.Add("-1");
        args.Add("-map_chapters");
        args.Add("-1");

        args.Add("-output_ts_offset");
        args.Add(Sec(tsOffset));
        args.Add("-muxdelay");
        args.Add("0");
        args.Add("-muxpreload");
        args.Add("0");
        args.Add("-mpegts_flags");
        args.Add("+initial_discontinuity");
        args.Add("-streamid");
        args.Add("0:256");
        args.Add("-streamid");
        args.Add("1:257");
        args.Add("-f");
        args.Add("mpegts");

        AddSplit(args, p.ExtraOutputArgs);
        args.Add("pipe:1");
    }

    private static void AddSplit(List<string> args, string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            return;
        }

        args.AddRange(SplitArgs(extra));
    }

    /// <summary>Splits a command-line fragment honoring single and double quotes.</summary>
    public static IEnumerable<string> SplitArgs(string input)
    {
        var current = new StringBuilder();
        char? quote = null;
        foreach (var ch in input)
        {
            if (quote.HasValue)
            {
                if (ch == quote.Value)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"' || ch == '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

    private static string NormalizeColor(string c)
    {
        if (string.IsNullOrWhiteSpace(c))
        {
            return "0x101010";
        }

        return c.StartsWith('#') ? "0x" + c[1..] : c;
    }

    internal static string EscapeDrawText(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "’", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal);
    }

    /// <summary>Quotes args for logging only.</summary>
    public static string ToLogString(IEnumerable<string> args)
    {
        return string.Join(' ', args.Select(a => a.Any(char.IsWhiteSpace) || a.Contains('\'') ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : a));
    }
}
