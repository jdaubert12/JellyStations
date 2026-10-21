namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>Looks at a running process from the outside: which GPU device node it holds open, and what DRM nodes the host has.</summary>
public static class ProcessInspector
{
    /// <summary>True when <see cref="OpenGpuDevice"/> can answer on this host (Linux, via /proc).</summary>
    public static bool CanInspect => OperatingSystem.IsLinux();

    /// <summary>
    /// The first GPU device node (/dev/dri/*, /dev/nvidia*) the process has open, or null when it has none or the
    /// host cannot tell. A hardware decoder or encoder holds its device for the life of the process, so an open
    /// node is proof the GPU is in play; software decoding never opens one.
    /// </summary>
    public static string? OpenGpuDevice(int pid)
    {
        if (!CanInspect)
        {
            return null;
        }

        try
        {
            var dir = "/proc/" + pid + "/fd";
            if (!Directory.Exists(dir))
            {
                return null;
            }

            foreach (var fd in Directory.EnumerateFileSystemEntries(dir))
            {
                string? target;
                try
                {
                    target = new FileInfo(fd).LinkTarget;
                }
                catch (Exception)
                {
                    continue;
                }

                if (IsGpuDevice(target))
                {
                    return target;
                }
            }
        }
        catch (Exception)
        {
            // Process gone, or /proc not readable.
        }

        return null;
    }

    public static bool IsGpuDevice(string? path) =>
        path is not null && (path.StartsWith("/dev/dri/", StringComparison.Ordinal) || path.StartsWith("/dev/nvidia", StringComparison.Ordinal));

    /// <summary>DRM render nodes and cards present on the host (Linux), e.g. /dev/dri/renderD128.</summary>
    public static IReadOnlyList<string> DrmNodes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.Exists("/dev/dri")
                ? Directory.EnumerateFileSystemEntries("/dev/dri").Order(StringComparer.Ordinal).ToList()
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}
