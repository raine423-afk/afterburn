using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Exceptions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Controls.Navigation;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Pages.Diff.ApplyDiff;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Observable = System.Reactive.Linq.Observable;
using ReactiveCommand = ReactiveUI.ReactiveCommand;
using Unit = System.Reactive.Unit;

namespace NexusMods.App.UI.LeftMenu.Items;

public class ApplyControlViewModel : AViewModel<IApplyControlViewModel>, IApplyControlViewModel
{
    private readonly IConnection _conn;
    private readonly ISynchronizerService _syncService;
    private readonly IJobMonitor _jobMonitor;
    private readonly IWindowNotificationService _notificationService;
    private readonly IGameRegistry _gameRegistry;

    private readonly LoadoutId _loadoutId;
    private readonly IServiceProvider _serviceProvider;
    private readonly GameInstallMetadataId _gameMetadataId;
    [Reactive] private bool CanApply { get; set; } = true;
    [Reactive] public bool IsApplying { get; private set; }

    public ReactiveUI.ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveUI.ReactiveCommand<NavigationInformation, Unit> ShowApplyDiffCommand { get; }

    [Reactive] public bool IsProcessing { get; private set; }
    [Reactive] public string ApplyButtonText { get; private set; } = Language.ApplyControlViewModel__APPLY;

    [Reactive] public string ProcessingText { get; private set; } = "";
    [Reactive] public bool IsLaunchButtonEnabled { get; private set; } = true;

    public ILaunchButtonViewModel LaunchButtonViewModel { get; }

    private static readonly string[] IgnoredWatchFolders =
    [
        "r6/cache", "r6/logs", "red4ext/logs", "bin/x64/plugins/cyber_engine_tweaks/scripting.log",
        "archive/pc/content", "archive/pc/ep1", "tools/redmod/bin", "engine/config/platform",
        // Written by Afterburn's own conflict check.
        "archive/pc/mod/modlist.txt", "archive/pc/mod/afterburn-modlist.owner",
    ];

    // Afterburn: when Afterburn or the game last touched the game folder (Environment.TickCount64), so the watcher
    // doesn't report Afterburn's own Apply or files the game and its mods write while playing.
    private long _lastBusyTicks = Environment.TickCount64;
    private volatile bool _gameRunning;
    private static readonly TimeSpan QuietAfterBusy = TimeSpan.FromSeconds(20);

    private void MarkBusy() => Interlocked.Exchange(ref _lastBusyTicks, Environment.TickCount64);

    private bool IsBusyOrJustFinished() =>
        IsApplying || IsProcessing || _gameRunning
        || Environment.TickCount64 - Interlocked.Read(ref _lastBusyTicks) < (long)QuietAfterBusy.TotalMilliseconds;

    private IDisposable StartGameFolderWatcher()
    {
        try
        {
            var loadout = Sdk.Loadouts.Loadout.Load(_conn.Db, _loadoutId);
            var root = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "")).GetFullPath();
            if (!Directory.Exists(root)) return System.Reactive.Disposables.Disposable.Empty;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024,
            };

            var gate = new object();
            System.Threading.Timer? pending = null;
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void OnChange(string fullPath)
            {
                if (IsBusyOrJustFinished()) return;
                var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                if (relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) || relative.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return;
                foreach (var ignored in IgnoredWatchFolders)
                    if (relative.StartsWith(ignored, StringComparison.OrdinalIgnoreCase)) return;

                lock (gate)
                {
                    changed.Add(relative);
                    pending?.Dispose();
                    // Wait until the copying has stopped for a few seconds, then say it once.
                    pending = new System.Threading.Timer(_ =>
                    {
                        int count;
                        lock (gate) { count = changed.Count; changed.Clear(); }
                        if (count == 0 || IsBusyOrJustFinished()) return;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            _notificationService.ShowToast(
                                $"{count:N0} file(s) changed in the game folder. Click Apply to add them to Afterburn (they'll show up under External Changes).",
                                ToastNotificationVariant.Neutral,
                                TimeSpan.FromSeconds(10)));
                    }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                }
            }

            // New and renamed files only: mods rewrite their own settings/data files all the time, which isn't news.
            watcher.Created += (_, e) => { if (!Directory.Exists(e.FullPath)) OnChange(e.FullPath); };
            watcher.Renamed += (_, e) => { if (!Directory.Exists(e.FullPath)) OnChange(e.FullPath); };
            watcher.Error += (_, _) => { lock (gate) changed.Clear(); };
            watcher.EnableRaisingEvents = true;

            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                lock (gate) pending?.Dispose();
            });
        }
        catch
        {
            return System.Reactive.Disposables.Disposable.Empty;
        }
    }

    public ApplyControlViewModel(LoadoutId loadoutId, IServiceProvider serviceProvider, IJobMonitor jobMonitor, IOverlayController overlayController, GameRunningTracker gameRunningTracker)
    {
        _loadoutId = loadoutId;
        _serviceProvider = serviceProvider;
        _syncService = serviceProvider.GetRequiredService<ISynchronizerService>();
        _conn = serviceProvider.GetRequiredService<IConnection>();
        _jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        _notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();
        var windowManager = serviceProvider.GetRequiredService<IWindowManager>();
        _gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();

        _gameMetadataId = Sdk.Loadouts.Loadout.Load(_conn.Db, loadoutId).InstallationId;

        LaunchButtonViewModel = serviceProvider.GetRequiredService<ILaunchButtonViewModel>();
        LaunchButtonViewModel.LoadoutId = loadoutId;
        
        ApplyCommand = ReactiveCommand.CreateFromTask(async () => await Apply(), 
            canExecute: this.WhenAnyValue(vm => vm.CanApply));
        
        ShowApplyDiffCommand = ReactiveCommand.Create<NavigationInformation>(info =>
        {
            var pageData = new PageData
            {
                FactoryId = ApplyDiffPageFactory.StaticId,
                Context = new ApplyDiffPageContext
                {
                    LoadoutId = _loadoutId,
                },
            };

            var workspaceController = windowManager.ActiveWorkspaceController;

            var behavior = workspaceController.GetOpenPageBehavior(pageData, info);
            var workspaceId = workspaceController.ActiveWorkspaceId;
            workspaceController.OpenPage(workspaceId, pageData, behavior);
        });

        this.WhenActivated(disposables =>
            {
                // Afterburn: notice mods dropped into the game folder by hand (or written by WolvenKit) straight away.
                StartGameFolderWatcher().DisposeWith(disposables);

                var isProcessingObservable = _jobMonitor.HasActiveJob<ProcessLoadoutChangesJob>(job => job.LoadoutId.Equals(loadoutId))
                    .Prepend(false);
                
                var loadoutStatuses = Observable.Prepend(Observable.FromAsync(() => _syncService.StatusForLoadout(_loadoutId))
                        .Switch(), LoadoutSynchronizerState.Pending);

                var gameStatuses = _syncService.StatusForGame(_gameMetadataId)
                    .Prepend(GameSynchronizerState.Idle);

                var hasUnmanagingJob = _jobMonitor.HasActiveJob<UnmanageGameJob>(job =>
                {
                    if (!_gameRegistry.TryGetMetadata(job.Installation, out var metadata)) return false;
                    return metadata.GameInstallMetadataId == _gameMetadataId;
                }).Prepend(false);

                // Note(sewer):
                // Fire an initial value with StartWith because CombineLatest requires all stuff to have latest values.
                // In any case, we should prevent Apply from being available while a file is in use.
                // A file may be in use because:
                // - The user launched the game externally (e.g. through Steam).
                //     - Approximate this by seeing if any EXE in any of the game folders are running.
                //     - This is done in 'Synchronize' method.
                // - They're running a tool from within the App.
                //     - Check running jobs.
                loadoutStatuses.CombineLatest(isProcessingObservable, gameStatuses, gameRunningTracker.GetWithCurrentStateAsStarting(), hasUnmanagingJob)
                    .OnUI()
                    .Subscribe(status =>
                    {
                        var (ldStatus, isProcessing,  gameStatus, running, isUnmanaging) = status;
                        IsProcessing = isProcessing;
                        _gameRunning = running;
                        if (isProcessing || running || gameStatus == GameSynchronizerState.Busy) MarkBusy();
                        CanApply = !isProcessing
                                   && !running
                                   && gameStatus != GameSynchronizerState.Busy
                                   && ldStatus != LoadoutSynchronizerState.Pending
                                   && ldStatus != LoadoutSynchronizerState.Current
                                   && !isUnmanaging;
                        IsLaunchButtonEnabled = !isProcessing 
                                                && !running
                                                && gameStatus != GameSynchronizerState.Busy
                                                && ldStatus == LoadoutSynchronizerState.Current
                                                && !isUnmanaging;
                        
                    })
                    .DisposeWith(disposables);

                _jobMonitor.HasActiveJob<SynchronizeLoadoutJob>(job => job.LoadoutId == loadoutId)
                    .Prepend(_jobMonitor.Jobs.Any(job => job.Definition is SynchronizeLoadoutJob sJob && sJob.LoadoutId == loadoutId))
                    .OnUI()
                    .Subscribe(isApplying => { IsApplying = isApplying; MarkBusy(); })
                    .DisposeWith(disposables);
                
                _jobMonitor.ObserveActiveJobs<SynchronizeLoadoutJob>()
                    .Prepend(ChangeSet<IJob, JobId>.Empty)
                    .QueryWhenChanged(jobs =>
                        {
                            if (jobs.Items.FirstOrDefault()?.Definition is SynchronizeLoadoutJob sJob && sJob.LoadoutId == loadoutId)
                                return sJob.StatusMessage.AsSystemObservable();
                            return new BindableReactiveProperty<string>(value: "").AsSystemObservable();
                        }
                    ).Switch()
                    .OnUI()
                    .Subscribe(status => ProcessingText = status)
                    .DisposeWith(disposables);
            }
        );
    }

    private async Task Apply()
    {
        var loadout = Sdk.Loadouts.Loadout.Load(_conn.Db, _loadoutId);
        try
        {
            await Task.Run(async () =>
            {
                await _syncService.Synchronize(_loadoutId);
            });
            
            _notificationService.ShowToast(Language.ToastNotification_Applied__0__successfully, ToastNotificationVariant.Success);
        }
        catch (ExecutableInUseException)
        {
            await MessageBoxOkViewModel.ShowGameAlreadyRunningError(_serviceProvider, loadout.Installation.Name);
        }
    }
}
