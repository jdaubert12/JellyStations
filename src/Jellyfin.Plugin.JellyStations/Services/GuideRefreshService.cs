using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Services;

/// <summary>Triggers Jellyfin's built-in "Refresh Guide" task, debounced so bursts of edits run it once.</summary>
public sealed class GuideRefreshService
{
    private readonly ITaskManager _taskManager;
    private readonly ILogger<GuideRefreshService> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _pending;

    public GuideRefreshService(ITaskManager taskManager, ILogger<GuideRefreshService> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    public void Trigger()
    {
        lock (_lock)
        {
            _pending?.Cancel();
            _pending = new CancellationTokenSource();
            var token = _pending.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(750), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                TriggerNow();
            });
        }
    }

    public bool TriggerNow()
    {
        var worker = _taskManager.ScheduledTasks.FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "RefreshGuide", StringComparison.Ordinal));
        if (worker is null)
        {
            _logger.LogWarning("JellyStations: RefreshGuide task not found; guide will update on its normal schedule");
            return false;
        }

        try
        {
            _logger.LogInformation("JellyStations: triggering Live TV guide refresh");
            _ = _taskManager.Execute(worker, new TaskOptions());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations: failed to trigger guide refresh");
            return false;
        }
    }
}
