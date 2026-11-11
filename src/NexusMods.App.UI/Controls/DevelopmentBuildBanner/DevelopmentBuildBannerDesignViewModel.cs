using System.Reactive;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.DevelopmentBuildBanner;

public class DevelopmentBuildBannerDesignViewModel : AViewModel<IDevelopmentBuildBannerViewModel>, IDevelopmentBuildBannerViewModel
{
    public ReactiveCommand<Unit, Unit> ReportBugCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> ToggleDownloadsPausedCommand { get; } = ReactiveCommand.Create(() => { });
    public bool DownloadsPaused => false;
    public string RateLimitText => "Rate Meter: 18,240/20,000 today";
    public string RateLimitDetails => "Design preview";
    public bool IsRateLimitLow => false;

    public DevelopmentBuildBannerDesignViewModel() { }
}
