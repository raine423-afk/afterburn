using System.Reactive;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.DevelopmentBuildBanner;

public class DevelopmentBuildBannerDesignViewModel : AViewModel<IDevelopmentBuildBannerViewModel>, IDevelopmentBuildBannerViewModel
{
    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand { get; } = ReactiveCommand.Create(() => { });
    public string RateLimitText => "Rate Meter: 412/500 per hr · 18,240/20,000 today";
    public string RateLimitDetails => "Design preview";
    public bool IsRateLimitLow => false;

    public DevelopmentBuildBannerDesignViewModel() { }
}
