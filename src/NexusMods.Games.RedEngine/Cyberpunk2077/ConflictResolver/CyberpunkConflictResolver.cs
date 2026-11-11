using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.RedEngine.Cyberpunk2077.ConflictResolver;

/// <summary>
/// Afterburn: a lightweight, automatic conflict check that runs before Cyberpunk 2077 starts.
/// <list type="bullet">
/// <item>Health check: mods whose scripts failed to compile, or whose RED4ext plugin was built for another game
/// version, on the previous launch are reported. Nothing is turned off automatically: one outdated framework mod makes
/// every mod that depends on it show errors, so only the user can tell which mod is really at fault.</item>
/// <item>Archive conflicts: when two .archive mods replace the same game resource, a load order is written to
/// archive/pc/mod/modlist.txt. Mod authors' own naming is respected (the game loads archives in ASCII order and the
/// first one wins, so names starting with "#", "!", "_" or a digit are meant to win and names starting with "zz" are
/// meant to lose); only the remaining conflicts are ordered: patches and fixes first, then smaller targeted mods,
/// then the newest.</item>
/// <item>Script conflicts: redscript mods that replace the same game function are reported.</item>
/// </list>
/// Everything it does is written to r6/logs/afterburn-conflicts.txt.
/// </summary>
public sealed class CyberpunkConflictResolver
{
    private const string ModListOwnerFile = "afterburn-modlist.owner";
    private static readonly string[] PatchWords = ["patch", "fix", "compat", "addon", "override", "hotfix"];
    private static readonly Regex ReplaceMethodRegex = new(
        @"@replaceMethod\(\s*(?<cls>[\w\.]+)\s*\)(?:\s|//[^\n]*\n)*(?:(?:public|private|protected|final|static|cb|native|quest|exec)\s+)*func\s+(?<fn>\w+)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private readonly ILogger _logger;
    private readonly IConnection _connection;
    private readonly ISynchronizerService _synchronizer;

    public CyberpunkConflictResolver(ILogger<CyberpunkConflictResolver> logger, IConnection connection, ISynchronizerService synchronizer)
    {
        _logger = logger;
        _connection = connection;
        _synchronizer = synchronizer;
    }

    /// <summary>
    /// Runs all checks. Never throws: a failed check is logged and skipped so the game still launches.
    /// </summary>
    public async Task<ConflictSummary> RunAsync(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var summary = new ConflictSummary();
        string gameRoot;
        try
        {
            gameRoot = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "")).ToString();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Conflict check: couldn't find the game folder");
            return summary;
        }

        await Try("health check", () => HealthCheckAsync(loadout, gameRoot, summary, cancellationToken));
        await Try("file overwrites", () => { FindOverwrites(Loadout.Load(_connection.Db, loadout.LoadoutId), summary); return Task.CompletedTask; });
        await Try("archive conflicts", () => { ResolveArchiveConflicts(gameRoot, summary); return Task.CompletedTask; });
        await Try("script conflicts", () => { FindScriptConflicts(gameRoot, summary); return Task.CompletedTask; });

        try
        {
            var logDir = Path.Combine(gameRoot, "r6", "logs");
            Directory.CreateDirectory(logDir);
            await File.WriteAllTextAsync(Path.Combine(logDir, "afterburn-conflicts.txt"), summary.ToReport(), cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Conflict check: couldn't write the report");
        }

        _logger.LogInformation("Conflict check: {Summary}", summary.ToShortText());
        return summary;
    }

    private async Task Try(string name, Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { _logger.LogWarning(e, "Conflict check: {Step} failed and was skipped", name); }
    }

    // ---------------------------------------------------------------- health check

    private async Task HealthCheckAsync(Loadout.ReadOnly loadout, string gameRoot, ConflictSummary summary, CancellationToken cancellationToken)
    {
        var logsDir = Path.Combine(gameRoot, "r6", "logs");
        var redscriptLog = new FileInfo(Path.Combine(logsDir, "redscript_rCURRENT.log"));
        var markerFile = Path.Combine(logsDir, "afterburn-health.last");

        // Only act on a compile log we haven't handled yet, so a mod you turn back on (for example after updating it)
        // isn't turned off again because of an old log.
        long lastHandled = 0;
        if (File.Exists(markerFile)) long.TryParse(await File.ReadAllTextAsync(markerFile, cancellationToken), out lastHandled);

        var brokenPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // game-relative path prefix -> reason

        if (redscriptLog.Exists && redscriptLog.LastWriteTimeUtc.Ticks > lastHandled)
        {
            foreach (var line in File.ReadLines(redscriptLog.FullName))
            {
                if (!line.Contains("[ERROR", StringComparison.Ordinal)) continue;
                var idx = line.IndexOf(@"\r6\scripts\", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var rest = line[(idx + @"\r6\scripts\".Length)..];
                var colon = rest.IndexOf(".reds", StringComparison.OrdinalIgnoreCase);
                if (colon < 0) continue;
                var relative = rest[..(colon + ".reds".Length)];
                var slash = relative.IndexOf('\\');
                var prefix = slash < 0 ? $"r6/scripts/{relative}" : $"r6/scripts/{relative[..slash]}/";
                brokenPaths.TryAdd(prefix, "its scripts don't compile on this game version");
            }

            await File.WriteAllTextAsync(markerFile, redscriptLog.LastWriteTimeUtc.Ticks.ToString(), cancellationToken);
        }

        // RED4ext plugins built for another game version are skipped by RED4ext anyway; turning them off keeps things tidy.
        var red4extLogs = new DirectoryInfo(Path.Combine(gameRoot, "red4ext", "logs"));
        if (red4extLogs.Exists)
        {
            var latest = red4extLogs.GetFiles("red4ext-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest is not null && latest.LastWriteTimeUtc.Ticks > lastHandled)
            {
                string? currentPlugin = null;
                foreach (var line in File.ReadLines(latest.FullName))
                {
                    var load = line.IndexOf(@"\red4ext\plugins\", StringComparison.OrdinalIgnoreCase);
                    if (load >= 0 && line.Contains("Loading plugin from", StringComparison.Ordinal))
                    {
                        var rest = line[(load + @"\red4ext\plugins\".Length)..];
                        var slash = rest.IndexOf('\\');
                        currentPlugin = slash > 0 ? rest[..slash] : null;
                    }
                    else if (currentPlugin is not null && line.Contains("is incompatible with the current patch", StringComparison.Ordinal))
                    {
                        brokenPaths.TryAdd($"red4ext/plugins/{currentPlugin}/", "its RED4ext plugin was built for another game version");
                    }
                }
            }
        }

        if (brokenPaths.Count == 0) return;

        // Find the mods (loadout groups) that own those files, so the report can name them.
        var groupsToDisable = new Dictionary<EntityId, (string Name, string Reason)>();
        var matchedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var path = file.TargetPath.Item3.ToString().Replace('\\', '/');
            var match = brokenPaths.FirstOrDefault(kv => path.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) || path.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null) continue;

            var item = file.AsLoadoutItem();
            if (!item.IsEnabled() || !item.HasParent()) continue;
            var group = item.Parent;
            if (LoadoutOverridesGroup.Load(group.Db, group.Id).IsValid() || LoadoutGameFilesGroup.Load(group.Db, group.Id).IsValid()) continue;

            groupsToDisable.TryAdd(group.Id, (group.AsLoadoutItem().Name, match.Value));
            matchedPrefixes.Add(match.Key);
        }

        foreach (var (prefix, reason) in brokenPaths.Where(kv => !matchedPrefixes.Contains(kv.Key)))
            summary.Notes.Add($"{prefix}: {reason}, but it wasn't installed by Afterburn, so remove it by hand.");

        foreach (var (name, reason) in groupsToDisable.Values)
            summary.BrokenMods.Add($"{name} ({reason})");
    }

    // ---------------------------------------------------------------- same-file overwrites

    /// <summary>
    /// Lists files that more than one enabled mod puts at the same path. Afterburn's load order already picks one
    /// winner for each; this makes it visible which mods overlap so surprises can be fixed on the Load Order page.
    /// </summary>
    private static void FindOverwrites(Loadout.ReadOnly loadout, ConflictSummary summary)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var item = file.AsLoadoutItem();
            if (!item.IsEnabled() || !item.HasParent()) continue;
            var group = item.Parent;
            if (LoadoutOverridesGroup.Load(group.Db, group.Id).IsValid() || LoadoutGameFilesGroup.Load(group.Db, group.Id).IsValid()) continue;

            var path = file.TargetPath.Item3.ToString().Replace('\\', '/');
            if (!owners.TryGetValue(path, out var mods)) owners[path] = mods = new HashSet<string>(StringComparer.Ordinal);
            mods.Add(group.AsLoadoutItem().Name);
        }

        // Group by the set of mods involved so one line covers all files two mods share.
        var byModSet = owners.Where(kv => kv.Value.Count > 1)
            .GroupBy(kv => string.Join(" + ", kv.Value.Order(StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(g => g.Count())
            .Take(40);
        foreach (var g in byModSet)
            summary.Overwrites.Add($"{g.Key}: {g.Count()} shared file(s), e.g. {g.First().Key}");
    }

    // ---------------------------------------------------------------- archive conflicts

    private void ResolveArchiveConflicts(string gameRoot, ConflictSummary summary)
    {
        var modDir = new DirectoryInfo(Path.Combine(gameRoot, "archive", "pc", "mod"));
        if (!modDir.Exists) return;

        var archives = modDir.GetFiles("*.archive", SearchOption.TopDirectoryOnly);
        var owners = new Dictionary<ulong, List<int>>();
        var entryCounts = new int[archives.Length];

        for (var i = 0; i < archives.Length; i++)
        {
            var hashes = ArchiveIndexReader.TryReadResourceHashes(archives[i].FullName);
            if (hashes is null) continue;
            entryCounts[i] = hashes.Count;
            foreach (var hash in hashes)
            {
                if (!owners.TryGetValue(hash, out var list)) owners[hash] = list = new List<int>(1);
                if (list.Count == 0 || list[^1] != i) list.Add(i);
            }
        }

        var conflicting = new HashSet<int>();
        var pairCounts = new Dictionary<(int, int), int>();
        foreach (var list in owners.Values)
        {
            if (list.Count < 2) continue;
            foreach (var a in list) conflicting.Add(a);
            for (var x = 0; x < list.Count; x++)
            for (var y = x + 1; y < list.Count; y++)
            {
                var key = (Math.Min(list[x], list[y]), Math.Max(list[x], list[y]));
                pairCounts[key] = pairCounts.GetValueOrDefault(key) + 1;
            }
        }

        if (conflicting.Count == 0) return;

        // The game loads archives in ASCII (ordinal) order and the first one wins. Respect the author's intent first:
        // names starting with "#", "!", "_" or a digit are meant to win and keep their order; names starting with
        // "zz" are meant to lose. Only the remaining conflicting archives get the smart order: patches/fixes first,
        // then the most targeted mod (fewest files), then the newest.
        int Rank(int i) => PatchWords.Any(w => archives[i].Name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? 0 : 1;
        var authorFirst = conflicting.Where(i => AuthorWantsFirst(archives[i].Name))
            .OrderBy(i => archives[i].Name, StringComparer.Ordinal);
        var authorLast = conflicting.Where(i => AuthorWantsLast(archives[i].Name))
            .OrderBy(i => archives[i].Name, StringComparer.Ordinal);
        var middle = conflicting.Where(i => !AuthorWantsFirst(archives[i].Name) && !AuthorWantsLast(archives[i].Name))
            .OrderBy(Rank)
            .ThenBy(i => entryCounts[i])
            .ThenByDescending(i => archives[i].LastWriteTimeUtc)
            .ThenBy(i => archives[i].Name, StringComparer.Ordinal);
        var winnersFirst = authorFirst.Concat(middle).Concat(authorLast).ToList();

        var modList = Path.Combine(modDir.FullName, "modlist.txt");
        var ownerFile = Path.Combine(modDir.FullName, ModListOwnerFile);
        var userModList = File.Exists(modList) && !File.Exists(ownerFile);

        foreach (var ((a, b), count) in pairCounts.OrderByDescending(p => p.Value).Take(50))
        {
            var winner = winnersFirst.IndexOf(a) < winnersFirst.IndexOf(b) ? a : b;
            var loser = winner == a ? b : a;
            summary.ArchiveConflicts.Add($"{archives[winner].Name} wins over {archives[loser].Name} ({count} shared resource(s))");
        }

        if (userModList)
        {
            summary.Notes.Add("archive/pc/mod/modlist.txt was made by you or another tool, so Afterburn left your archive load order as it is.");
            return;
        }

        // Conflicting archives in winning order first, then everything else in the game's default (ASCII) order.
        var rest = Enumerable.Range(0, archives.Length).Where(i => !conflicting.Contains(i))
            .OrderBy(i => archives[i].Name, StringComparer.Ordinal);
        var lines = winnersFirst.Concat(rest).Select(i => archives[i].Name).ToArray();
        var previous = File.Exists(modList) ? File.ReadAllLines(modList) : [];
        if (previous.SequenceEqual(lines, StringComparer.Ordinal)) return;
        File.WriteAllLines(modList, lines, new UTF8Encoding(false));
        summary.ModListChanged = true;
        File.WriteAllText(ownerFile, "This modlist.txt is managed by Afterburn's conflict check. Delete this file to manage modlist.txt yourself.");
    }

    private static bool AuthorWantsFirst(string name) => name.Length > 0 && !char.IsAsciiLetter(name[0]);

    private static bool AuthorWantsLast(string name) => name.StartsWith("zz", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- script conflicts

    private void FindScriptConflicts(string gameRoot, ConflictSummary summary)
    {
        var scriptsDir = new DirectoryInfo(Path.Combine(gameRoot, "r6", "scripts"));
        if (!scriptsDir.Exists) return;

        var replacements = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in scriptsDir.EnumerateFiles("*.reds", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(scriptsDir.FullName, file.FullName);
            var sep = relative.IndexOfAny(['\\', '/']);
            var mod = sep < 0 ? Path.GetFileNameWithoutExtension(relative) : relative[..sep];

            string text;
            try { text = File.ReadAllText(file.FullName); } catch { continue; }
            if (!text.Contains("@replaceMethod", StringComparison.Ordinal)) continue;

            MatchCollection matches;
            try { matches = ReplaceMethodRegex.Matches(text); _ = matches.Count; }
            catch (RegexMatchTimeoutException) { continue; }

            foreach (Match m in matches)
            {
                var key = $"{m.Groups["cls"].Value}.{m.Groups["fn"].Value}";
                if (!replacements.TryGetValue(key, out var mods)) replacements[key] = mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                mods.Add(mod);
            }
        }

        foreach (var (method, mods) in replacements.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key))
            summary.ScriptConflicts.Add($"{method} is replaced by {string.Join(", ", mods.Order())} (only one can take effect)");
    }
}

/// <summary>What the conflict check found and did.</summary>
public sealed class ConflictSummary
{
    /// <summary>Mods that failed on the last launch (reported only; Afterburn doesn't turn them off).</summary>
    public List<string> BrokenMods { get; } = [];
    public List<string> ArchiveConflicts { get; } = [];
    public List<string> ScriptConflicts { get; } = [];
    public List<string> Overwrites { get; } = [];
    public List<string> Notes { get; } = [];

    public bool ModListChanged { get; set; }

    public bool HasAnything => BrokenMods.Count > 0 || ArchiveConflicts.Count > 0 || ScriptConflicts.Count > 0 || Overwrites.Count > 0;

    /// <summary>True when Afterburn changed something this launch, so the toast doesn't repeat every time.</summary>
    public bool ShouldNotify => BrokenMods.Count > 0 || ModListChanged;

    public string ToShortText()
    {
        var parts = new List<string>();
        if (BrokenMods.Count > 0) parts.Add($"{BrokenMods.Count} mod(s) failed to load last time; check them and update or turn them off");
        if (ArchiveConflicts.Count > 0) parts.Add($"ordered {ArchiveConflicts.Count} archive conflict(s)");
        if (ScriptConflicts.Count > 0) parts.Add($"found {ScriptConflicts.Count} script conflict(s)");
        if (Overwrites.Count > 0) parts.Add($"{Overwrites.Count} mod overlap(s) handled by load order");
        if (parts.Count > 0) parts.Add("details in r6/logs/afterburn-conflicts.txt");
        return parts.Count == 0 ? "no conflicts found" : "Conflict check: " + string.Join("; ", parts);
    }

    public string ToReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Afterburn conflict check - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        Section(sb, "Mods that failed on the last launch (nothing was turned off; if many are listed, an outdated framework mod such as Codeware, ArchiveXL, TweakXL or RED4ext is the usual cause)", BrokenMods);
        Section(sb, "Archive conflicts (load order set in archive/pc/mod/modlist.txt)", ArchiveConflicts);
        Section(sb, "Script conflicts (two mods replace the same game function; the game uses only one)", ScriptConflicts);
        Section(sb, "Same-file overwrites (load order picks the winner; change it on the Load Order page)", Overwrites);
        Section(sb, "Notes", Notes);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, List<string> items)
    {
        sb.AppendLine($"{title}: {items.Count}");
        foreach (var item in items) sb.AppendLine($"  - {item}");
        sb.AppendLine();
    }
}
