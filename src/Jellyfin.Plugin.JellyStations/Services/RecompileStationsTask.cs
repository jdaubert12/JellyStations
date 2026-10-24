using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Services;

/// <summary>Nightly: re-expand station content so new episodes/movies join the rotation.</summary>
public sealed class RecompileStationsTask : IScheduledTask
{
    private readonly StationService _stations;
    private readonly GuideRefreshService _guide;
    private readonly ILogger<RecompileStationsTask> _logger;

    public RecompileStationsTask(StationService stations, GuideRefreshService guide, ILogger<RecompileStationsTask> logger)
    {
        _stations = stations;
        _guide = guide;
        _logger = logger;
    }

    public string Name => "Refresh JellyStations content";

    public string Key => "JellyStationsRecompile";

    public string Description => "Re-scans the library for every TV station that has nightly refresh enabled and updates the guide if anything changed.";

    public string Category => "JellyStations";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var all = _stations.GetAll().Where(s => s.AutoRecompileNightly).ToList();
        var changed = 0;
        for (var i = 0; i < all.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = _stations.Recompile(all[i].Id, triggerGuide: false);
                if (result.ContentChanged)
                {
                    changed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyStations: nightly recompile failed for {Name}", all[i].Name);
            }

            progress.Report(100.0 * (i + 1) / Math.Max(1, all.Count));
        }

        if (changed > 0)
        {
            _logger.LogInformation("JellyStations: {Count} station(s) changed content; refreshing guide", changed);
            _guide.Trigger();
        }

        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }
}
