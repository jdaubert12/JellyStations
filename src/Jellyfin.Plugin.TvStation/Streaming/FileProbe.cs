namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>Tells apart the ways a media file can fail to open, so the dashboard can say which one it was.</summary>
public static class FileProbe
{
    /// <summary>Null when the file can be opened for reading; otherwise a one-line reason suitable for the dashboard.</summary>
    public static string? Describe(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "No file path for this item";
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Permission denied: " + path + " (the Jellyfin user cannot read it; check file and folder permissions)";
        }
        catch (DirectoryNotFoundException)
        {
            return "Folder not found: " + (Path.GetDirectoryName(path) ?? path) + " (is the share or volume mounted?)";
        }
        catch (FileNotFoundException)
        {
            return "File not found: " + path;
        }
        catch (IOException ex)
        {
            return "Cannot open " + path + ": " + ex.Message;
        }
    }
}
