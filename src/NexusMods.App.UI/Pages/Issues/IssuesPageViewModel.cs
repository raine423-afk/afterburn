using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.Issues;

/// <summary>Afterburn: one mod on the "By mod" tab of the Issues page.</summary>
public sealed class ModConflictRow
{
    public required string Name { get; init; }
    public required int Wins { get; init; }
    public required int Losses { get; init; }
    public required IReadOnlyList<(string Name, int Files)> OpponentList { get; init; }

    public string Counts => $"Its copy is used for {Wins:N0} shared file{(Wins == 1 ? "" : "s")} · another mod's copy is used for {Losses:N0}";

    public string Opponents
    {
        get
        {
            const int shown = 8;
            var parts = OpponentList.Take(shown).Select(o => $"{o.Name} ({o.Files:N0})");
            var more = OpponentList.Count > shown ? $" and {OpponentList.Count - shown:N0} more" : "";
            return $"Shares files with: {string.Join(", ", parts)}{more}";
        }
    }

    internal bool Matches(string search) =>
        Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || OpponentList.Any(o => o.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Afterburn: one conflicting file on the Issues page.</summary>
public sealed class FileConflictRow
{
    public required string Path { get; init; }
    public required string Winner { get; init; }
    public required IReadOnlyList<string> Losers { get; init; }

    public string WinnerText => $"Used: {Winner}";
    public string LosersText => $"Hidden: {string.Join(", ", Losers)}";

    internal bool Matches(string search) =>
        Path.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Winner.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Losers.Any(l => l.Contains(search, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Afterburn: one Health Check result on the Issues page.</summary>
public sealed class HealthIssueRow
{
    private static readonly IImmutableBrush CriticalBrush = new ImmutableSolidColorBrush(Color.Parse("#F87171"));
    private static readonly IImmutableBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IImmutableBrush SuggestionBrush = new ImmutableSolidColorBrush(Color.Parse("#60A5FA"));

    public required DiagnosticSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Details { get; init; }

    public string SeverityText => Severity switch
    {
        DiagnosticSeverity.Critical => "Critical",
        DiagnosticSeverity.Warning => "Warning",
        _ => "Suggestion",
    };

    public IBrush SeverityBrush => Severity switch
    {
        DiagnosticSeverity.Critical => CriticalBrush,
        DiagnosticSeverity.Warning => WarningBrush,
        _ => SuggestionBrush,
    };

    internal bool Matches(string search) =>
        Title.Contains(search, StringComparison.OrdinalIgnoreCase) || Details.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Afterburn: one error or warning from a game mod log on the Issues page.</summary>
public sealed class LogIssueRow
{
    private static readonly IImmutableBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#F87171"));
    private static readonly IImmutableBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse("#FBBF24"));

    public required bool IsError { get; init; }
    public required string Source { get; init; }
    public required string File { get; init; }
    public required DateTime FileTime { get; init; }
    public required int LineNumber { get; init; }
    public required string Line { get; init; }
    public required int Repeats { get; init; }

    public string LevelText => IsError ? "Error" : "Warning";
    public IBrush LevelBrush => IsError ? ErrorBrush : WarningBrush;

    public string FileText
    {
        get
        {
            var repeats = Repeats > 1 ? $" · seen {Repeats:N0} times" : "";
            return $"{File}, line {LineNumber:N0} · {IssuesPageViewModel.Ago(FileTime)}{repeats}";
        }
    }

    internal bool Matches(string search) =>
        Line.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Source.Contains(search, StringComparison.OrdinalIgnoreCase)
        || File.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Afterburn: the Issues page. Gathers, in one place and in full: every file conflict (which mod's copy is used and
/// which are hidden), a per-mod summary of those conflicts, every Health Check result with its full explanation,
/// and the errors and warnings in the game's own mod-tool logs. It can copy or save everything as one text report.
/// </summary>
[UsedImplicitly]
public partial class IssuesPageViewModel : APageViewModel<IIssuesPageViewModel>, IIssuesPageViewModel
{
    private const int MaxLogLinesPerFile = 300;
    private const int MaxLogLinesTotal = 3000;
    private const long MaxLogBytesRead = 4L * 1024 * 1024;

    private readonly IConnection _connection;
    private readonly IDiagnosticManager _diagnosticManager;
    private readonly IDiagnosticWriter _diagnosticWriter;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IAvaloniaInterop _avaloniaInterop;
    private readonly IOSInterop _osInterop;
    private readonly ILogger<IssuesPageViewModel> _logger;

    private IReadOnlyList<ModConflictRow> _allMods = [];
    private IReadOnlyList<FileConflictRow> _allConflicts = [];
    private IReadOnlyList<HealthIssueRow> _allHealth = [];
    private IReadOnlyList<LogIssueRow> _allLogs = [];
    private string _logsNote = string.Empty;

    public IssuesPageViewModel(
        IWindowManager windowManager,
        IConnection connection,
        IDiagnosticManager diagnosticManager,
        IDiagnosticWriter diagnosticWriter,
        ILoadoutManager loadoutManager,
        IAvaloniaInterop avaloniaInterop,
        IOSInterop osInterop,
        ILogger<IssuesPageViewModel> logger) : base(windowManager)
    {
        _connection = connection;
        _diagnosticManager = diagnosticManager;
        _diagnosticWriter = diagnosticWriter;
        _loadoutManager = loadoutManager;
        _avaloniaInterop = avaloniaInterop;
        _osInterop = osInterop;
        _logger = logger;

        TabIcon = IconValues.WarningAmber;
        TabTitle = "Issues";

        var notBusy = this.WhenAnyValue(vm => vm.IsBusy).Select(busy => !busy);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync, notBusy);
        CopyReportCommand = ReactiveCommand.CreateFromTask(CopyReportAsync, notBusy);
        SaveReportCommand = ReactiveCommand.CreateFromTask(SaveReportAsync, notBusy);
        AutoResolveCommand = ReactiveCommand.CreateFromTask(AutoResolveAsync, notBusy);

        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(vm => vm.SearchText)
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ApplyFilter())
                .DisposeWith(disposables);

            RefreshCommand.Execute().Subscribe(_ => { }, e => _logger.LogError(e, "Couldn't refresh the Issues page")).DisposeWith(disposables);
        });
    }

    [Reactive] public LoadoutId LoadoutId { get; set; }
    [Reactive] public string Summary { get; private set; } = "Looking for issues…";
    [Reactive] public string Status { get; private set; } = string.Empty;
    [Reactive] public bool IsBusy { get; private set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;

    [Reactive] public string ModsHeader { get; private set; } = "By mod";
    [Reactive] public string ConflictsHeader { get; private set; } = "File conflicts";
    [Reactive] public string HealthHeader { get; private set; } = "Health Check";
    [Reactive] public string LogsHeader { get; private set; } = "Mod logs";

    [Reactive] public IReadOnlyList<ModConflictRow> VisibleMods { get; private set; } = [];
    [Reactive] public IReadOnlyList<FileConflictRow> VisibleConflicts { get; private set; } = [];
    [Reactive] public IReadOnlyList<HealthIssueRow> VisibleHealth { get; private set; } = [];
    [Reactive] public IReadOnlyList<LogIssueRow> VisibleLogs { get; private set; } = [];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyReportCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveReportCommand { get; }
    public ReactiveCommand<Unit, Unit> AutoResolveCommand { get; }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Looking for issues…";
        try
        {
            var loadoutId = LoadoutId;

            var conflictsTask = Task.Run(() => GatherConflicts(loadoutId));
            var logsTask = Task.Run(() => GatherGameLogs(loadoutId));
            var health = await GatherHealthAsync(loadoutId);

            var (conflicts, mods) = await SafeAwait<(IReadOnlyList<FileConflictRow>, IReadOnlyList<ModConflictRow>)>(conflictsTask, (Array.Empty<FileConflictRow>(), Array.Empty<ModConflictRow>()), "file conflicts");
            var (logs, logsNote) = await SafeAwait<(IReadOnlyList<LogIssueRow>, string)>(logsTask, (Array.Empty<LogIssueRow>(), "Couldn't read the game's mod logs."), "game logs");

            _allConflicts = conflicts;
            _allMods = mods;
            _allHealth = health;
            _allLogs = logs;
            _logsNote = logsNote;

            var critical = health.Count(h => h.Severity == DiagnosticSeverity.Critical);
            var warnings = health.Count(h => h.Severity == DiagnosticSeverity.Warning);
            var logErrors = logs.Count(l => l.IsError);

            Summary = $"{conflicts.Count:N0} conflicting file{(conflicts.Count == 1 ? "" : "s")} across {mods.Count:N0} mod{(mods.Count == 1 ? "" : "s")} · " +
                      $"Health Check: {critical:N0} critical, {warnings:N0} warning{(warnings == 1 ? "" : "s")}, {health.Count - critical - warnings:N0} suggestion{(health.Count - critical - warnings == 1 ? "" : "s")} · " +
                      $"mod logs: {logErrors:N0} error{(logErrors == 1 ? "" : "s")}, {logs.Count - logErrors:N0} warning{(logs.Count - logErrors == 1 ? "" : "s")}";
            Status = string.IsNullOrEmpty(logsNote) ? $"Checked {DateTime.Now:t}." : $"Checked {DateTime.Now:t}. {logsNote}";

            ApplyFilter();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't gather issues");
            Status = "Something went wrong while looking for issues. Press Refresh to try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<T> SafeAwait<T>(Task<T> task, T fallback, string what)
    {
        try
        {
            return await task;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't gather {What} for the Issues page", what);
            return fallback;
        }
    }

    private void ApplyFilter()
    {
        var search = SearchText?.Trim() ?? string.Empty;
        if (search.Length == 0)
        {
            VisibleMods = _allMods;
            VisibleConflicts = _allConflicts;
            VisibleHealth = _allHealth;
            VisibleLogs = _allLogs;
        }
        else
        {
            VisibleMods = _allMods.Where(r => r.Matches(search)).ToArray();
            VisibleConflicts = _allConflicts.Where(r => r.Matches(search)).ToArray();
            VisibleHealth = _allHealth.Where(r => r.Matches(search)).ToArray();
            VisibleLogs = _allLogs.Where(r => r.Matches(search)).ToArray();
        }

        ModsHeader = Header("By mod", VisibleMods.Count, _allMods.Count);
        ConflictsHeader = Header("File conflicts", VisibleConflicts.Count, _allConflicts.Count);
        HealthHeader = Header("Health Check", VisibleHealth.Count, _allHealth.Count);
        LogsHeader = Header("Mod logs", VisibleLogs.Count, _allLogs.Count);
    }

    private static string Header(string name, int visible, int total) =>
        visible == total ? $"{name} ({total:N0})" : $"{name} ({visible:N0} of {total:N0})";

    // ---------------------------------------------------------------- file conflicts

    private (IReadOnlyList<FileConflictRow> Conflicts, IReadOnlyList<ModConflictRow> Mods) GatherConflicts(LoadoutId loadoutId)
    {
        var db = _connection.Db;

        // One row per (conflicting file, hidden copy). The winner is the copy the game gets.
        var rows = _connection.Query<(EntityId WinnerPriorityId, EntityId WinnerItemId, EntityId LoserPriorityId, EntityId LoserItemId)>(
            $"""
             SELECT
               conflicting_path.Winner.PriorityId,
               conflicting_path.Winner.LoadoutItemId,
               losers."unnest".PriorityId,
               losers."unnest".LoadoutItemId
             FROM
               synchronizer.ConflictingPaths ({_connection}) conflicting_path
               CROSS JOIN unnest(conflicting_path.Losers) losers
             WHERE
               conflicting_path.Loadout = {loadoutId};
             """
        ).ToArray();

        var names = new Dictionary<EntityId, string>();
        string NameOf(EntityId priorityId)
        {
            if (names.TryGetValue(priorityId, out var name)) return name;
            try
            {
                name = LoadoutItemGroupPriority.Load(db, priorityId).Target.AsLoadoutItem().Name;
            }
            catch
            {
                name = "(unknown mod)";
            }
            if (string.IsNullOrWhiteSpace(name)) name = "(unnamed mod)";
            names[priorityId] = name;
            return name;
        }

        var conflicts = new List<FileConflictRow>();
        var perMod = new Dictionary<EntityId, (int Wins, int Losses, Dictionary<EntityId, int> Opponents)>();

        (int Wins, int Losses, Dictionary<EntityId, int> Opponents) Stats(EntityId id) =>
            perMod.TryGetValue(id, out var s) ? s : (0, 0, new Dictionary<EntityId, int>());

        foreach (var group in rows.GroupBy(r => r.WinnerItemId))
        {
            var first = group.First();
            string path;
            try
            {
                GamePath gamePath = LoadoutItemWithTargetPath.Load(db, first.WinnerItemId).TargetPath;
                path = gamePath.ToString();
            }
            catch
            {
                path = "(unknown file)";
            }

            var winnerId = first.WinnerPriorityId;
            var loserIds = group.Select(r => r.LoserPriorityId).Where(id => id != winnerId).Distinct().ToArray();
            if (loserIds.Length == 0) continue;

            conflicts.Add(new FileConflictRow
            {
                Path = path,
                Winner = NameOf(winnerId),
                Losers = loserIds.Select(NameOf).ToArray(),
            });

            var winnerStats = Stats(winnerId);
            winnerStats.Wins++;
            foreach (var loserId in loserIds)
            {
                winnerStats.Opponents[loserId] = winnerStats.Opponents.GetValueOrDefault(loserId) + 1;

                var loserStats = Stats(loserId);
                loserStats.Losses++;
                loserStats.Opponents[winnerId] = loserStats.Opponents.GetValueOrDefault(winnerId) + 1;
                perMod[loserId] = loserStats;
            }
            perMod[winnerId] = winnerStats;
        }

        var mods = perMod
            .Select(kv => new ModConflictRow
            {
                Name = NameOf(kv.Key),
                Wins = kv.Value.Wins,
                Losses = kv.Value.Losses,
                OpponentList = kv.Value.Opponents
                    .OrderByDescending(o => o.Value)
                    .Select(o => (NameOf(o.Key), o.Value))
                    .ToArray(),
            })
            // Mods that lose the most files first: those are the ones most likely to look "broken".
            .OrderByDescending(m => m.Losses)
            .ThenByDescending(m => m.Wins)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sortedConflicts = conflicts
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return (sortedConflicts, mods);
    }

    // ---------------------------------------------------------------- Health Check

    private async Task<IReadOnlyList<HealthIssueRow>> GatherHealthAsync(LoadoutId loadoutId)
    {
        try
        {
            var diagnostics = await _diagnosticManager
                .GetLoadoutDiagnostics(loadoutId)
                .Timeout(TimeSpan.FromSeconds(60))
                .FirstAsync();

            return diagnostics
                .Where(d => d.Severity != DiagnosticSeverity.Hidden)
                .OrderByDescending(d => d.Severity)
                .Select(d => new HealthIssueRow
                {
                    Severity = d.Severity,
                    Title = SafeFormat(() => d.FormatSummary(_diagnosticWriter, DiagnosticWriterMode.PlainText)),
                    Details = SafeFormat(() => d.FormatDetails(_diagnosticWriter, DiagnosticWriterMode.PlainText)),
                })
                .ToArray();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't get Health Check results for the Issues page");
            return [];
        }
    }

    private static string SafeFormat(Func<string> format)
    {
        try
        {
            return format().Trim();
        }
        catch
        {
            return "(couldn't show this message)";
        }
    }

    // ---------------------------------------------------------------- game mod logs

    [GeneratedRegex(@"\b0 errors?\b|\bno errors?\b|\berrors?: 0\b|\bwithout errors?\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAnErrorRegex();

    [GeneratedRegex(@"\[(error|err|e|critical|crit|fatal)\]|\berror\b|\bexception\b|\bfatal\b|\bfailed\b|\bfailure\b|\bcrash", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex(@"\[(warn|warning|w)\]|\bwarning\b|\bwarn\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningRegex();

    // Leading timestamps like "[2026-11-21 07:00:00.123]" or "07:00:00.123", so repeated messages can be grouped.
    [GeneratedRegex(@"^\s*\[?\d{1,4}[-/.:]\d{1,2}[-/.:\dT ,+Z]*\]?\s*")]
    private static partial Regex TimestampRegex();

    private (IReadOnlyList<LogIssueRow> Logs, string Note) GatherGameLogs(LoadoutId loadoutId)
    {
        var loadout = Loadout.Load(_connection.Db, loadoutId);
        var gameRoot = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "")).GetFullPath();

        var sources = new List<(string Folder, string Pattern, bool Recursive)>
        {
            (Path.Combine(gameRoot, "red4ext", "logs"), "*.log", false),
            (Path.Combine(gameRoot, "red4ext", "plugins"), "*.log", true),
            (Path.Combine(gameRoot, "r6", "logs"), "*.log", false),
            (Path.Combine(gameRoot, "r6", "cache", "modded"), "*.log", false),
            (Path.Combine(gameRoot, "tools", "redmod", "bin"), "*.log", false),
            (Path.Combine(gameRoot, "bin", "x64", "plugins", "cyber_engine_tweaks"), "*.log", true),
        };

        var existing = sources.Where(s => Directory.Exists(s.Folder)).ToArray();
        if (existing.Length == 0)
            return (Array.Empty<LogIssueRow>(), "This game has no mod-tool logs Afterburn knows how to read.");

        var results = new List<LogIssueRow>();
        var filesRead = 0;

        foreach (var (folder, pattern, recursive) in existing)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = recursive,
                    MaxRecursionDepth = 4,
                }).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (results.Count >= MaxLogLinesTotal) break;
                try
                {
                    results.AddRange(ReadLogFile(gameRoot, file));
                    filesRead++;
                }
                catch (Exception e)
                {
                    _logger.LogDebug(e, "Couldn't read {File}", file);
                }
            }
        }

        var ordered = results
            .Take(MaxLogLinesTotal)
            .OrderByDescending(r => r.IsError)
            .ThenByDescending(r => r.FileTime)
            .ThenBy(r => r.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.LineNumber)
            .ToArray();

        var note = filesRead == 0
            ? "No mod logs found yet. Start the game once and refresh."
            : results.Count >= MaxLogLinesTotal ? $"Showing the first {MaxLogLinesTotal:N0} log problems." : string.Empty;
        return (ordered, note);
    }

    private static IEnumerable<LogIssueRow> ReadLogFile(string gameRoot, string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0) yield break;

        string text;
        var skippedLines = 0;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // Very large logs: only read the end, which is the most recent run.
            if (stream.Length > MaxLogBytesRead)
            {
                stream.Seek(-MaxLogBytesRead, SeekOrigin.End);
                skippedLines = -1;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }

        var relative = Path.GetRelativePath(gameRoot, file).Replace('\\', '/');
        var source = SourceFor(relative);
        var lines = text.Split('\n');

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<(bool IsError, int LineNumber, string Line, string Key)>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (skippedLines == -1 && i == 0) continue; // probably a partial line
            var line = lines[i].TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            bool isError;
            if (ErrorRegex().IsMatch(line) && !NotAnErrorRegex().IsMatch(line)) isError = true;
            else if (WarningRegex().IsMatch(line)) isError = false;
            else continue;

            if (line.Length > 600) line = line[..600] + "…";
            var key = TimestampRegex().Replace(line, string.Empty);

            if (seen.TryGetValue(key, out var count))
            {
                seen[key] = count + 1;
                continue;
            }

            seen[key] = 1;
            if (rows.Count < MaxLogLinesPerFile) rows.Add((isError, i + 1, line, key));
        }

        foreach (var row in rows)
        {
            yield return new LogIssueRow
            {
                IsError = row.IsError,
                Source = source,
                File = relative,
                FileTime = info.LastWriteTime,
                LineNumber = skippedLines == -1 ? 0 : row.LineNumber,
                Line = row.Line,
                Repeats = seen[row.Key],
            };
        }
    }

    private static string SourceFor(string relativePath)
    {
        var p = relativePath.ToLowerInvariant();
        if (p.Contains("redscript")) return "redscript";
        if (p.Contains("cyber_engine_tweaks"))
        {
            // CET mods log to mods/<name>/<name>.log
            var parts = relativePath.Split('/');
            var modsIndex = Array.FindIndex(parts, s => s.Equals("mods", StringComparison.OrdinalIgnoreCase));
            return modsIndex >= 0 && modsIndex + 1 < parts.Length - 1 ? $"CET mod {parts[modsIndex + 1]}" : "Cyber Engine Tweaks";
        }
        if (p.StartsWith("red4ext/plugins/"))
        {
            var parts = relativePath.Split('/');
            return parts.Length > 2 ? parts[2] : "RED4ext plugin";
        }
        if (p.StartsWith("red4ext/")) return "RED4ext";
        if (p.Contains("redmod") || p.StartsWith("r6/cache/modded")) return "REDmod";
        return Path.GetFileNameWithoutExtension(relativePath);
    }

    internal static string Ago(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(60)) return $"{(int)span.TotalDays} days ago";
        return time.ToString("d");
    }

    // ---------------------------------------------------------------- actions

    private async Task AutoResolveAsync()
    {
        IsBusy = true;
        Status = "Re-running the automatic conflict rules…";
        try
        {
            await _loadoutManager.AutoResolveFileConflicts(LoadoutId);
            Status = "Done. Press Apply to put the new winners in your game folder.";
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Automatic conflict resolution failed");
            Status = "Couldn't fix conflicts automatically. See the log for details.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private async Task CopyReportAsync()
    {
        try
        {
            var report = await Task.Run(BuildReport);
            await _avaloniaInterop.SetClipboardTextAsync(report);
            Status = $"Copied the full report ({report.Length / 1024.0:N0} KB of text) to the clipboard.";
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't copy the issues report");
            Status = "Couldn't copy the report. Try Save report instead.";
        }
    }

    private async Task SaveReportAsync()
    {
        try
        {
            var path = await Task.Run(() =>
            {
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                var target = Path.Combine(downloads, $"Afterburn-issues-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(target, BuildReport(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return target;
            });

            Status = $"Saved the report to {path}";
            try { _osInterop.OpenFileInDirectory(FileSystem.Shared.FromUnsanitizedFullPath(path)); }
            catch { /* optional */ }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't save the issues report");
            Status = "Couldn't save the report.";
        }
    }

    private string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Afterburn {ApplicationConstants.Version} - issues report - {DateTime.Now:yyyy-MM-dd HH:mm}");
        try
        {
            var loadout = Loadout.Load(_connection.Db, LoadoutId);
            sb.AppendLine($"Loadout: {loadout.Name}");
        }
        catch { /* optional */ }
        sb.AppendLine(Summary);
        sb.AppendLine();

        sb.AppendLine($"==== HEALTH CHECK ({_allHealth.Count:N0}) ====");
        foreach (var h in _allHealth)
        {
            sb.AppendLine($"[{h.SeverityText}] {h.Title}");
            if (h.Details.Length > 0)
            {
                foreach (var line in h.Details.Split('\n')) sb.AppendLine($"    {line.TrimEnd('\r')}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"==== CONFLICTS BY MOD ({_allMods.Count:N0}) ====");
        foreach (var m in _allMods)
        {
            sb.AppendLine(m.Name);
            sb.AppendLine($"    used for {m.Wins:N0} shared files, hidden for {m.Losses:N0}");
            sb.AppendLine($"    shares files with: {string.Join(", ", m.OpponentList.Select(o => $"{o.Name} ({o.Files:N0})"))}");
        }
        sb.AppendLine();

        sb.AppendLine($"==== FILE CONFLICTS ({_allConflicts.Count:N0}) ====");
        foreach (var c in _allConflicts)
        {
            sb.AppendLine(c.Path);
            sb.AppendLine($"    used:   {c.Winner}");
            sb.AppendLine($"    hidden: {string.Join(", ", c.Losers)}");
        }
        sb.AppendLine();

        sb.AppendLine($"==== MOD LOGS ({_allLogs.Count:N0}) ====");
        if (_logsNote.Length > 0) sb.AppendLine(_logsNote);
        foreach (var l in _allLogs)
        {
            sb.AppendLine($"[{l.LevelText}] {l.Source} - {l.FileText}");
            sb.AppendLine($"    {l.Line}");
        }

        return RedactUser(sb.ToString());
    }

    private static string RedactUser(string text)
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                           .Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            }
            var user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length >= 3)
                text = Regex.Replace(text, $@"(?<=[\\/]Users[\\/]){Regex.Escape(user)}(?=[\\/])", "<user>", RegexOptions.IgnoreCase);
        }
        catch { /* best effort */ }
        return text;
    }
}
