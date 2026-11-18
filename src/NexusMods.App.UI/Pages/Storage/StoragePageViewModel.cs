using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GC;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
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

    public void RaiseBackupChanged()
    {
        this.RaisePropertyChanged(nameof(BackupButtonText));
        this.RaisePropertyChanged(nameof(BackupStateText));
    }
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

    public StoragePageViewModel(
        IWindowManager windowManager,
        IConnection connection,
        IFileStore fileStore,
        IGarbageCollectorRunner garbageCollector,
        IOSInterop osInterop,
        ILogger<StoragePageViewModel> logger) : base(windowManager)
    {
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

        this.WhenActivated(disposables =>
        {
            RefreshCommand.Execute().Subscribe().DisposeWith(disposables);
        });
    }

    [Reactive] public LoadoutId LoadoutId { get; set; }
    [Reactive] public string StorageSummary { get; private set; } = "Working out how much space Afterburn uses…";
    [Reactive] public string FreeSpaceText { get; private set; } = string.Empty;
    [Reactive] public string CleanupStatus { get; private set; } = "Clean up removes backed-up and downloaded files that no mod or loadout uses any more.";
    [Reactive] public string UnmanagedSummary { get; private set; } = "Looking for files in the game folder that Afterburn didn't install…";

    public ReadOnlyObservableCollection<UnmanagedFolderRow> UnmanagedFolders { get; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CleanupCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenArchivesFolderCommand { get; }

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
        var (archiveBytes, archiveCount, freeText) = await Task.Run(() =>
        {
            var (b, c) = MeasureArchives();
            return (b, c, MeasureFreeSpace());
        });

        StorageSummary = _fileStore.ArchiveLocations.Count == 0
            ? "Afterburn couldn't find its archive folder."
            : $"Afterburn's backups and downloaded mods take {FormatBytes(archiveBytes)} in {archiveCount:N0} archives, in {_fileStore.ArchiveLocations[0]}.";
        FreeSpaceText = freeText;

        var rows = await Task.Run(BuildUnmanagedRows);
        _folders.Clear();
        foreach (var row in rows) _folders.Add(row);

        var totalFiles = rows.Sum(r => r.FileCount);
        var totalBytes = rows.Sum(r => r.Bytes);
        UnmanagedSummary = totalFiles == 0
            ? "Every file in the game folder was installed by Afterburn (or is part of the game)."
            : $"{totalFiles:N0} files ({FormatBytes(totalBytes)}) in the game folder weren't installed by Afterburn. They show up as External Changes. " +
              "Afterburn keeps a backup copy of them so it can put them back; turn that off for folders you'd rather not duplicate.";
    }

    private List<UnmanagedFolderRow> BuildUnmanagedRows()
    {
        var result = new List<UnmanagedFolderRow>();
        try
        {
            var db = _connection.Db;
            if (!LoadoutOverridesGroup.FindByOverridesFor(db, LoadoutId).TryGetFirst(out var overrides)) return result;

            var loadout = Loadout.Load(db, LoadoutId);
            var groups = new Dictionary<(LocationId, string), (int Files, long Bytes)>();
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

            foreach (var ((location, folder), (files, bytes)) in groups.OrderByDescending(kv => kv.Value.Bytes))
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
                        var absolute = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(location, RelativePath.FromUnsanitizedInput(folder)));
                        _osInterop.OpenDirectory(absolute);
                    }
                    catch (Exception e)
                    {
                        _logger.LogWarning(e, "Couldn't open {Folder}", folder);
                    }
                });

                row = new UnmanagedFolderRow
                {
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
