using System.Text.Json;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Afterburn: game folders the user chose not to back up (from the Storage page). Files in these folders that
/// Afterburn didn't install are still tracked as External Changes, but no backup copy is kept, so they take no extra
/// disk space. The trade-off: if they are removed from the loadout, Afterburn can't put them back.
/// Saved in %LOCALAPPDATA%\Afterburn\backup-exclusions.json as "LocationId|relative/folder" entries.
/// </summary>
public static class BackupExclusions
{
    private static readonly object Gate = new();
    private static HashSet<string>? _entries;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Afterburn", "backup-exclusions.json");

    /// <summary>Key used to store a folder.</summary>
    public static string KeyFor(LocationId location, string folder) =>
        $"{location}|{folder.Replace('\\', '/').Trim('/').ToLowerInvariant()}";

    private static HashSet<string> Entries
    {
        get
        {
            lock (Gate)
            {
                if (_entries is not null) return _entries;
                _entries = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var list = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath));
                        if (list is not null) _entries.UnionWith(list);
                    }
                }
                catch { /* start empty */ }
                return _entries;
            }
        }
    }

    /// <summary>True if this folder is excluded.</summary>
    public static bool IsFolderExcluded(LocationId location, string folder)
    {
        var key = KeyFor(location, folder);
        lock (Gate) return Entries.Contains(key);
    }

    /// <summary>True if the path is inside any excluded folder.</summary>
    public static bool IsExcluded(GamePath path)
    {
        lock (Gate)
        {
            var entries = Entries;
            if (entries.Count == 0) return false;

            var p = path.Path.ToString().Replace('\\', '/').ToLowerInvariant();
            var prefix = $"{path.LocationId}|";
            foreach (var entry in entries)
            {
                if (!entry.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var folder = entry[prefix.Length..];
                if (folder.Length == 0)
                {
                    // "" means files directly in the location's root folder, not everything under it.
                    if (!p.Contains('/')) return true;
                    continue;
                }
                if (p.StartsWith(folder + "/", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>Turns backups for a folder off (excluded = true) or back on.</summary>
    public static void SetExcluded(LocationId location, string folder, bool excluded)
    {
        lock (Gate)
        {
            var entries = Entries;
            var key = KeyFor(location, folder);
            var changed = excluded ? entries.Add(key) : entries.Remove(key);
            if (!changed) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(entries.OrderBy(x => x, StringComparer.Ordinal).ToArray()));
            }
            catch { /* kept for this session */ }
        }
    }
}
