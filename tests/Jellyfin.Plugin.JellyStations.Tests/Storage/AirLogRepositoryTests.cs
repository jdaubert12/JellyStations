using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Storage;

public class AirLogRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jellystations-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RoundTrips_AndMissingIsEmpty()
    {
        var repo = new AirLogRepository(_dir);
        var id = Guid.NewGuid();
        var empty = repo.Get(id);
        Assert.Equal(id, empty.StationId);
        Assert.Empty(empty.Keys);

        var log = new AirLog { StationId = id, LastTickUtc = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), LastProgramId = "p1" };
        log.Keys["s:abc"] = new AirLogKey { LastStartUtc = log.LastTickUtc.Value, LastEndUtc = log.LastTickUtc.Value.AddMinutes(22), Count = 3, LastItemId = Guid.NewGuid(), Title = "Bluey", Season = 1, Episode = 4 };
        log.Items[log.Keys["s:abc"].LastItemId] = new AirLogItem { LastStartUtc = log.LastTickUtc.Value, Count = 1 };
        repo.Save(log);

        var back = repo.Get(id);
        Assert.Equal("p1", back.LastProgramId);
        Assert.Equal(log.LastTickUtc, back.LastTickUtc);
        Assert.Equal(3, back.Keys["s:abc"].Count);
        Assert.Equal(4, back.Keys["s:abc"].Episode);
        Assert.Single(back.Items);

        repo.Delete(id);
        Assert.Empty(repo.Get(id).Keys);
    }

    [Fact]
    public void UnknownFormat_ReadsAsEmpty()
    {
        var repo = new AirLogRepository(_dir);
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(repo.FileFor(id), "{\"stationId\":\"" + id + "\",\"format\":99,\"keys\":{\"s:x\":{\"count\":5}}}");
        Assert.Empty(repo.Get(id).Keys);
        File.WriteAllText(repo.FileFor(id), "not json");
        Assert.Empty(repo.Get(id).Keys);
    }
}
