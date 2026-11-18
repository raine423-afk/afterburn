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

    // Parsed copy of the entries for the hot path (checked for every file on every Apply): no lock, no allocations
    // when nothing is excluded.
    private static volatile (string Location, string Folder)[]? _snapshot;

    private static (string Location, string Folder)[] Snapshot()
    {
        var snap = _snapshot;
        if (snap is not null) return snap;
        lock (Gate)
        {
            snap = Entries.Select(static e =>
            {
                var bar = e.IndexOf('|');
                return bar < 0 ? (e, string.Empty) : (e[..bar], e[(bar + 1)..]);
            }).ToArray();
            _snapshot = snap;
            return snap;
        }
    }

    /// <summary>True if the path is inside any excluded folder.</summary>
    public static bool IsExcluded(GamePath path)
    {
        var snap = Snapshot();
        if (snap.Length == 0) return false;

        var location = path.LocationId.ToString();
        var p = path.Path.ToString().Replace('\\', '/');
        foreach (var (entryLocation, folder) in snap)
        {
            if (!string.Equals(entryLocation, location, StringComparison.Ordinal)) continue;
            if (folder.Length == 0)
            {
                // "" means files directly in the location's root folder, not everything under it.
                if (!p.Contains('/')) return true;
                continue;
            }
            if (p.Length > folder.Length && p[folder.Length] == '/' && p.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
            _snapshot = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(entries.OrderBy(x => x, StringComparer.Ordinal).ToArray()));
            }
            catch { /* kept for this session */ }
        }
    }
}
