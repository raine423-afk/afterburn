using NexusMods.UI.Sdk.Dialog.Enums;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Dialog;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GC;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.Storage;

/// <summary>
/// Afterburn: one row on the Storage page, a game folder holding files Afterburn didn't install.
/// </summary>
public class UnmanagedFolderRow : ReactiveObject
{
    public required string Folder { get; init; }
    public string DisplayName => Folder.Length == 0 ? "(game folder itself)" : Folder;
    public required LocationId Location { get; init; }
    public required int FileCount { get; init; }
    public required long Bytes { get; init; }

    public string Details => $"{FileCount:N0} files · {StoragePageViewModel.FormatBytes(Bytes)}";

    [Reactive] public bool IsExcluded { get; set; }

    public string BackupButtonText => IsExcluded ? "Back up again" : "Stop backing up";
    public string BackupStateText => IsExcluded ? "Not backed up" : "Backed up";

    public required ReactiveCommand<Unit, Unit> ToggleBackupCommand { get; init; }
    public required ReactiveCommand<Unit, Unit> OpenFolderCommand { get; init; }
    public required ReactiveCommand<Unit, Unit> MakeModCommand { get; init; }

    public void RaiseBackupChanged()
    {
        this.RaisePropertyChanged(nameof(BackupButtonText));
        this.RaisePropertyChanged(nameof(BackupStateText));
    }
}

/// <summary>
/// Afterburn: a saved restore point (a copy of a loadout named "Restore point …").
/// </summary>
public class RestorePointRow
{
    public required string Name { get; init; }
    public required string Details { get; init; }
    public required ReactiveCommand<Unit, Unit> RestoreCommand { get; init; }
    public required ReactiveCommand<Unit, Unit> DeleteCommand { get; init; }
}

[UsedImplicitly]
public class StoragePageViewModel : APageViewModel<IStoragePageViewModel>, IStoragePageViewModel
{
    private readonly IConnection _connection;
    private readonly IFileStore _fileStore;
    private readonly IGarbageCollectorRunner _garbageCollector;
    private readonly IOSInterop _osInterop;
    private readonly ILogger<StoragePageViewModel> _logger;

    private readonly ObservableCollection<UnmanagedFolderRow> _folders = new();
    private readonly ObservableCollection<RestorePointRow> _restorePoints = new();
    private readonly ILoadoutManager _loadoutManager;
    private readonly ILibraryService _libraryService;
    private const string RestorePointPrefix = "Restore point";

    public StoragePageViewModel(
        IWindowManager windowManager,
        IConnection connection,
        IFileStore fileStore,
        IGarbageCollectorRunner garbageCollector,
        IOSInterop osInterop,
        ILoadoutManager loadoutManager,
        ILibraryService libraryService,
        ILogger<StoragePageViewModel> logger) : base(windowManager)
    {
        _loadoutManager = loadoutManager;
        _libraryService = libraryService;
        RestorePoints = new ReadOnlyObservableCollection<RestorePointRow>(_restorePoints);
        SaveRestorePointCommand = ReactiveCommand.CreateFromTask(SaveRestorePointAsync);
        _connection = connection;
        _fileStore = fileStore;
        _garbageCollector = garbageCollector;
        _osInterop = osInterop;
        _logger = logger;

        TabIcon = IconValues.FolderEditOutline;
        TabTitle = "Storage";

        UnmanagedFolders = new ReadOnlyObservableCollection<UnmanagedFolderRow>(_folders);

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        CleanupCommand = ReactiveCommand.CreateFromTask(CleanupAsync);
        OpenArchivesFolderCommand = ReactiveCommand.Create(() =>
        {
            if (_fileStore.ArchiveLocations.Count > 0) _osInterop.OpenDirectory(_fileStore.ArchiveLocations[0]);
        });

        CollectGameLogsCommand = ReactiveCommand.CreateFromTask(CollectGameLogsAsync);

        this.WhenActivated(disposables =>
        {
            RefreshCommand.Execute().Subscribe().DisposeWith(disposables);
        });
    }

    [Reactive] public LoadoutId LoadoutId { get; set; }
    [Reactive] public string StorageSummary { get; private set; } = "Working out how much space Afterburn uses…";
    [Reactive] public string FreeSpaceText { get; private set; } = string.Empty;
    [Reactive] public string CleanupStatus { get; private set; } = "Clean up removes backed-up and downloaded files that no mod or loadout uses any more.";
    [Reactive] public string MakeModStatus { get; private set; } = string.Empty;
    [Reactive] public string UnmanagedSummary { get; private set; } = "Looking for files in the game folder that Afterburn didn't install…";

    public ReadOnlyObservableCollection<UnmanagedFolderRow> UnmanagedFolders { get; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CleanupCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenArchivesFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> CollectGameLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveRestorePointCommand { get; }
    public ReadOnlyObservableCollection<RestorePointRow> RestorePoints { get; }
    [Reactive] public string RestorePointStatus { get; private set; } = "A restore point saves your current mod setup. If an update breaks the game, restore it to go straight back. It reuses the files Afterburn already has, so it takes almost no space.";
    [Reactive] public string GameLogsStatus { get; private set; } = "If the game crashes, this puts the Cyberpunk, CET, RED4ext and redscript logs, your mod list and recently changed game files into one zip in your Downloads folder.";

    internal static string FormatBytes(long bytes)
    {
        const double gb = 1024d * 1024 * 1024;
        const double mb = 1024d * 1024;
        return bytes >= gb ? $"{bytes / gb:0.0} GB" : bytes >= mb ? $"{bytes / mb:0} MB" : $"{Math.Max(0, bytes) / 1024d:0} KB";
    }

    private (long Bytes, int Archives) MeasureArchives()
    {
        long bytes = 0;
        var count = 0;
        foreach (var location in _fileStore.ArchiveLocations)
        {
            try
            {
                var dir = new DirectoryInfo(location.GetFullPath());
                if (!dir.Exists) continue;
                foreach (var file in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    bytes += file.Length;
                    count++;
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Couldn't measure archive folder {Folder}", location);
            }
        }
        return (bytes, count);
    }

    private string MeasureFreeSpace()
    {
        try
        {
            if (_fileStore.ArchiveLocations.Count == 0) return string.Empty;
            var root = Path.GetPathRoot(_fileStore.ArchiveLocations[0].GetFullPath());
            if (string.IsNullOrEmpty(root)) return string.Empty;
            var drive = new DriveInfo(root);
            return $"{FormatBytes(drive.AvailableFreeSpace)} free of {FormatBytes(drive.TotalSize)} on {root.TrimEnd('\\', '/')}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task RefreshAsync()
    {
        // Afterburn: this runs every time the page opens; a failure must show on the page, not as a crash popup.
        try
        {
            await RefreshCoreAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't refresh the Storage page");
            StorageSummary = "Afterburn couldn't read its storage details just now. Open the page again to retry.";
        }
    }

    private async Task RefreshCoreAsync()
    {
        var (archiveBytes, archiveCount, freeText) = await Task.Run(() =>
        {
            var (b, c) = MeasureArchives();
            return (b, c, MeasureFreeSpace());
        });

        StorageSummary = _fileStore.ArchiveLocations.Count == 0
            ? "Afterburn couldn't find its archive folder."
            : $"Afterburn's backups and downloaded mods take {FormatBytes(archiveBytes)} in {archiveCount:N0} archives, in {_fileStore.ArchiveLocations[0]}.";
        FreeSpaceText = freeText;

        LoadRestorePoints();

        var data = await Task.Run(GatherUnmanagedFolders);
        var rows = BuildUnmanagedRows(data);
        _folders.Clear();
        foreach (var row in rows) _folders.Add(row);

        var totalFiles = rows.Sum(r => r.FileCount);
        var totalBytes = rows.Sum(r => r.Bytes);
        UnmanagedSummary = totalFiles == 0
            ? "Every file in the game folder was installed by Afterburn (or is part of the game)."
            : $"{totalFiles:N0} files ({FormatBytes(totalBytes)}) in the game folder weren't installed by Afterburn. They show up as External Changes. " +
              "Afterburn keeps a backup copy of them so it can put them back; turn that off for folders you'd rather not duplicate.";
    }

    private List<((LocationId Location, string Folder) Key, (int Files, long Bytes) Value)> GatherUnmanagedFolders()
    {
        var groups = new Dictionary<(LocationId, string), (int Files, long Bytes)>();
        try
        {
            var db = _connection.Db;
            if (!LoadoutOverridesGroup.FindByOverridesFor(db, LoadoutId).TryGetFirst(out var overrides)) return [];

            foreach (var item in overrides.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            {
                var location = item.TargetPath.Item2;
                var path = item.TargetPath.Item3.ToString().Replace('\\', '/');
                var key = (location, FolderOf(path));
                long size = 0;
                if (item.TryGetAsLoadoutFile(out var file)) size = (long)file.Size.Value;
                groups.TryGetValue(key, out var acc);
                groups[key] = (acc.Files + 1, acc.Bytes + size);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't list the game-folder files Afterburn didn't install");
        }
        return groups.OrderByDescending(kv => kv.Value.Bytes).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private List<UnmanagedFolderRow> BuildUnmanagedRows(List<((LocationId Location, string Folder) Key, (int Files, long Bytes) Value)> data)
    {
        var result = new List<UnmanagedFolderRow>();
        try
        {
            var loadout = Loadout.Load(_connection.Db, LoadoutId);
            foreach (var ((location, folder), (files, bytes)) in data)
            {
                UnmanagedFolderRow? row = null;
                var toggle = ReactiveCommand.Create(() =>
                {
                    if (row is null) return;
                    row.IsExcluded = !row.IsExcluded;
                    BackupExclusions.SetExcluded(row.Location, row.Folder, row.IsExcluded);
                    row.RaiseBackupChanged();
                });
                var open = ReactiveCommand.Create(() =>
                {
                    try
                    {
                        var relative = folder.Length == 0 ? RelativePath.Empty : RelativePath.FromUnsanitizedInput(folder);
                        var absolute = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(location, relative));
                        _osInterop.OpenDirectory(absolute);
                    }
                    catch (Exception e)
                    {
                        _logger.LogWarning(e, "Couldn't open {Folder}", folder);
                    }
                });

                var makeMod = ReactiveCommand.CreateFromTask(() => MakeModFromFolderAsync(location, folder, files));

                row = new UnmanagedFolderRow
                {
                    MakeModCommand = makeMod,
                    Folder = folder,
                    Location = location,
                    FileCount = files,
                    Bytes = bytes,
                    IsExcluded = BackupExclusions.IsFolderExcluded(location, folder),
                    ToggleBackupCommand = toggle,
                    OpenFolderCommand = open,
                };
                result.Add(row);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't list the game-folder files Afterburn didn't install");
        }
        return result;
    }

    /// <summary>
    /// Groups files by their folder, two levels deep (three under archive/ and r6/, where the interesting split is).
    /// </summary>
    private static string FolderOf(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return string.Empty;
        var depth = parts[0].Equals("archive", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("r6", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
        depth = Math.Min(depth, parts.Length - 1);
        return string.Join('/', parts.Take(depth));
    }

    /// <summary>
    /// Afterburn: puts everything useful for working out a game crash into one zip in Downloads.
    /// </summary>
    private async Task CollectGameLogsAsync()
    {
        GameLogsStatus = "Collecting logs…";
        try
        {
            var loadout = Loadout.Load(_connection.Db, LoadoutId);
            var gameRoot = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "")).GetFullPath();
            var modList = BuildModList(loadout);

            var zipPath = await Task.Run(() =>
            {
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                var target = Path.Combine(downloads, $"Afterburn-game-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

                using var zip = System.IO.Compression.ZipFile.Open(target, System.IO.Compression.ZipArchiveMode.Create);
                var added = 0;

                void AddFolder(string folder, string zipFolder, string pattern, SearchOption option)
                {
                    try
                    {
                        if (!Directory.Exists(folder)) return;
                        var enumeration = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = option == SearchOption.AllDirectories };
                        foreach (var file in Directory.EnumerateFiles(folder, pattern, enumeration))
                        {
                            try
                            {
                                var info = new FileInfo(file);
                                if (info.Length > 20L * 1024 * 1024) continue; // skip crash dumps and other huge files
                                var name = Path.Combine(zipFolder, Path.GetRelativePath(folder, file)).Replace('\\', '/');
                                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                using var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal).Open();
                                source.CopyTo(entry);
                                added++;
                            }
                            catch { /* file in use or gone; skip it */ }
                        }
                    }
                    catch { /* folder not readable */ }
                }

                // Mod framework logs inside the game folder.
                AddFolder(Path.Combine(gameRoot, "bin", "x64", "plugins", "cyber_engine_tweaks"), "cyber_engine_tweaks", "*.log", SearchOption.AllDirectories);
                AddFolder(Path.Combine(gameRoot, "red4ext", "logs"), "red4ext", "*.log", SearchOption.TopDirectoryOnly);
                AddFolder(Path.Combine(gameRoot, "red4ext", "plugins"), "red4ext-plugins", "*.log", SearchOption.AllDirectories);
                AddFolder(Path.Combine(gameRoot, "r6", "logs"), "r6-logs", "*.log", SearchOption.TopDirectoryOnly);
                AddFolder(Path.Combine(gameRoot, "r6", "cache", "modded"), "redmod", "*.log", SearchOption.TopDirectoryOnly);
                AddFolder(Path.Combine(gameRoot, "tools", "redmod", "bin"), "redmod", "*.log", SearchOption.TopDirectoryOnly);

                // The game's own crash reports (text parts only).
                var reportQueue = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "REDEngine", "ReportQueue");
                try
                {
                    if (Directory.Exists(reportQueue))
                    {
                        foreach (var dir in new DirectoryInfo(reportQueue).EnumerateDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Take(3))
                        {
                            AddFolder(dir.FullName, Path.Combine("crash-reports", dir.Name), "*.txt", SearchOption.TopDirectoryOnly);
                            AddFolder(dir.FullName, Path.Combine("crash-reports", dir.Name), "*.log", SearchOption.TopDirectoryOnly);
                            AddFolder(dir.FullName, Path.Combine("crash-reports", dir.Name), "*.json", SearchOption.TopDirectoryOnly);
                        }
                    }
                }
                catch { /* optional */ }

                // Afterburn's own current log, with the Windows user name taken out.
                try
                {
                    var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Afterburn", "Logs", "nexusmods.app.main.current.log");
                    if (File.Exists(logPath))
                    {
                        using var reader = new StreamReader(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                        using var writer = new StreamWriter(zip.CreateEntry("afterburn/nexusmods.app.main.current.log").Open());
                        writer.Write(RedactUser(reader.ReadToEnd()));
                        added++;
                    }
                }
                catch { /* optional */ }

                void AddText(string name, string text)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(text);
                }

                AddText("mod-list.txt", modList);
                AddText("recently-changed-game-files.txt", RedactUser(ListRecentlyChangedFiles(gameRoot)));
                AddText("README.txt", RedactUser($"Collected by Afterburn {ApplicationConstants.Version} on {DateTime.Now:yyyy-MM-dd HH:mm}.\nGame folder: {gameRoot}\nLog files included: {added}\n"));
                return target;
            });

            GameLogsStatus = $"Saved to {zipPath}";
            try { _osInterop.OpenFileInDirectory(FileSystem.Shared.FromUnsanitizedFullPath(zipPath)); } catch { /* optional */ }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't collect the game logs");
            GameLogsStatus = "Couldn't collect the logs: " + e.Message;
        }
    }

    private static string RedactUser(string text)
    {
        try
        {
            var user = Environment.UserName;
            if (string.IsNullOrWhiteSpace(user) || user.Length < 2) return text;
            return text
                .Replace($"\\Users\\{user}", "\\Users\\<user>", StringComparison.OrdinalIgnoreCase)
                .Replace($"/Users/{user}", "/Users/<user>", StringComparison.OrdinalIgnoreCase)
                .Replace($"/home/{user}", "/home/<user>", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return text;
        }
    }

    private string BuildModList(Loadout.ReadOnly loadout)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var db = _connection.Db;
            var mods = new List<(string Name, bool Enabled)>();
            foreach (var item in LoadoutItem.FindByLoadout(db, LoadoutId))
            {
                var group = LoadoutItemGroup.Load(db, item.Id);
                if (!group.IsValid() || !group.TryGetAsLibraryLinkedLoadoutItem(out _)) continue;
                mods.Add((item.Name, item.IsEnabled()));
            }

            sb.AppendLine($"{mods.Count} mods in loadout \"{loadout.Name}\" ({mods.Count(m => m.Enabled)} enabled)");
            sb.AppendLine();
            foreach (var (name, enabled) in mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"[{(enabled ? "on " : "off")}] {name}");
        }
        catch (Exception e)
        {
            sb.AppendLine("Couldn't list mods: " + e.Message);
        }
        return sb.ToString();
    }

    private static string ListRecentlyChangedFiles(string gameRoot)
    {
        var sb = new System.Text.StringBuilder();
        var since = DateTime.UtcNow.AddDays(-3);
        sb.AppendLine("Game-folder files changed in the last 3 days (newest first):");
        sb.AppendLine();
        try
        {
            var skip = new[] { Path.Combine("archive", "pc", "content"), Path.Combine("archive", "pc", "ep1") };
            var files = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(gameRoot, "*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true }))
            {
                var relative = Path.GetRelativePath(gameRoot, path);
                if (skip.Any(s => relative.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc >= since) files.Add(info);
                if (files.Count > 5000) break;
            }

            foreach (var info in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(1000))
                sb.AppendLine($"{info.LastWriteTime:yyyy-MM-dd HH:mm}  {FormatBytes(info.Length),9}  {Path.GetRelativePath(gameRoot, info.FullName)}");
        }
        catch (Exception e)
        {
            sb.AppendLine("Couldn't list files: " + e.Message);
        }
        return sb.ToString();
    }

    private void LoadRestorePoints()
    {
        _restorePoints.Clear();
        try
        {
            var db = _connection.Db;
            var current = Loadout.Load(db, LoadoutId);
            if (!current.IsValid()) return;
            var metadata = GameInstallMetadata.Load(db, current.InstallationId);
            var activeId = metadata.Contains(GameInstallMetadata.LastSyncedLoadout) ? metadata.LastSyncedLoadout.LoadoutId : current.LoadoutId;
            var points = Loadout.All(db)
                .Where(l => l.IsVisible() && l.InstallationId == current.InstallationId && l.Name.StartsWith(RestorePointPrefix, StringComparison.Ordinal))
                .OrderByDescending(l => l.Name, StringComparer.Ordinal)
                .ToArray();

            foreach (var point in points)
            {
                var id = point.LoadoutId;
                var isCurrent = id == activeId;
                _restorePoints.Add(new RestorePointRow
                {
                    Name = point.Name,
                    Details = isCurrent ? "In use now" : "Not in use",
                    RestoreCommand = ReactiveCommand.CreateFromTask(async () =>
                    {
                        RestorePointStatus = $"Switching to {point.Name}…";
                        try
                        {
                            await _loadoutManager.ActivateLoadout(id);
                            RestorePointStatus = $"Switched to {point.Name}. Afterburn is now using that mod setup; you can switch back from My Loadouts.";
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, "Couldn't switch to restore point {Name}", point.Name);
                            RestorePointStatus = "Couldn't switch to the restore point: " + e.Message;
                        }
                    }),
                    DeleteCommand = ReactiveCommand.CreateFromTask(async () =>
                    {
                        var meta = GameInstallMetadata.Load(_connection.Db, current.InstallationId);
                        var activeNow = meta.Contains(GameInstallMetadata.LastSyncedLoadout) ? meta.LastSyncedLoadout.LoadoutId : current.LoadoutId;
                        if (id == activeNow || id == LoadoutId) { RestorePointStatus = "That restore point is in use, so it can't be deleted. Switch to another loadout first."; return; }
                        await _loadoutManager.DeleteLoadout(id);
                        LoadRestorePoints();
                        RestorePointStatus = $"Deleted {point.Name}.";
                    }),
                });
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't list restore points");
        }
    }

    private async Task SaveRestorePointAsync()
    {
        RestorePointStatus = "Saving a restore point…";
        try
        {
            var copy = await _loadoutManager.CopyLoadout(LoadoutId);
            var name = $"{RestorePointPrefix} {DateTime.Now:yyyy-MM-dd HH:mm}";
            using (var tx = _connection.BeginTransaction())
            {
                tx.Add(copy.Id, Loadout.Name, name);
                await tx.Commit();
            }
            LoadRestorePoints();
            RestorePointStatus = $"Saved \"{name}\". Your current setup is unchanged.";
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't save a restore point");
            RestorePointStatus = "Couldn't save a restore point: " + e.Message;
        }
    }

    /// <summary>
    /// Afterburn: turns the External Changes files in one folder into a normal mod. The files are packed into a zip,
    /// added to the Library and installed, then removed from External Changes. Nothing on disk moves, and afterwards
    /// the mod can be switched off, updated and sorted like any other.
    /// </summary>
    private async Task MakeModFromFolderAsync(LocationId location, string folder, int fileCount)
    {
        var display = folder.Length == 0 ? "game folder" : folder;

        // Everything in the folder becomes ONE mod, which isn't always what people expect (archive/pc/mod usually holds
        // many separate mods), so say so first.
        var confirm = await WindowManager.ShowDialog(DialogFactory.CreateStandardDialog(
            $"Turn {display} into one mod?",
            new StandardDialogParameters
            {
                Text = $"All {fileCount:N0} files in {display} that Afterburn didn't install will become a single mod in your Library and My Mods. " +
                       "Your files stay where they are. Switching that mod off later removes all of them from the game folder at once (they stay in the Library, so you can switch it back on).",
            },
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Turn into a mod", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
            ],
            DialogWindowSize.Medium
        ), DialogWindowType.Modal);
        if (confirm.ButtonId != ButtonDefinitionId.Accept) return;

        MakeModStatus = $"Turning {display} into a mod…";
        if (location != LocationId.Game)
        {
            MakeModStatus = $"Only files inside the game folder can be turned into a mod ({display} is in {location}).";
            return;
        }
        string? zipPath = null;
        try
        {
            var db = _connection.Db;
            if (!LoadoutOverridesGroup.FindByOverridesFor(db, LoadoutId).TryGetFirst(out var overrides))
            {
                MakeModStatus = "There are no External Changes to turn into a mod.";
                return;
            }

            var loadout = Loadout.Load(db, LoadoutId);
            var items = overrides.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath()
                .Where(item => item.TargetPath.Item2 == location && FolderOf(item.TargetPath.Item3.ToString().Replace('\\', '/')) == folder)
                .ToArray();
            if (items.Length == 0)
            {
                MakeModStatus = $"Nothing left in {display}.";
                return;
            }

            var safeName = string.Concat((folder.Length == 0 ? "Game folder files" : folder.Replace('/', ' ')).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var tempDir = Path.Combine(Path.GetTempPath(), "Afterburn", "make-mod");
            Directory.CreateDirectory(tempDir);
            zipPath = Path.Combine(tempDir, $"{safeName} (from game folder).zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            var packed = await Task.Run(() =>
            {
                var count = 0;
                using var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
                foreach (var item in items)
                {
                    var relative = item.TargetPath.Item3.ToString().Replace('\\', '/');
                    var absolute = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(location, item.TargetPath.Item3)).GetFullPath();
                    if (!File.Exists(absolute)) continue;
                    using var source = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var entry = zip.CreateEntry(relative, System.IO.Compression.CompressionLevel.Fastest).Open();
                    source.CopyTo(entry);
                    count++;
                }
                return count;
            });
            if (packed == 0)
            {
                MakeModStatus = $"The files in {display} weren't found on disk any more. Click Apply first.";
                return;
            }

            var localFile = await _libraryService.AddLocalFile(FileSystem.Shared.FromUnsanitizedFullPath(zipPath));
            var libraryItem = localFile.AsLibraryFile().AsLibraryItem();
            var result = await _loadoutManager.InstallItem(libraryItem, LoadoutId);
            if (result.LoadoutItemGroup is not { } installed)
            {
                MakeModStatus = $"Afterburn couldn't install {display} as a mod. The files are still in External Changes and nothing was changed.";
                return;
            }

            // Safety check: the new mod must put every file exactly where it already is. If an installer rearranged
            // them, taking them out of External Changes would make the next Apply move or delete the originals.
            var expected = items.Select(i => (i.TargetPath.Item2, i.TargetPath.Item3.ToString().Replace('\\', '/').ToLowerInvariant())).ToHashSet();
            var installedGroup = LoadoutItemGroup.Load(_connection.Db, installed.Id);
            var actual = installedGroup.Children.OfTypeLoadoutItemWithTargetPath()
                .Select(i => (i.TargetPath.Item2, i.TargetPath.Item3.ToString().Replace('\\', '/').ToLowerInvariant()))
                .ToHashSet();
            if (!expected.SetEquals(actual))
            {
                await _loadoutManager.RemoveItems([installed.LoadoutItemGroupId]);
                MakeModStatus = $"Afterburn's installer would have put some of the files in {display} somewhere else, so it stopped. Nothing was changed; the files are still in External Changes.";
                return;
            }

            // The new mod now provides these files, so take them out of External Changes.
            using (var tx = _connection.BeginTransaction())
            {
                foreach (var item in items) tx.Delete(item.Id, recursive: false);
                await tx.Commit();
            }
            await _loadoutManager.AutoResolveFileConflicts(LoadoutId);

            MakeModStatus = $"Done: \"{safeName} (from game folder)\" is now a mod with {packed:N0} files. You'll find it in My Mods and the Library.";
            await RefreshAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't turn {Folder} into a mod", folder);
            MakeModStatus = $"Couldn't turn {display} into a mod: {e.Message}. Nothing was removed from External Changes.";
        }
        finally
        {
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { /* temp file */ }
        }
    }

    private async Task CleanupAsync()
    {
        var before = await Task.Run(() => MeasureArchives().Bytes);
        CleanupStatus = "Cleaning up…";
        try
        {
            await _garbageCollector.RunAsync();
            var after = await Task.Run(() => MeasureArchives().Bytes);
            var freed = Math.Max(0, before - after);
            CleanupStatus = freed > 0
                ? $"Done. Freed {FormatBytes(freed)}."
                : "Done. Nothing was unused, so nothing was removed.";
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Storage clean-up failed");
            CleanupStatus = "Clean up didn't finish. Try again after the current Apply or install has finished.";
        }
        await RefreshAsync();
    }
}
