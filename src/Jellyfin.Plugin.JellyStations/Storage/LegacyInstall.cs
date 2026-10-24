using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.JellyStations.Storage;

/// <summary>
/// The plugin shipped as "TvStation" up to 1.14.2.0. Jellyfin keeps only the newest version of a plugin, but it
/// decides that by the plugin <em>name</em> in meta.json, not the id, so a JellyStations folder does not retire a
/// TvStation folder beside it and both load. This marks any sibling folder that carries our id under another
/// name with status "Deleted", which Jellyfin honours on its next start by removing the folder.
/// </summary>
public static class LegacyInstall
{
    /// <summary>Marks sibling plugin folders with this id and a different name for deletion. Returns the folders marked.</summary>
    public static IReadOnlyList<string> MarkOtherNamesForDeletion(string pluginsPath, Guid id, string currentName)
    {
        var marked = new List<string>();
        if (string.IsNullOrEmpty(pluginsPath) || !Directory.Exists(pluginsPath))
        {
            return marked;
        }

        foreach (var dir in Directory.EnumerateDirectories(pluginsPath))
        {
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath))
            {
                continue;
            }

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject;
                if (node is null)
                {
                    continue;
                }

                var guid = node["guid"]?.GetValue<string>();
                var name = node["name"]?.GetValue<string>();
                var status = node["status"]?.GetValue<string>();
                if (!Guid.TryParse(guid, out var g) || g != id || string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(status, "Deleted", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                node["status"] = "Deleted";
                File.WriteAllText(metaPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                marked.Add(dir);
            }
            catch (JsonException)
            {
                // Not ours to judge; Jellyfin reports unreadable manifests itself.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return marked;
    }
}
