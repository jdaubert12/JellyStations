using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

/// <summary>Per-source audio and subtitle choices: an anime series in Japanese with English subtitles, the rest of the station as it was.</summary>
public class TrackPickerTests
{
    private static MediaStream Audio(int index, string lang, bool isDefault = false) => new() { Type = MediaStreamType.Audio, Index = index, Language = lang, IsDefault = isDefault, Codec = "aac" };

    private static MediaStream Sub(int index, string lang, string codec = "subrip", bool forced = false, bool external = false, string? path = null, bool hearingImpaired = false, bool isDefault = false)
        => new() { Type = MediaStreamType.Subtitle, Index = index, Language = lang, Codec = codec, IsForced = forced, IsExternal = external, Path = path, IsHearingImpaired = hearingImpaired, IsDefault = isDefault };

    private static MediaStream Video() => new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" };

    [Fact]
    public void Audio_PicksTheLanguage_AndSaysWhetherItWasFound()
    {
        var streams = new[] { Video(), Audio(1, "jpn", isDefault: true), Audio(2, "eng") };

        Assert.Equal(1, TrackPicker.PickAudio(streams, "eng", out var matched));
        Assert.True(matched);
        Assert.Equal(0, TrackPicker.PickAudio(streams, "jpn", out matched));
        Assert.True(matched);
        Assert.Equal(0, TrackPicker.PickAudio(streams, "fra", out matched)); // falls back to the default track
        Assert.False(matched);
    }

    [Fact]
    public void Audio_SingleTrack_LeavesFfmpegToTheFirst_ButStillReportsTheMatch()
    {
        var streams = new[] { Video(), Audio(1, "jpn") };

        Assert.Null(TrackPicker.PickAudio(streams, "jpn", out var matched));
        Assert.True(matched);
        Assert.Null(TrackPicker.PickAudio(streams, "eng", out matched));
        Assert.False(matched);
    }

    [Fact]
    public void Audio_MatchesAcrossCodeSpellings()
    {
        var streams = new[] { Audio(1, "eng"), Audio(2, "ger") };

        Assert.Equal(1, TrackPicker.PickAudio(streams, "deu", out var matched));
        Assert.True(matched);
        Assert.Equal(1, TrackPicker.PickAudio(streams, "de", out _));
    }

    [Fact]
    public void Subtitles_PreferEmbeddedFullTextOverForcedExternalAndBitmap()
    {
        var streams = new[]
        {
            Video(),
            Audio(1, "jpn"),
            Sub(2, "eng", "pgssub"),
            Sub(3, "eng", "subrip", forced: true),
            Sub(4, "eng", "ass"),
            Sub(5, "eng", "subrip", external: true, path: "/media/show.eng.srt")
        };

        var pick = TrackPicker.PickSubtitle(streams, "eng");

        Assert.NotNull(pick);
        Assert.Equal(2, pick!.StreamIndex); // third subtitle stream of the file: pgs, forced srt, then this one
        Assert.Null(pick.ExternalPath);
        Assert.False(pick.IsBitmap);
    }

    [Fact]
    public void Subtitles_UseAnExternalFileBeforeBitmaps_AndBitmapsBeforeNothing()
    {
        var withExternal = new[] { Sub(2, "eng", "pgssub"), Sub(3, "eng", "subrip", external: true, path: "/media/show.eng.srt") };
        var external = TrackPicker.PickSubtitle(withExternal, "eng");
        Assert.Equal("/media/show.eng.srt", external!.ExternalPath);
        Assert.Null(external.StreamIndex);

        var onlyBitmap = new[] { Sub(2, "jpn", "subrip"), Sub(3, "eng", "pgssub") };
        var bitmap = TrackPicker.PickSubtitle(onlyBitmap, "eng");
        Assert.Equal(1, bitmap!.StreamIndex);
        Assert.True(bitmap.IsBitmap);
    }

    [Fact]
    public void Subtitles_NoneWhenOff_OrMissing_OrUnknownFormat()
    {
        var streams = new[] { Sub(2, "eng", "subrip"), Sub(3, "fra", "ttml") };

        Assert.Null(TrackPicker.PickSubtitle(streams, null));
        Assert.Null(TrackPicker.PickSubtitle(streams, "deu"));
        Assert.Null(TrackPicker.PickSubtitle(streams, "fra"));
    }

    [Fact]
    public void Preferences_FallBackToTheStationLanguage()
    {
        var plain = TrackPreferences.For(new ContentSource(), "eng");
        var anime = TrackPreferences.For(new ContentSource { AudioLanguage = " jpn ", SubtitleLanguage = "eng", SubtitlesOnlyIfAudioMissing = true }, "eng");

        Assert.Equal(new TrackPreferences("eng"), plain);
        Assert.Equal("jpn", anime.AudioLanguage);
        Assert.Equal("eng", anime.SubtitleLanguage);
        Assert.True(anime.SubtitlesOnlyIfAudioMissing);
    }

    [Theory]
    [InlineData("jpn", "jpn")]
    [InlineData("ja", "jpn")]
    [InlineData("ger", "deu")]
    [InlineData("DEU", "deu")]
    [InlineData("fre", "fra")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void LanguageCodes_Canonicalize(string? code, string expected)
    {
        Assert.Equal(expected, LanguageNames.Canonical(code));
    }

    [Fact]
    public void LanguageNames_AreReadable()
    {
        Assert.Equal("Japanese", LanguageNames.Describe("jpn"));
        Assert.Equal("English", LanguageNames.Describe("eng"));
        Assert.Equal("German", LanguageNames.Describe("ger"));
        Assert.Equal("Unknown", LanguageNames.Describe("und"));
        Assert.Equal("QQQ", LanguageNames.Describe("qqq"));
        Assert.True(LanguageNames.Same("ja", "JPN"));
        Assert.False(LanguageNames.Same(null, "jpn"));
    }
}
