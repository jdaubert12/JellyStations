namespace Jellyfin.Plugin.JellyStations.Weather;

/// <summary>How the last fetches of one kind (conditions, alerts, radar) for one place went.</summary>
public abstract class FetchStatus
{
    public Guid LocationId { get; set; }

    public DateTime? LastAttemptUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public string? LastError { get; set; }

    /// <summary>Consecutive failures since the last success.</summary>
    public int Failures { get; set; }

    /// <summary>Records a fetch that worked: clears the error and the failure count.</summary>
    public void MarkSuccess(DateTime successUtc)
    {
        LastSuccessUtc = successUtc;
        LastError = null;
        Failures = 0;
    }

    /// <summary>Records a fetch that failed: one more failure in a row, with its message.</summary>
    public void MarkFailure(string error)
    {
        Failures++;
        LastError = error;
    }
}

/// <summary>When a polled place is due again: every interval after a success, sooner-then-slower after failures.</summary>
public static class FetchBackoff
{
    /// <summary>The wait after <paramref name="failures"/> failures in a row: 1, 2, 4, 8 then 15 minutes.</summary>
    public static TimeSpan Delay(int failures) => TimeSpan.FromMinutes(Math.Min(15, Math.Pow(2, Math.Min(10, failures - 1))));

    /// <summary>
    /// Due when never attempted and the cached copy (asked for only then) is missing or older than the interval,
    /// when a failure's backoff has passed, or when the last success is older than the interval.
    /// </summary>
    public static bool IsDue(FetchStatus status, Func<DateTime?> cachedFetchedUtc, TimeSpan interval, DateTime nowUtc)
    {
        if (status.LastAttemptUtc is null)
        {
            var cached = cachedFetchedUtc();
            return cached is null || nowUtc - cached.Value >= interval;
        }

        var wait = status.Failures > 0 ? Delay(status.Failures) : interval;
        return nowUtc - status.LastAttemptUtc.Value >= wait;
    }
}
