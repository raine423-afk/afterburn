using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using Avalonia.Platform;
using Avalonia.Threading;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Extensions;
using ReactiveUI;

namespace NexusMods.App.UI.Windows;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    public MainWindow()
    {        
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 44; // 44 pixels drag height to match the top bar
        
        InitializeComponent();

        #if DEBUG
        this.AttachDevTools();
        #endif

        this.WhenActivated(disposables =>
        {
            var storageProvider = GetTopLevel(this)?.StorageProvider;
            var clipboard = GetTopLevel(this)?.Clipboard;
            
            if (storageProvider is not null)
            {
                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .SubscribeWithErrorLogging(vm =>
                    {
                        using var _ = vm.RegisterStorageProvider.Execute(storageProvider).Subscribe();
                    })
                    .DisposeWith(disposables);
            }
            
            if (clipboard is not null)
            {
                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .SubscribeWithErrorLogging(vm =>
                    {
                        using var _ = vm.RegisterClipboard.Execute(clipboard).Subscribe();
                    })
                    .DisposeWith(disposables);
            }

            this.OneWayBind(ViewModel, vm => vm.TopBar, v => v.TopBarViewModelHost.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Spine, v => v.SpineViewModelHost.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.DevelopmentBuildBanner, v => v.DevelopmentBuildBanner.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.WorkspaceController.ActiveWorkspace, view => view.WorkspaceView.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.LeftMenu)
                .BindTo(this, view => view.LeftMenuViewModelHost.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.CurrentOverlay)
                .BindTo(this, view => view.OverlayViewHost.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.CurrentOverlay)
                .Select(content => content != null)
                .BindTo(this, view => view.OverlayBorder.IsVisible)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.IsActive)
                .BindTo(this, view => view.ViewModel!.IsActive)
                .DisposeWith(disposables);

            this.WhenAnyObservable(view => view.ViewModel!.BringWindowToFront)
                .OnUI()
                .Where(static shouldBringToFront => shouldBringToFront)
                .Subscribe(_ => {
                    // Afterburn: opened again while an apply was finishing in the background.
                    if (!IsVisible)
                    {
                        _closeAfterApply = false;
                        Show();
                    }

                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;

                    Activate();
                }).DisposeWith(disposables);
        });
    }

    private bool _closeAfterApply;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Afterburn: closing in the middle of an apply loses track of the files it already wrote, and the next apply
        // then re-checks all of them ("Collecting files" for minutes). Hide the window and let the apply finish first.
        if (ApplyTracker.IsApplying && !_closeAfterApply)
        {
            e.Cancel = true;
            _closeAfterApply = true;
            Hide();
            _ = CloseWhenApplyFinishes();
            return;
        }

        ViewModel?.OnClose();
        base.OnClosing(e);
    }

    private async Task CloseWhenApplyFinishes()
    {
        var giveUpAt = DateTime.UtcNow.AddMinutes(15);
        while (_closeAfterApply && ApplyTracker.IsApplying && DateTime.UtcNow < giveUpAt)
            await Task.Delay(500);

        if (!_closeAfterApply) return; // the window was opened again
        Dispatcher.UIThread.Post(Close);
    }
    
    
}
