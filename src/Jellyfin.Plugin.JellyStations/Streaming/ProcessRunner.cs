using System.Diagnostics;

namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>Starts ffmpeg with redirected stdout/stderr and guarantees the process tree dies with us.</summary>
public sealed class ProcessRunner : IDisposable
{
    private readonly Process _process;
    private readonly Task _stderrPump;
    private bool _disposed;

    private ProcessRunner(Process process, Action<string> onStderrLine)
    {
        _process = process;
        _stderrPump = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    onStderrLine(line);
                }
            }
            catch (Exception)
            {
                // Process ended; nothing to do.
            }
        });
    }

    public Stream Stdout => _process.StandardOutput.BaseStream;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int ExitCode => HasExited ? _process.ExitCode : -1;

    public int Pid => _process.Id;

    /// <summary>CPU time the process has consumed so far, or null once it is gone.</summary>
    public TimeSpan? ProcessorTime
    {
        get
        {
            try
            {
                return HasExited ? null : _process.TotalProcessorTime;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public static ProcessRunner Start(string executable, IReadOnlyList<string> args, Action<string> onStderrLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start " + executable);
        }

        return new ProcessRunner(process, onStderrLine);
    }

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the process to exit: true when it did, false when it ran over and
    /// was killed. When <paramref name="ct"/> is cancelled the process is killed and the cancellation thrown.
    /// </summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            Kill();
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }

    public void Kill()
    {
        try
        {
            if (!HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Kill();
        try
        {
            _stderrPump.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Ignore.
        }

        _process.Dispose();
    }
}
