using System.Reactive;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.DevelopmentBuildBanner;

public interface IDevelopmentBuildBannerViewModel : IViewModelInterface
{
    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand { get; }

    /// <summary>Pauses or resumes all downloads.</summary>
    public ReactiveCommand<Unit, Unit> ToggleDownloadsPausedCommand { get; }

    /// <summary>True while the user has paused all downloads.</summary>
    public bool DownloadsPaused { get; }

    /// <summary>Short Nexus API rate-limit summary shown in the bottom bar.</summary>
    public string RateLimitText { get; }

    /// <summary>Detailed rate-limit and usage info for the tooltip.</summary>
    public string RateLimitDetails { get; }

    /// <summary>True while the API is refusing requests (HTTP 429) or nearly out of requests.</summary>
    public bool IsRateLimitLow { get; }
}
