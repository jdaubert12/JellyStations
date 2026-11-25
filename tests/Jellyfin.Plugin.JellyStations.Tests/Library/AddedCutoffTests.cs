using System.Text.Json;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

/// <summary>The plugin-wide "Hide shows and movies added after" date.</summary>
public class AddedCutoffTests
{
    private static readonly DateOnly Day = new(2026, 11, 20);
    private static readonly AddedCutoff Cutoff = AddedCutoff.For(Day, TimeZoneInfo.Utc)!;

    private static DateTime Utc(int day, int hour = 12, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Utc);

    private static bool Hides(BaseItem item, BaseItem? series = null) => Cutoff.Hides(item, _ => series);

    [Fact]
    public void NoDate_IsNoCutoff()
    {
        Assert.Null(AddedCutoff.For(null));
        Assert.Null(AddedCutoff.For(DateOnly.MaxValue));
    }

    [Fact]
    public void Movies_GoByTheirOwnDate_AndTheDayItselfPlays()
    {
        Assert.False(Hides(new Movie { DateCreated = Utc(19) }));
        Assert.False(Hides(new Movie { DateCreated = Utc(20, 23, 59) }));
        Assert.True(Hides(new Movie { DateCreated = Utc(21, 0, 0) }));
        Assert.True(Hides(new Movie { DateCreated = DateTime.SpecifyKind(Utc(21), DateTimeKind.Unspecified) }));
        Assert.False(Hides(new Movie()), "a movie with no date plays");
    }

    [Fact]
    public void Episodes_GoByTheirShow()
    {
        var oldShow = new Series { DateCreated = Utc(1) };
        var newShow = new Series { DateCreated = Utc(22) };

        Assert.False(Hides(new Episode { DateCreated = Utc(24) }, oldShow), "a new episode of an old show plays");
        Assert.True(Hides(new Episode { DateCreated = Utc(24) }, newShow));
        Assert.True(Hides(new Episode { DateCreated = Utc(24) }), "with no show to go by, the episode's own date");
        Assert.False(Hides(new Episode { DateCreated = Utc(2) }));
    }

    [Fact]
    public void Clips_AreNeverHidden()
    {
        Assert.False(Hides(new Video { DateCreated = Utc(24) }));
    }

    [Fact]
    public void TheDayEndsInTheServersTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "test", "test");
        var cutoff = AddedCutoff.For(Day, zone)!;

        // 04:59 UTC on the 21st is still 23:59 on the 20th there.
        Assert.False(cutoff.Hides(new Movie { DateCreated = Utc(21, 4, 59) }, _ => null));
        Assert.True(cutoff.Hides(new Movie { DateCreated = Utc(21, 5, 0) }, _ => null));
    }

    [Fact]
    public void ADayThatStartsWithAClockChange_IsStillADay()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 21);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 12, 21);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-dst", TimeSpan.Zero, "test", "test", "test dst", new[] { rule });

        var cutoff = AddedCutoff.For(Day, zone)!;

        Assert.False(cutoff.Hides(new Movie { DateCreated = Utc(20, 23, 59) }, _ => null));
        Assert.True(cutoff.Hides(new Movie { DateCreated = Utc(21, 0, 0) }, _ => null));
    }

    [Theory]
    [InlineData(SourceKind.Genre, true)]
    [InlineData(SourceKind.Tag, true)]
    [InlineData(SourceKind.Library, true)]
    [InlineData(SourceKind.BoxSet, true)]
    [InlineData(SourceKind.Playlist, true)]
    [InlineData(SourceKind.Series, false)]
    [InlineData(SourceKind.Season, false)]
    [InlineData(SourceKind.Movie, false)]
    [InlineData(SourceKind.Episode, false)]
    [InlineData(SourceKind.Folder, false)]
    public void OnlySweepingSourcesAreFiltered(SourceKind kind, bool filtered)
    {
        Assert.Equal(filtered, AddedCutoff.Filters(new ContentSource { Kind = kind }));
    }

    [Fact]
    public void WhatAnotherSourceNames_IsNotReportedAsHidden()
    {
        PlayableItem named = new() { ItemId = Guid.NewGuid() }, hidden = new() { ItemId = Guid.NewGuid() };
        var compiled = new CompiledStation { Pool = { named }, AddedExcluded = { named, hidden, hidden } };
        foreach (var item in compiled.AddedExcluded)
        {
            compiled.Excluded.Add(new ExcludedItem { ItemId = item.ItemId, Reason = ExclusionReason.AddedAfterCutoff });
        }

        compiled.Excluded.Add(new ExcludedItem { ItemId = named.ItemId, Reason = ExclusionReason.TagExcluded });

        StationCompiler.KeepNamed(compiled);

        Assert.Equal(new[] { hidden.ItemId }, compiled.AddedExcluded.Select(i => i.ItemId));
        Assert.DoesNotContain(compiled.Excluded, e => e.Reason == ExclusionReason.AddedAfterCutoff && e.ItemId == named.ItemId);
        Assert.Contains(compiled.Excluded, e => e.Reason == ExclusionReason.TagExcluded);
    }

    [Fact]
    public void TheSetting_IsAPlainDate_AndOldSettingsHaveNone()
    {
        var json = JsonSerializer.Serialize(new GlobalSettings { HideAddedAfter = Day }, JsonFileStore.Options);

        Assert.Contains("\"hideAddedAfter\": \"2026-11-20\"", json);
        Assert.Equal(Day, JsonSerializer.Deserialize<GlobalSettings>(json, JsonFileStore.Options)!.HideAddedAfter);
        Assert.Null(JsonSerializer.Deserialize<GlobalSettings>("{}", JsonFileStore.Options)!.HideAddedAfter);
    }
}
