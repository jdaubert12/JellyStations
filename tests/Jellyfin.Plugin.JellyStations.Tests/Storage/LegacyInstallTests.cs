using System.Text.Json;
using Jellyfin.Plugin.JellyStations.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Storage;

public class LegacyInstallTests : IDisposable
{
    private static readonly Guid Id = Guid.Parse("b7f3c1d2-5e8a-4f0b-9c6d-2a1e7f4b8d31");
    private readonly string _plugins = Path.Combine(Path.GetTempPath(), "JellyStationsTests", "plugins-" + Guid.NewGuid().ToString("N"));

    public LegacyInstallTests() => Directory.CreateDirectory(_plugins);

    private string Folder(string name, string version, Guid guid, string status = "Active")
    {
        var dir = Path.Combine(_plugins, $"{name}_{version}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new { guid = guid.ToString(), name, version, status, targetAbi = "12.0.0.0" }));
        return dir;
    }

    private static string StatusOf(string dir) => JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json"))).RootElement.GetProperty("status").GetString()!;

    [Fact]
    public void MarksOldNameWithSameId_LeavesEverythingElse()
    {
        var old = Folder("TvStation", "1.13.0.0", Id);
        var current = Folder("JellyStations", "1.15.0.0", Id);
        var olderCurrent = Folder("JellyStations", "1.14.9.0", Id);
        var other = Folder("Trakt", "20.0.0.0", Guid.NewGuid());
        var noMeta = Path.Combine(_plugins, "Stray_1.0.0.0");
        Directory.CreateDirectory(noMeta);

        var marked = LegacyInstall.MarkOtherNamesForDeletion(_plugins, Id, "JellyStations");

        Assert.Equal(new[] { old }, marked);
        Assert.Equal("Deleted", StatusOf(old));
        Assert.Equal("Active", StatusOf(current));
        Assert.Equal("Active", StatusOf(olderCurrent)); // same name: Jellyfin supersedes that one itself
        Assert.Equal("Active", StatusOf(other));
    }

    [Fact]
    public void SecondRun_FindsNothingNew()
    {
        var old = Folder("TvStation", "1.13.0.0", Id);
        LegacyInstall.MarkOtherNamesForDeletion(_plugins, Id, "JellyStations");
        Assert.Empty(LegacyInstall.MarkOtherNamesForDeletion(_plugins, Id, "JellyStations"));
        Assert.Equal("Deleted", StatusOf(old));
    }

    [Fact]
    public void MissingPluginsFolder_IsHarmless()
        => Assert.Empty(LegacyInstall.MarkOtherNamesForDeletion(Path.Combine(_plugins, "nope"), Id, "JellyStations"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_plugins, true);
        }
        catch (IOException)
        {
        }
    }
}
