using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.Conflicts;

/// <summary>
/// Afterburn: the Conflicts page. Shows, for debugging, every file that enabled mods provide with different content:
/// every copy in conflict order with rank, size, hash, download and collection; which mod pairs clash and who wins;
/// each mod's totals; and which folders and file types the conflicts are in. Can copy or save a report and a CSV.
/// </summary>
[UsedImplicitly]
public class ConflictsPageViewModel : APageViewModel<IConflictsPageViewModel>, IConflictsPageViewModel
{
    private readonly IConnection _connection;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IAvaloniaInterop _avaloniaInterop;
    private readonly IOSInterop _osInterop;
    private readonly ILogger<ConflictsPageViewModel> _logger;

    private ConflictsSnapshot _snapshot = ConflictsSnapshot.Empty;

    public ConflictsPageViewModel(
        IWindowManager windowManager,
        IConnection connection,
        ILoadoutManager loadoutManager,
        IAvaloniaInterop avaloniaInterop,
        IOSInterop osInterop,
        ILogger<ConflictsPageViewModel> logger) : base(windowManager)
    {
        _connection = connection;
        _loadoutManager = loadoutManager;
        _avaloniaInterop = avaloniaInterop;
        _osInterop = osInterop;
        _logger = logger;

        TabIcon = IconValues.Swap;
        TabTitle = "Conflicts";

        var notBusy = this.WhenAnyValue(vm => vm.IsBusy).Select(busy => !busy);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync, notBusy);
        AutoResolveCommand = ReactiveCommand.CreateFromTask(AutoResolveAsync, notBusy);
        CopyReportCommand = ReactiveCommand.CreateFromTask(CopyReportAsync, notBusy);
        SaveReportCommand = ReactiveCommand.CreateFromTask(() => SaveAsync("txt", BuildReport), notBusy);
        SaveCsvCommand = ReactiveCommand.CreateFromTask(() => SaveAsync("csv", () => ConflictsCollector.BuildCsv(_snapshot)), notBusy);

        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(vm => vm.SearchText, vm => vm.OnlyHiddenCollectionFiles)
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(250))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ApplyFilter())
                .DisposeWith(disposables);

            RefreshCommand.Execute().Subscribe(_ => { }, e => _logger.LogError(e, "Couldn't refresh the Conflicts page")).DisposeWith(disposables);
        });
    }

    [Reactive] public LoadoutId LoadoutId { get; set; }
    [Reactive] public string Summary { get; private set; } = "Looking for conflicts…";
    [Reactive] public string Status { get; private set; } = string.Empty;
    [Reactive] public bool IsBusy { get; private set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool OnlyHiddenCollectionFiles { get; set; }

    [Reactive] public string FilesHeader { get; private set; } = "Files";
    [Reactive] public string PairsHeader { get; private set; } = "Mod pairs";
    [Reactive] public string ModsHeader { get; private set; } = "Mods";
    [Reactive] public string GroupsHeader { get; private set; } = "Folders & file types";

    [Reactive] public IReadOnlyList<ConflictFile> VisibleFiles { get; private set; } = [];
    [Reactive] public IReadOnlyList<ModPairRow> VisiblePairs { get; private set; } = [];
    [Reactive] public IReadOnlyList<ConflictModRow> VisibleMods { get; private set; } = [];
    [Reactive] public IReadOnlyList<ConflictGroupRow> VisibleGroups { get; private set; } = [];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> AutoResolveCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyReportCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveReportCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCsvCommand { get; }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Looking for conflicts…";
        try
        {
            var loadoutId = LoadoutId;
            var started = DateTime.Now;
            _snapshot = await Task.Run(() => ConflictsCollector.Gather(_connection, loadoutId));

            var files = _snapshot.Files;
            var hiddenCopies = files.Sum(f => f.Providers.Count - 1);
            var bytesHidden = files.Sum(f => f.Providers.Where(p => !p.IsUsed).Sum(p => (double)(p.Size ?? 0)));
            Summary = $"{files.Count:N0} conflicting file{(files.Count == 1 ? "" : "s")} · {hiddenCopies:N0} hidden cop{(hiddenCopies == 1 ? "y" : "ies")} ({ConflictsCollector.FormatSize((ulong)bytesHidden)}) · " +
                      $"{_snapshot.Mods.Count:N0} mods involved · {_snapshot.Pairs.Count:N0} mod pairs · {_snapshot.PriorityGroups:N0} mods in the conflict order";
            Status = $"Checked {DateTime.Now:t} in {(DateTime.Now - started).TotalSeconds:N1} s.";
            ApplyFilter();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't gather conflicts");
            Status = "Something went wrong while looking for conflicts. Press Refresh to try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var search = SearchText?.Trim() ?? string.Empty;
        var onlyCollections = OnlyHiddenCollectionFiles;

        IEnumerable<ConflictFile> files = _snapshot.Files;
        if (onlyCollections) files = files.Where(f => f.Providers.Any(p => !p.IsUsed && p.Collection.Length > 0));
        if (search.Length > 0) files = files.Where(f => f.Matches(search));
        VisibleFiles = files.ToArray();

        VisiblePairs = search.Length == 0 ? _snapshot.Pairs : _snapshot.Pairs.Where(p => p.Matches(search)).ToArray();
        VisibleMods = search.Length == 0 ? _snapshot.Mods : _snapshot.Mods.Where(m => m.Matches(search)).ToArray();
        VisibleGroups = search.Length == 0 ? _snapshot.Groups : _snapshot.Groups.Where(g => g.Matches(search)).ToArray();

        FilesHeader = Header("Files", VisibleFiles.Count, _snapshot.Files.Count);
        PairsHeader = Header("Mod pairs", VisiblePairs.Count, _snapshot.Pairs.Count);
        ModsHeader = Header("Mods", VisibleMods.Count, _snapshot.Mods.Count);
        GroupsHeader = Header("Folders & file types", VisibleGroups.Count, _snapshot.Groups.Count);
    }

    private static string Header(string name, int visible, int total) =>
        visible == total ? $"{name} ({total:N0})" : $"{name} ({visible:N0} of {total:N0})";

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

    private string BuildReport()
    {
        var header = new List<string> { $"Afterburn {ApplicationConstants.Version} - conflicts report - {DateTime.Now:yyyy-MM-dd HH:mm}" };
        try
        {
            header.Add($"Loadout: {Loadout.Load(_connection.Db, LoadoutId).Name}");
        }
        catch { /* optional */ }
        header.Add(Summary);
        return ConflictsCollector.BuildReport(header, _snapshot);
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
            _logger.LogError(e, "Couldn't copy the conflicts report");
            Status = "Couldn't copy the report. Try Save report instead.";
        }
    }

    private async Task SaveAsync(string extension, Func<string> build)
    {
        try
        {
            var path = await Task.Run(() =>
            {
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                var target = Path.Combine(downloads, $"Afterburn-conflicts-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");
                File.WriteAllText(target, build(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return target;
            });

            Status = $"Saved to {path}";
            try { _osInterop.OpenFileInDirectory(FileSystem.Shared.FromUnsanitizedFullPath(path)); }
            catch { /* optional */ }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't save the conflicts {Extension}", extension);
            Status = "Couldn't save the file.";
        }
    }
}
