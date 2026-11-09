using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class ContentChangesTests
{
    [Fact]
    public void Changes_CountsProgramsAddedAndRemoved_AcrossSections()
    {
        var a = Items.Movie("A", 60);
        var b = Items.Movie("B", 60);
        var c = Items.Movie("C", 60);
        var d = Items.Episode("Show", 1, 1, 30);
        var block = Guid.NewGuid();
        var previous = new CompiledStation { Pool = new() { a, b }, Filler = new() { c } };
        var current = new CompiledStation { Pool = new() { a }, Blocks = new() { [block] = new() { d } }, Filler = new() { c, Items.Movie("E", 90) } };

        var changes = StationCompiler.Changes(previous, current);

        Assert.Equal(2, changes.Added);
        Assert.Equal(1, changes.Removed);
        Assert.Equal(0, changes.ClipsAdded);
        Assert.Equal(0, changes.TracksAdded);
    }

    [Fact]
    public void Changes_CountsAnItemOnce_HoweverOftenItIsListed()
    {
        var a = Items.Movie("A", 60);
        var b = Items.Movie("B", 60);
        var block = Guid.NewGuid();
        var previous = new CompiledStation { Pool = new() { a } };
        var current = new CompiledStation { Pool = new() { a, b, b }, Blocks = new() { [block] = new() { a, b } } };

        var changes = StationCompiler.Changes(previous, current);

        Assert.Equal(1, changes.Added);
        Assert.Equal(0, changes.Removed);
    }

    [Fact]
    public void Changes_ReorderedContent_AddsAndRemovesNothing()
    {
        var a = Items.Movie("A", 60);
        var b = Items.Movie("B", 60);
        var previous = new CompiledStation { Pool = new() { a, b } };
        var current = new CompiledStation { Pool = new() { b, a } };

        var changes = StationCompiler.Changes(previous, current);

        Assert.False(StationCompiler.SameContent(previous, current));
        Assert.Equal(0, changes.Added);
        Assert.Equal(0, changes.Removed);
    }

    [Fact]
    public void Changes_CountsClipsAndMusicTracksApartFromPrograms()
    {
        var a = Items.Movie("A", 60);
        var ident = Items.Movie("Ident", 1);
        var promo = Items.Movie("Promo", 1);
        var kept = new MusicTrack { Id = Guid.NewGuid() };
        var dropped = new MusicTrack { Id = Guid.NewGuid() };
        var previous = new CompiledStation { Pool = new() { a }, Bumpers = new() { ident }, PreRoll = new() { ident }, Music = new() { kept, dropped } };
        var current = new CompiledStation
        {
            Pool = new() { a },
            Bumpers = new() { ident, promo },
            PreRoll = new() { ident, promo },
            Music = new() { kept, new MusicTrack { Id = Guid.NewGuid() }, new MusicTrack { Id = Guid.NewGuid() } }
        };

        var changes = StationCompiler.Changes(previous, current);

        Assert.Equal(0, changes.Added);
        Assert.Equal(1, changes.ClipsAdded);
        Assert.Equal(0, changes.ClipsRemoved);
        Assert.Equal(2, changes.TracksAdded);
        Assert.Equal(1, changes.TracksRemoved);
    }

    [Fact]
    public void Changes_FirstScan_CountsEverythingAsAdded()
    {
        var current = new CompiledStation { Pool = new() { Items.Movie("A", 60), Items.Movie("B", 60) }, Bumpers = new() { Items.Movie("Ident", 1) } };

        var changes = StationCompiler.Changes(null, current);

        Assert.Equal(2, changes.Added);
        Assert.Equal(1, changes.ClipsAdded);
        Assert.Equal(0, changes.Removed);
    }
}
