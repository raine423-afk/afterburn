using System.Globalization;
using System.Text;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Pages.Conflicts;

/// <summary>Afterburn: one mod's copy of a conflicting file.</summary>
public sealed class ConflictProvider
{
    /// <summary>Position in the conflict order (1 = first). Higher wins.</summary>
    public required int Rank { get; init; }
    public required ulong Priority { get; init; }
    public required string ModName { get; init; }
    public required EntityId GroupId { get; init; }
    public required EntityId ItemId { get; init; }
    public required bool IsUsed { get; init; }
    public required bool IsDeletion { get; init; }
    public required ulong? Size { get; init; }
    public required string Hash { get; init; }
    public required string Source { get; init; }
    public required string Collection { get; init; }

    public string Line
    {
        get
        {
            var parts = new List<string>
            {
                $"#{Rank,-5} {(IsUsed ? "USED  " : "hidden")}  {ModName}",
                IsDeletion ? "removes this file" : $"{ConflictsCollector.FormatSize(Size)}, xxh3 {Hash}",
            };
            if (Source.Length > 0) parts.Add($"from {Source}");
            if (Collection.Length > 0) parts.Add($"collection {Collection}");
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>Afterburn: one file that more than one enabled mod provides with different content.</summary>
public sealed class ConflictFile
{
    public required string Location { get; init; }
    public required string Path { get; init; }
    public required IReadOnlyList<ConflictProvider> Providers { get; init; }
    public required int DistinctContents { get; init; }

    public ConflictProvider Winner => Providers[^1];
    public string FullPath => $"{{{Location}}}/{Path}";
    public string Folder => ConflictsCollector.FolderOf(Path);
    public string Extension => ConflictsCollector.ExtensionOf(Path);

    public string Header =>
        $"{FullPath}    ({Providers.Count} copies, {DistinctContents} different, game uses {Winner.ModName})";

    public string Details => string.Join("\n", Providers.Select(p => p.Line));

    internal bool Matches(string search) =>
        FullPath.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Providers.Any(p => p.ModName.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || p.Hash.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || p.Source.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || p.Collection.Contains(search, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Afterburn: two mods that provide some of the same files.</summary>
public sealed class ModPairRow
{
    public required string ModA { get; init; }
    public required int RankA { get; init; }
    public required string ModB { get; init; }
    public required int RankB { get; init; }
    public required int Files { get; init; }
    public required int AWins { get; init; }
    public required int BWins { get; init; }
    public required IReadOnlyList<string> SamplePaths { get; init; }

    public string Header => $"{ModA} (#{RankA})  vs  {ModB} (#{RankB})  —  {Files:N0} shared file{(Files == 1 ? "" : "s")}";

    public string Outcome =>
        $"{ModA}'s copy is used for {AWins:N0}, {ModB}'s for {BWins:N0}" +
        (AWins > 0 && BWins > 0 ? "  (split: a third mod or a per-file choice decides some of them)" : "");

    public string Samples => "e.g. " + string.Join(", ", SamplePaths) + (Files > SamplePaths.Count ? $" and {Files - SamplePaths.Count:N0} more" : "");

    internal bool Matches(string search) =>
        ModA.Contains(search, StringComparison.OrdinalIgnoreCase)
        || ModB.Contains(search, StringComparison.OrdinalIgnoreCase)
        || SamplePaths.Any(p => p.Contains(search, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Afterburn: one mod's totals across all conflicts.</summary>
public sealed class ConflictModRow
{
    public required string Name { get; init; }
    public required int Rank { get; init; }
    public required int Wins { get; init; }
    public required int Losses { get; init; }
    public required string Source { get; init; }
    public required string Collection { get; init; }
    public required IReadOnlyList<(string Name, int Files)> Opponents { get; init; }

    public string Header => $"#{Rank}  {Name}";

    public string Counts =>
        $"Its copy is used for {Wins:N0} file{(Wins == 1 ? "" : "s")} · hidden for {Losses:N0}" +
        (Source.Length > 0 ? $" · from {Source}" : "") +
        (Collection.Length > 0 ? $" · collection {Collection}" : "");

    public string OpponentText
    {
        get
        {
            const int shown = 10;
            var text = string.Join(", ", Opponents.Take(shown).Select(o => $"{o.Name} ({o.Files:N0})"));
            return $"Shares files with: {text}{(Opponents.Count > shown ? $" and {Opponents.Count - shown:N0} more" : "")}";
        }
    }

    internal bool Matches(string search) =>
        Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Opponents.Any(o => o.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Afterburn: conflicts grouped by folder or by file type.</summary>
public sealed class ConflictGroupRow
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required int Files { get; init; }
    public required int Mods { get; init; }

    public string Text => $"{Kind}: {Name}  —  {Files:N0} conflicting file{(Files == 1 ? "" : "s")} across {Mods:N0} mod{(Mods == 1 ? "" : "s")}";

    internal bool Matches(string search) => Name.Contains(search, StringComparison.OrdinalIgnoreCase);
}

public sealed record ConflictsSnapshot(
    IReadOnlyList<ConflictFile> Files,
    IReadOnlyList<ModPairRow> Pairs,
    IReadOnlyList<ConflictModRow> Mods,
    IReadOnlyList<ConflictGroupRow> Groups,
    int PriorityGroups)
{
    public static readonly ConflictsSnapshot Empty = new([], [], [], [], 0);
}

/// <summary>
/// Afterburn: gathers full conflict details for the Conflicts page: for each file that enabled mods provide with
/// different content, every copy in conflict order with its size, hash, download and collection; plus mod pairs,
/// per-mod totals, and folders/file types. The conflict set is Afterburn's own (synchronizer.ConflictingPaths), so
/// it matches what Apply does. Files that several mods provide with identical content aren't conflicts and aren't listed.
/// </summary>
public static class ConflictsCollector
{
    public static ConflictsSnapshot Gather(IConnection connection, LoadoutId loadoutId)
    {
        var db = connection.Db;

        // Conflict order for the whole loadout: rank 1 = lowest priority (loses to everything after it).
        var ranks = new Dictionary<EntityId, (int Rank, ulong Priority)>();
        var orderedPriorities = LoadoutItemGroupPriority.FindByLoadout(db, loadoutId)
            .Select(p => (p.Id, Priority: p.Priority.Value))
            .OrderBy(p => p.Priority)
            .ToArray();
        for (var i = 0; i < orderedPriorities.Length; i++)
            ranks[orderedPriorities[i].Id] = (i + 1, orderedPriorities[i].Priority);

        // One row per (conflicting file, copy). The key is the winning copy, which is unique per file.
        var rows = connection.Query<(EntityId WinnerItemId, EntityId PriorityId, EntityId ItemId)>(
            $"""
             SELECT
               conflicting_path.Winner.LoadoutItemId,
               conflict."unnest".PriorityId,
               conflict."unnest".LoadoutItemId
             FROM
               synchronizer.ConflictingPaths ({connection}) conflicting_path
               CROSS JOIN unnest(conflicting_path.Conflicts) conflict
             WHERE
               conflicting_path.Loadout = {loadoutId};
             """
        ).ToArray();

        var groupInfo = new Dictionary<EntityId, (string Name, EntityId GroupId, string Source, string Collection)>();
        (string Name, EntityId GroupId, string Source, string Collection) GroupOf(EntityId priorityId)
        {
            if (groupInfo.TryGetValue(priorityId, out var info)) return info;
            var name = "(unknown mod)";
            var groupId = priorityId;
            var source = string.Empty;
            var collection = string.Empty;
            try
            {
                var group = LoadoutItemGroupPriority.Load(db, priorityId).Target;
                groupId = group.Id;
                var item = group.AsLoadoutItem();
                if (!string.IsNullOrWhiteSpace(item.Name)) name = item.Name;
                if (group.TryGetAsLibraryLinkedLoadoutItem(out var linked)) source = linked.LibraryItem.Name;
                if (item.HasParent() && item.Parent.TryGetAsCollectionGroup(out var collectionGroup))
                    collection = collectionGroup.AsLoadoutItemGroup().AsLoadoutItem().Name;
            }
            catch
            {
                // keep the fallbacks
            }
            info = (name, groupId, source, collection);
            groupInfo[priorityId] = info;
            return info;
        }

        var files = new List<ConflictFile>();
        foreach (var group in rows.GroupBy(r => r.WinnerItemId))
        {
            string location;
            string path;
            try
            {
                var target = LoadoutItemWithTargetPath.Load(db, group.Key);
                GamePath gamePath = target.TargetPath;
                location = gamePath.LocationId.ToString();
                path = gamePath.Path.ToString().Replace('\\', '/');
            }
            catch
            {
                location = "?";
                path = "(unknown file)";
            }

            var providers = new List<ConflictProvider>();
            foreach (var row in group.DistinctBy(r => r.ItemId))
            {
                var (name, groupId, source, collection) = GroupOf(row.PriorityId);
                var (rank, priority) = ranks.TryGetValue(row.PriorityId, out var r) ? r : (0, 0UL);

                ulong? size = null;
                var hash = "-";
                var isDeletion = false;
                try
                {
                    var item = LoadoutItemWithTargetPath.Load(db, row.ItemId);
                    if (item.TryGetAsLoadoutFile(out var file))
                    {
                        size = file.Size.Value;
                        hash = file.Hash.Value.ToString("x16", CultureInfo.InvariantCulture);
                    }
                    else if (item.TryGetAsDeletedFile(out _))
                    {
                        isDeletion = true;
                    }
                }
                catch
                {
                    // keep the fallbacks
                }

                providers.Add(new ConflictProvider
                {
                    Rank = rank,
                    Priority = priority,
                    ModName = name,
                    GroupId = groupId,
                    ItemId = row.ItemId,
                    IsUsed = row.ItemId == group.Key,
                    IsDeletion = isDeletion,
                    Size = size,
                    Hash = hash,
                    Source = source,
                    Collection = collection,
                });
            }

            if (providers.Count < 2) continue;

            // Order like Apply does: lowest priority first, the used copy last.
            var ordered = providers
                .OrderBy(p => p.IsUsed ? 1 : 0)
                .ThenBy(p => p.Priority)
                .ToList();

            files.Add(new ConflictFile
            {
                Location = location,
                Path = path,
                Providers = ordered,
                DistinctContents = ordered.Select(p => p.IsDeletion ? "deleted" : p.Hash).Distinct().Count(),
            });
        }

        files.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));

        return new ConflictsSnapshot(
            files,
            BuildPairs(files),
            BuildMods(files, ranks),
            BuildGroups(files),
            orderedPriorities.Length);
    }

    private static IReadOnlyList<ModPairRow> BuildPairs(IReadOnlyList<ConflictFile> files)
    {
        var pairs = new Dictionary<(EntityId, EntityId), (ConflictProvider A, ConflictProvider B, int Files, int AWins, int BWins, List<string> Samples)>();
        foreach (var file in files)
        {
            var winnerGroup = file.Winner.GroupId;
            var distinctMods = file.Providers.DistinctBy(p => p.GroupId).ToArray();
            for (var i = 0; i < distinctMods.Length; i++)
            for (var j = i + 1; j < distinctMods.Length; j++)
            {
                var (a, b) = distinctMods[i].Rank <= distinctMods[j].Rank ? (distinctMods[i], distinctMods[j]) : (distinctMods[j], distinctMods[i]);
                var key = (a.GroupId, b.GroupId);
                if (!pairs.TryGetValue(key, out var acc)) acc = (a, b, 0, 0, 0, new List<string>());
                acc.Files++;
                if (winnerGroup == a.GroupId) acc.AWins++;
                else if (winnerGroup == b.GroupId) acc.BWins++;
                if (acc.Samples.Count < 5) acc.Samples.Add(file.Path);
                pairs[key] = acc;
            }
        }

        return pairs.Values
            .Select(p => new ModPairRow
            {
                ModA = p.A.ModName,
                RankA = p.A.Rank,
                ModB = p.B.ModName,
                RankB = p.B.Rank,
                Files = p.Files,
                AWins = p.AWins,
                BWins = p.BWins,
                SamplePaths = p.Samples,
            })
            .OrderByDescending(p => p.Files)
            .ThenBy(p => p.ModA, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ConflictModRow> BuildMods(IReadOnlyList<ConflictFile> files, Dictionary<EntityId, (int Rank, ulong Priority)> ranks)
    {
        var mods = new Dictionary<EntityId, (ConflictProvider Sample, int Wins, int Losses, Dictionary<string, int> Opponents)>();
        foreach (var file in files)
        {
            var winnerGroup = file.Winner.GroupId;
            var distinctMods = file.Providers.DistinctBy(p => p.GroupId).ToArray();
            foreach (var mod in distinctMods)
            {
                if (!mods.TryGetValue(mod.GroupId, out var acc)) acc = (mod, 0, 0, new Dictionary<string, int>());
                if (mod.GroupId == winnerGroup) acc.Wins++;
                else acc.Losses++;
                foreach (var other in distinctMods)
                {
                    if (other.GroupId == mod.GroupId) continue;
                    acc.Opponents[other.ModName] = acc.Opponents.GetValueOrDefault(other.ModName) + 1;
                }
                mods[mod.GroupId] = acc;
            }
        }

        return mods.Values
            .Select(m => new ConflictModRow
            {
                Name = m.Sample.ModName,
                Rank = m.Sample.Rank,
                Wins = m.Wins,
                Losses = m.Losses,
                Source = m.Sample.Source,
                Collection = m.Sample.Collection,
                Opponents = m.Opponents.OrderByDescending(o => o.Value).Select(o => (o.Key, o.Value)).ToArray(),
            })
            .OrderBy(m => m.Rank)
            .ToArray();
    }

    private static IReadOnlyList<ConflictGroupRow> BuildGroups(IReadOnlyList<ConflictFile> files)
    {
        static IEnumerable<ConflictGroupRow> By(IReadOnlyList<ConflictFile> files, string kind, Func<ConflictFile, string> key) =>
            files.GroupBy(key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ConflictGroupRow
                {
                    Kind = kind,
                    Name = g.Key,
                    Files = g.Count(),
                    Mods = g.SelectMany(f => f.Providers.Select(p => p.GroupId)).Distinct().Count(),
                })
                .OrderByDescending(g => g.Files);

        return By(files, "Folder", f => $"{{{f.Location}}}/{f.Folder}")
            .Concat(By(files, "File type", f => f.Extension))
            .ToArray();
    }

    // ---------------------------------------------------------------- formatting and export

    public static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "(top folder)" : path[..slash];
    }

    public static string ExtensionOf(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "(no extension)" : name[dot..].ToLowerInvariant();
    }

    public static string FormatSize(ulong? bytes)
    {
        if (bytes is null) return "? bytes";
        var b = (double)bytes.Value;
        return b switch
        {
            < 1024 => $"{bytes.Value:N0} B",
            < 1024 * 1024 => $"{b / 1024:N1} KB",
            < 1024 * 1024 * 1024 => $"{b / 1024 / 1024:N1} MB",
            _ => $"{b / 1024 / 1024 / 1024:N2} GB",
        };
    }

    public static string BuildReport(IEnumerable<string> header, ConflictsSnapshot snapshot)
    {
        var sb = new StringBuilder();
        foreach (var line in header) sb.AppendLine(line);
        sb.AppendLine("Order: #rank is the position in the conflict order; the highest rank's copy is USED, the rest are hidden.");
        sb.AppendLine();

        sb.AppendLine($"==== FILES ({snapshot.Files.Count:N0}) ====");
        foreach (var file in snapshot.Files)
        {
            sb.AppendLine(file.Header);
            foreach (var p in file.Providers) sb.AppendLine("    " + p.Line);
        }
        sb.AppendLine();

        sb.AppendLine($"==== MOD PAIRS ({snapshot.Pairs.Count:N0}) ====");
        foreach (var pair in snapshot.Pairs)
        {
            sb.AppendLine(pair.Header);
            sb.AppendLine("    " + pair.Outcome);
            sb.AppendLine("    " + pair.Samples);
        }
        sb.AppendLine();

        sb.AppendLine($"==== MODS IN CONFLICT ORDER ({snapshot.Mods.Count:N0}) ====");
        foreach (var mod in snapshot.Mods)
        {
            sb.AppendLine(mod.Header);
            sb.AppendLine("    " + mod.Counts);
            sb.AppendLine("    " + mod.OpponentText);
        }
        sb.AppendLine();

        sb.AppendLine($"==== FOLDERS AND FILE TYPES ({snapshot.Groups.Count:N0}) ====");
        foreach (var group in snapshot.Groups) sb.AppendLine(group.Text);

        return sb.ToString();
    }

    public static string BuildCsv(ConflictsSnapshot snapshot)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("Location,Path,Rank,Priority,Mod,Used,Removes file,Size bytes,xxHash3,Download,Collection,Loadout item id");
        foreach (var file in snapshot.Files)
        foreach (var p in file.Providers)
        {
            sb.Append(Q(file.Location)).Append(',')
              .Append(Q(file.Path)).Append(',')
              .Append(p.Rank.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(p.Priority.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Q(p.ModName)).Append(',')
              .Append(p.IsUsed ? "yes" : "no").Append(',')
              .Append(p.IsDeletion ? "yes" : "no").Append(',')
              .Append(p.Size?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(Q(p.Hash)).Append(',')
              .Append(Q(p.Source)).Append(',')
              .Append(Q(p.Collection)).Append(',')
              .Append(p.ItemId.Value.ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }
        return sb.ToString();
    }
}
