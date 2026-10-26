using Jellyfin.Plugin.JellyStations.Models;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>What a source asks of each of its items: the audio language, and whether to burn subtitles.</summary>
/// <param name="AudioLanguage">Audio language code; empty = the file's default track.</param>
/// <param name="SubtitleLanguage">Subtitle language code to burn in; null = none.</param>
/// <param name="SubtitlesOnlyIfAudioMissing">Burn them only when the audio language was not found in the file.</param>
public sealed record TrackPreferences(string AudioLanguage, string? SubtitleLanguage = null, bool SubtitlesOnlyIfAudioMissing = false)
{
    /// <summary>A source's own choices, falling back to the station's audio language.</summary>
    public static TrackPreferences For(ContentSource source, string stationAudioLanguage) => new(
        string.IsNullOrWhiteSpace(source.AudioLanguage) ? stationAudioLanguage : source.AudioLanguage.Trim(),
        string.IsNullOrWhiteSpace(source.SubtitleLanguage) ? null : source.SubtitleLanguage.Trim(),
        source.SubtitlesOnlyIfAudioMissing);
}

/// <summary>The subtitle track to burn: an embedded stream (index among the file's subtitle streams) or an external file.</summary>
public sealed record SubtitlePick(int? StreamIndex, string? ExternalPath, bool IsBitmap);

/// <summary>Chooses the audio and subtitle tracks of a file from its probed streams.</summary>
public static class TrackPicker
{
    private static readonly string[] TextCodecs = { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "vtt", "text" };
    private static readonly string[] BitmapCodecs = { "pgssub", "hdmv_pgs_subtitle", "dvdsub", "dvd_subtitle", "dvbsub", "dvb_subtitle", "xsub" };

    /// <summary>
    /// Zero-based index among the audio streams for the language, else the default track; null when the file has at
    /// most one audio track (ffmpeg takes the first). <paramref name="matched"/> tells whether the language was found at all.
    /// </summary>
    public static int? PickAudio(IReadOnlyList<MediaStream> streams, string language, out bool matched)
    {
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderBy(s => s.Index).ToList();
        var idx = string.IsNullOrEmpty(language) ? -1 : audio.FindIndex(s => LanguageNames.Same(s.Language, language));
        matched = idx >= 0;
        if (audio.Count <= 1)
        {
            return null;
        }

        if (idx < 0)
        {
            idx = audio.FindIndex(s => s.IsDefault);
        }

        return idx < 0 ? null : idx;
    }

    /// <summary>
    /// The best subtitle track in the language: embedded text first (rendered by libass), then an external text file,
    /// then embedded bitmap subtitles (PGS, DVD: overlaid as pictures); full subtitles before forced-only and
    /// hearing-impaired ones. Null when the file has none in that language.
    /// </summary>
    public static SubtitlePick? PickSubtitle(IReadOnlyList<MediaStream> streams, string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return null;
        }

        var subs = streams.Where(s => s.Type == MediaStreamType.Subtitle).OrderBy(s => s.Index).ToList();
        var embedded = subs.Where(s => !s.IsExternal).ToList();
        var pick = subs
            .Where(s => LanguageNames.Same(s.Language, language) && (IsText(s) || (IsBitmap(s) && !s.IsExternal)))
            .OrderBy(s => IsBitmap(s) ? 2 : s.IsExternal ? 1 : 0)
            .ThenBy(s => s.IsForced ? 1 : 0)
            .ThenBy(s => s.IsHearingImpaired ? 1 : 0)
            .ThenByDescending(s => s.IsDefault)
            .FirstOrDefault();
        if (pick is null)
        {
            return null;
        }

        if (pick.IsExternal)
        {
            return string.IsNullOrEmpty(pick.Path) ? null : new SubtitlePick(null, pick.Path, false);
        }

        return new SubtitlePick(embedded.IndexOf(pick), null, IsBitmap(pick));
    }

    public static bool IsText(MediaStream s) => s.Codec is not null && TextCodecs.Contains(s.Codec.Trim().ToLowerInvariant());

    public static bool IsBitmap(MediaStream s) => s.Codec is not null && BitmapCodecs.Contains(s.Codec.Trim().ToLowerInvariant());
}
