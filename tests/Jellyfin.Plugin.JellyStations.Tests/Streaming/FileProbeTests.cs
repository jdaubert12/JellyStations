using Jellyfin.Plugin.JellyStations.Streaming;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

public class FileProbeTests
{
    [Fact]
    public void ReadableFile_IsFine()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Null(FileProbe.Describe(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_InExistingFolder_SaysFileNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), "jellystations-missing-" + Guid.NewGuid().ToString("N") + ".mkv");

        var reason = FileProbe.Describe(path);

        Assert.NotNull(reason);
        Assert.StartsWith("File not found: ", reason);
        Assert.Contains(path, reason);
    }

    [Fact]
    public void MissingFolder_SaysFolderNotFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jellystations-nodir-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "movie.mkv");

        var reason = FileProbe.Describe(path);

        Assert.NotNull(reason);
        Assert.StartsWith("Folder not found: ", reason);
        Assert.Contains(dir, reason);
        Assert.Contains("mounted", reason);
    }

    [Fact]
    public void EmptyPath_IsReported()
    {
        Assert.NotNull(FileProbe.Describe(null));
        Assert.NotNull(FileProbe.Describe(string.Empty));
    }
}
