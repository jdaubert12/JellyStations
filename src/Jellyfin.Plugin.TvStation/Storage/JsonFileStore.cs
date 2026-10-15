using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvStation.Storage;

/// <summary>Thread-safe JSON file persistence with atomic writes.</summary>
public static class JsonFileStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static T? Read<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        Gate.Wait();
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(fs, Options);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        Gate.Wait();
        try
        {
            using (var fs = File.Create(tmp))
            {
                JsonSerializer.Serialize(fs, value, Options);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Delete(string path)
    {
        Gate.Wait();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public static T Clone<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
