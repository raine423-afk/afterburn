using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
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
public sealed class CyberpunkConflictResolver : IConflictReportProvider
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
    private readonly ConcurrentDictionary<LoadoutId, ConflictReport> _latest = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <inheritdoc />
    public event Action<LoadoutId, ConflictReport>? ReportChanged;

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
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            return await RunCoreAsync(loadout, cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<ConflictSummary> RunCoreAsync(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
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

        // The four checks touch separate parts of the summary, so run them at the same time.
        var current = Loadout.Load(_connection.Db, loadout.LoadoutId);
        await Task.WhenAll(
            Try("health check", () => HealthCheckAsync(current, gameRoot, summary, cancellationToken)),
            Try("file overwrites", () => Task.Run(() => FindOverwrites(current, summary), cancellationToken)),
            Try("archive conflicts", () => Task.Run(() => ResolveArchiveConflicts(gameRoot, summary), cancellationToken)),
            Try("script conflicts", () => Task.Run(() => FindScriptConflicts(gameRoot, summary), cancellationToken))
        );

        // The game's own logs only tell us about the last launch once; keep showing those findings until the game
        // runs again, so checking again from the Conflicts tab doesn't make them disappear.
        if (!summary.ReadNewGameLogs && GetLatest(loadout.LoadoutId) is { } previous)
            summary.CarryOverLastLaunch(previous);

        var report = summary.ToConflictReport(DateTimeOffset.Now);
        _latest[loadout.LoadoutId] = report;

        try
        {
            var logDir = Path.Combine(gameRoot, "r6", "logs");
            Directory.CreateDirectory(logDir);
            await File.WriteAllTextAsync(Path.Combine(logDir, "afterburn-conflicts.txt"), summary.ToReport(), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(logDir, ReportJsonFile), JsonSerializer.Serialize(report), cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Conflict check: couldn't write the report");
        }

        try { ReportChanged?.Invoke(loadout.LoadoutId, report); }
        catch (Exception e) { _logger.LogWarning(e, "Conflict check: showing the report failed"); }

        _logger.LogInformation("Conflict check: {Summary}", summary.ToShortText());
        return summary;
    }

    private const string ReportJsonFile = "afterburn-conflicts.json";

    /// <inheritdoc />
    public ConflictReport? GetLatest(LoadoutId loadoutId)
    {
        if (_latest.TryGetValue(loadoutId, out var report)) return report;

        // Nothing from this session yet: show the report saved by the last check, if there is one.
        try
        {
            var loadout = Loadout.Load(_connection.Db, loadoutId);
            if (!loadout.IsValid()) return null;
            var gameRoot = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "")).ToString();
            var path = Path.Combine(gameRoot, "r6", "logs", ReportJsonFile);
            if (!File.Exists(path)) return null;
            report = JsonSerializer.Deserialize<ConflictReport>(File.ReadAllText(path));
            if (report is null) return null;
            return _latest.GetOrAdd(loadoutId, report);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Conflict check: couldn't read the saved report");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ConflictReport> RunNowAsync(LoadoutId loadoutId, CancellationToken cancellationToken)
    {
        var loadout = Loadout.Load(_connection.Db, loadoutId);
        await RunAsync(loadout, cancellationToken);
        return _latest.TryGetValue(loadoutId, out var report)
            ? report
            : new ConflictReport(DateTimeOffset.Now, "The check couldn't find the game folder.", []);
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
            summary.ReadNewGameLogs = true;
        }

        // RED4ext plugins built for another game version are skipped by RED4ext anyway; turning them off keeps things tidy.
        var red4extLogs = new DirectoryInfo(Path.Combine(gameRoot, "red4ext", "logs"));
        if (red4extLogs.Exists)
        {
            var latest = red4extLogs.GetFiles("red4ext-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest is not null && latest.LastWriteTimeUtc.Ticks > lastHandled)
            {
                summary.ReadNewGameLogs = true;
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
            summary.LastLaunchNotes.Add($"{prefix}: {reason}, but it wasn't installed by Afterburn, so remove it by hand.");

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
        // Most paths have one owner, so only remember the first owner and make a set when a second one turns up.
        var groups = new Dictionary<EntityId, string?>(); // mod group -> name, or null for Afterburn's own groups
        var firstOwner = new Dictionary<string, EntityId>(StringComparer.OrdinalIgnoreCase);
        var shared = new Dictionary<string, HashSet<EntityId>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var item = file.AsLoadoutItem();
            if (!item.IsEnabled() || !item.HasParent()) continue;
            var group = item.Parent;
            if (!groups.TryGetValue(group.Id, out var name))
            {
                var ownGroup = LoadoutOverridesGroup.Load(group.Db, group.Id).IsValid() || LoadoutGameFilesGroup.Load(group.Db, group.Id).IsValid();
                groups[group.Id] = name = ownGroup ? null : group.AsLoadoutItem().Name;
            }
            if (name is null) continue;

            var path = file.TargetPath.Item3.ToString().Replace('\\', '/');
            if (!firstOwner.TryAdd(path, group.Id))
            {
                var first = firstOwner[path];
                if (first.Equals(group.Id)) continue;
                if (!shared.TryGetValue(path, out var set)) shared[path] = set = [first];
                set.Add(group.Id);
            }
        }

        // One line per set of mods, covering every file they share.
        var byModSet = shared
            .GroupBy(kv => string.Join(" + ", kv.Value.Select(id => groups[id]!).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase)))
            .Where(g => g.Key.Contains(" + ", StringComparison.Ordinal))
            .OrderByDescending(g => g.Count());
        foreach (var g in byModSet)
            summary.Overwrites.Add($"{g.Key}: {g.Count()} shared file(s), e.g. {g.First().Key}");
    }

    // ---------------------------------------------------------------- archive conflicts

    private void ResolveArchiveConflicts(string gameRoot, ConflictSummary summary)
    {
        var modDir = new DirectoryInfo(Path.Combine(gameRoot, "archive", "pc", "mod"));
        if (!modDir.Exists) return;

        var archives = modDir.GetFiles("*.archive", SearchOption.TopDirectoryOnly);
        Array.Sort(archives, static (x, y) => string.CompareOrdinal(x.Name, y.Name));

        // Nothing added, removed or changed since the last check: reuse its result instead of reading the archives again.
        var fingerprint = Fingerprint(archives);
        var scan = _lastArchiveScan is { } cached && cached.Fingerprint == fingerprint ? cached : ScanArchives(archives, fingerprint);
        _lastArchiveScan = scan;

        var conflicting = scan.Conflicting;
        var entryCounts = scan.EntryCounts;
        var pairCounts = scan.PairCounts;

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

        var position = new int[archives.Length];
        for (var p = 0; p < winnersFirst.Count; p++) position[winnersFirst[p]] = p;

        foreach (var ((a, b), count) in pairCounts.OrderByDescending(p => p.Value))
        {
            var winner = position[a] < position[b] ? a : b;
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

    private sealed record ArchiveScan(long Fingerprint, HashSet<int> Conflicting, int[] EntryCounts, Dictionary<(int, int), int> PairCounts);

    private ArchiveScan? _lastArchiveScan;
    private ScriptScan? _lastScriptScan;

    private static long Fingerprint(IReadOnlyList<FileInfo> files)
    {
        var hash = new HashCode();
        foreach (var file in files)
        {
            hash.Add(file.FullName, StringComparer.OrdinalIgnoreCase);
            hash.Add(file.Length);
            hash.Add(file.LastWriteTimeUtc.Ticks);
        }
        return ((long)files.Count << 32) ^ (uint)hash.ToHashCode();
    }

    private static ArchiveScan ScanArchives(FileInfo[] archives, long fingerprint)
    {
        // Read only each archive's index, several at once.
        var hashes = new ulong[archives.Length][];
        Parallel.For(0, archives.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) },
            i => hashes[i] = ArchiveIndexReader.TryReadResourceHashes(archives[i].FullName) ?? []);

        // One flat, sorted list of (resource, archive) instead of a list per resource: far less memory.
        var total = 0;
        var entryCounts = new int[archives.Length];
        for (var i = 0; i < archives.Length; i++)
        {
            entryCounts[i] = hashes[i].Length;
            total += hashes[i].Length;
        }

        var keys = new ulong[total];
        var owners = new int[total];
        var n = 0;
        for (var i = 0; i < archives.Length; i++)
        {
            hashes[i].CopyTo(keys, n);
            owners.AsSpan(n, hashes[i].Length).Fill(i);
            n += hashes[i].Length;
            hashes[i] = [];
        }
        Array.Sort(keys, owners);

        var conflicting = new HashSet<int>();
        var pairCounts = new Dictionary<(int, int), int>();
        for (var start = 0; start < total;)
        {
            var end = start + 1;
            while (end < total && keys[end] == keys[start]) end++;
            if (end - start > 1)
            {
                for (var x = start; x < end; x++)
                {
                    conflicting.Add(owners[x]);
                    for (var y = x + 1; y < end; y++)
                    {
                        var key = (Math.Min(owners[x], owners[y]), Math.Max(owners[x], owners[y]));
                        pairCounts[key] = pairCounts.GetValueOrDefault(key) + 1;
                    }
                }
            }
            start = end;
        }

        return new ArchiveScan(fingerprint, conflicting, entryCounts, pairCounts);
    }

    private static bool AuthorWantsFirst(string name) => name.Length > 0 && !char.IsAsciiLetter(name[0]);

    private static bool AuthorWantsLast(string name) => name.StartsWith("zz", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- script conflicts

    private sealed record ScriptScan(long Fingerprint, IReadOnlyList<string> Conflicts);

    private void FindScriptConflicts(string gameRoot, ConflictSummary summary)
    {
        var scriptsDir = new DirectoryInfo(Path.Combine(gameRoot, "r6", "scripts"));
        if (!scriptsDir.Exists) return;

        var files = scriptsDir.GetFiles("*.reds", SearchOption.AllDirectories);
        Array.Sort(files, static (x, y) => string.CompareOrdinal(x.FullName, y.FullName));

        // Nothing changed since the last check: reuse its result instead of reading every script again.
        var fingerprint = Fingerprint(files);
        var scan = _lastScriptScan;
        if (scan is null || scan.Fingerprint != fingerprint)
        {
            scan = ScanScripts(scriptsDir.FullName, files, fingerprint);
            _lastScriptScan = scan;
        }

        summary.ScriptConflicts.AddRange(scan.Conflicts);
    }

    private static ScriptScan ScanScripts(string scriptsRoot, FileInfo[] files, long fingerprint)
    {
        var replacements = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, file =>
        {
            var relative = Path.GetRelativePath(scriptsRoot, file.FullName);
            var sep = relative.IndexOfAny(['\\', '/']);
            var mod = sep < 0 ? Path.GetFileNameWithoutExtension(relative) : relative[..sep];

            string text;
            try { text = File.ReadAllText(file.FullName); } catch { return; }
            if (!text.Contains("@replaceMethod", StringComparison.Ordinal)) return;

            MatchCollection matches;
            try { matches = ReplaceMethodRegex.Matches(text); _ = matches.Count; }
            catch (RegexMatchTimeoutException) { return; }

            foreach (Match m in matches)
            {
                var key = $"{m.Groups["cls"].Value}.{m.Groups["fn"].Value}";
                replacements.GetOrAdd(key, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)).TryAdd(mod, 0);
            }
        });

        var conflicts = replacements.Where(kv => kv.Value.Count > 1)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} is replaced by {string.Join(", ", kv.Value.Keys.Order(StringComparer.OrdinalIgnoreCase))} (only one can take effect)")
            .ToList();
        return new ScriptScan(fingerprint, conflicts);
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

    /// <summary>Files from the last launch's logs that Afterburn didn't install.</summary>
    public List<string> LastLaunchNotes { get; } = [];

    /// <summary>True when this check read game logs from a launch it hadn't seen before.</summary>
    public bool ReadNewGameLogs { get; set; }

    public bool ModListChanged { get; set; }

    public bool HasAnything => BrokenMods.Count > 0 || ArchiveConflicts.Count > 0 || ScriptConflicts.Count > 0 || Overwrites.Count > 0;

    /// <summary>True when Afterburn changed something this launch, so the toast doesn't repeat every time.</summary>
    public bool ShouldNotify => (BrokenMods.Count > 0 && !CarriedOver) || ModListChanged;

    /// <summary>True when the last-launch findings were copied from the previous report rather than found now.</summary>
    public bool CarriedOver { get; private set; }

    public string ToShortText()
    {
        var parts = new List<string>();
        if (BrokenMods.Count > 0) parts.Add($"{BrokenMods.Count} mod(s) failed to load last time; check them and update or turn them off");
        if (ArchiveConflicts.Count > 0) parts.Add($"ordered {ArchiveConflicts.Count} archive conflict(s)");
        if (ScriptConflicts.Count > 0) parts.Add($"found {ScriptConflicts.Count} script conflict(s)");
        if (Overwrites.Count > 0) parts.Add($"{Overwrites.Count} mod overlap(s) handled by load order");
        if (parts.Count > 0) parts.Add("full report on the Conflicts tab of the mod list");
        return parts.Count == 0 ? "no conflicts found" : "Conflict check: " + string.Join("; ", parts);
    }

    private const string FailedTitle = "Mods that failed on the last launch";
    private const string FailedExplanation = "Nothing was turned off. If many are listed, an outdated framework mod such as Codeware, ArchiveXL, TweakXL or RED4ext is the usual cause; update it or turn the listed mods off.";

    /// <summary>Keeps the previous report's last-launch findings when the game hasn't run since.</summary>
    public void CarryOverLastLaunch(ConflictReport previous)
    {
        var failed = previous.Sections.FirstOrDefault(s => s.Title == FailedTitle);
        if (failed is null) return;
        CarriedOver = true;
        foreach (var item in failed.Items)
        {
            if (item.EndsWith("so remove it by hand.", StringComparison.Ordinal)) LastLaunchNotes.Add(item);
            else BrokenMods.Add(item);
        }
    }

    /// <summary>The full report, in the form the Conflicts tab shows it.</summary>
    public ConflictReport ToConflictReport(DateTimeOffset checkedAt)
    {
        var sections = new List<ConflictReportSection>
        {
            new(FailedTitle, FailedExplanation, [.. BrokenMods, .. LastLaunchNotes]),
            new("Script conflicts", "Two or more mods replace the same game function. The game uses only one of them, so the other mods' change to that function won't work. Keep the one you want, or look for a compatibility patch.", [.. ScriptConflicts]),
            new("Archive conflicts", "Mods that change the same game resources. In each line the first mod's version is the one the game uses; Afterburn sets this order in archive/pc/mod/modlist.txt unless you manage that file yourself.", [.. ArchiveConflicts]),
            new("Same-file overwrites", "Mods that install the same file. The load order picks the winner; you can change it on the Rules tab.", [.. Overwrites]),
            new("Notes", "", [.. Notes]),
        };

        var counts = new List<string>();
        if (BrokenMods.Count + LastLaunchNotes.Count > 0) counts.Add($"{BrokenMods.Count + LastLaunchNotes.Count} failed on the last launch");
        if (ScriptConflicts.Count > 0) counts.Add($"{ScriptConflicts.Count} script conflict(s)");
        if (ArchiveConflicts.Count > 0) counts.Add($"{ArchiveConflicts.Count} archive conflict(s)");
        if (Overwrites.Count > 0) counts.Add($"{Overwrites.Count} overwrite(s)");
        var text = counts.Count == 0 ? "No conflicts found." : string.Join(", ", counts) + ".";

        return new ConflictReport(checkedAt, text, sections.Where(s => s.Items.Count > 0).ToList());
    }

    public string ToReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Afterburn conflict check - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        Section(sb, "Mods that failed on the last launch (nothing was turned off; if many are listed, an outdated framework mod such as Codeware, ArchiveXL, TweakXL or RED4ext is the usual cause)", [.. BrokenMods, .. LastLaunchNotes]);
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
