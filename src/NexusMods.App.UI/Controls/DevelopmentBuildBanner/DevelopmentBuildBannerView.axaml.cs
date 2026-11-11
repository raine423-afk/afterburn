using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using NexusMods.Sdk;
using NexusMods.UI.Sdk.Icons;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.DevelopmentBuildBanner;

[UsedImplicitly]
public partial class DevelopmentBuildBannerView : ReactiveUserControl<IDevelopmentBuildBannerViewModel>
{
    private static readonly StyledProperty<string> AppNameProperty =
        AvaloniaProperty.Register<DevelopmentBuildBannerView, string>(nameof(AppName), "DEVELOPMENT USE ONLY");
    
    private static readonly StyledProperty<string> AppVersionProperty =
        AvaloniaProperty.Register<DevelopmentBuildBannerView, string>(nameof(AppVersion), "vX.X.X");

    public string AppName
    {
        get => GetValue(AppNameProperty);
        set => SetValue(AppNameProperty, value);
    }
    
    public string AppVersion
    {
        get => GetValue(AppVersionProperty);
        set => SetValue(AppVersionProperty, value);
    }

    public DevelopmentBuildBannerView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
            {
                this.BindCommand(ViewModel, vm => vm.ReportBugCommand, view => view.ReportBugButton)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.ToggleDownloadsPausedCommand, view => view.PauseDownloadsButton)
                    .DisposeWith(d);

                this.WhenAnyValue(view => view.ViewModel!.DownloadsPaused)
                    .Subscribe(paused =>
                    {
                        PauseDownloadsButton.Text = paused ? "Resume downloads" : "Pause downloads";
                        PauseDownloadsButton.LeftIcon = paused ? IconValues.PlayArrow : IconValues.Pause;
                    })
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.RateLimitText, view => view.RateLimitTextBlock.Text)
                    .DisposeWith(d);

                this.WhenAnyValue(view => view.ViewModel!.RateLimitDetails)
                    .Subscribe(details => ToolTip.SetTip(RateLimitTextBlock, details))
                    .DisposeWith(d);

                this.WhenAnyValue(view => view.ViewModel!.IsRateLimitLow)
                    .Subscribe(low => RateLimitTextBlock.Foreground = low
                        ? Avalonia.Media.Brushes.OrangeRed
                        : (Avalonia.Media.IBrush?)this.FindResource("NeutralSubduedBrush"))
                    .DisposeWith(d);
            }
        );
        
        var appName = ApplicationConstants.IsDebug ? "DEVELOPMENT USE ONLY" : AfterburnBrand.Name;

        AppVersion = GetAppVersion();
        AppName = $"{appName}:";
    }

    private static string GetAppVersion()
    {
        return ApplicationConstants.IsDebug ? $"Debug build - {ApplicationConstants.CommitHash}" : ApplicationConstants.Version.ToString();
    }
}
