using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using NexusMods.Abstractions.Downloads;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Sdk;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.DevelopmentBuildBanner;

public class DevelopmentBuildBannerViewModel : AViewModel<IDevelopmentBuildBannerViewModel>, IDevelopmentBuildBannerViewModel
{
    private static readonly Uri GiveFeedbackUri = AfterburnBrand.IssuesUri;

    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand { get; }

    /// <inheritdoc />
    public ReactiveCommand<Unit, Unit> ToggleDownloadsPausedCommand { get; }

    private bool _downloadsPaused;
    /// <inheritdoc />
    public bool DownloadsPaused
    {
        get => _downloadsPaused;
        private set => this.RaiseAndSetIfChanged(ref _downloadsPaused, value);
    }

    private string _rateLimitText = "Rate Meter: no calls yet";
    public string RateLimitText
    {
        get => _rateLimitText;
        private set => this.RaiseAndSetIfChanged(ref _rateLimitText, value);
    }

    private string _rateLimitDetails = "Nexus Mods API usage appears here after the first download or update check.";
    public string RateLimitDetails
    {
        get => _rateLimitDetails;
        private set => this.RaiseAndSetIfChanged(ref _rateLimitDetails, value);
    }

    private bool _isRateLimitLow;
    public bool IsRateLimitLow
    {
        get => _isRateLimitLow;
        private set => this.RaiseAndSetIfChanged(ref _isRateLimitLow, value);
    }

    public DevelopmentBuildBannerViewModel(IOSInterop osInterop, NexusApiRateLimitTracker rateLimitTracker, IDownloadsService downloadsService)
    {
        GiveFeedbackCommand = ReactiveCommand.Create(() => osInterop.OpenUri(GiveFeedbackUri));

        // Afterburn: pause/resume all downloads from the bottom bar. While paused, downloads that start later
        // (for example the next files of a collection) are paused too, every couple of seconds.
        ToggleDownloadsPausedCommand = ReactiveCommand.Create(() =>
        {
            DownloadsPaused = !DownloadsPaused;
            if (DownloadsPaused) downloadsService.PauseAll();
            else downloadsService.ResumeAll();
        });
        _pauseTimer = new System.Threading.Timer(_ =>
        {
            if (!DownloadsPaused) return;
            try { downloadsService.PauseAll(); } catch { /* best effort */ }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        Apply(rateLimitTracker.Current);

        // The bottom bar lives as long as the main window, so subscribe for the view model's lifetime.
        rateLimitTracker.Changed += snapshot => Dispatcher.UIThread.Post(() => Apply(snapshot));
    }

    private readonly System.Threading.Timer _pauseTimer;

    private void Apply(NexusApiRateLimitSnapshot s)
    {
        if (s.RequestsThisSession == 0) return;

        var limitedRecently = s.LastRateLimitedAt is { } t && DateTime.Now - t < TimeSpan.FromMinutes(2);
        var dailyLow = s.DailyLimit > 0 && s.DailyRemaining <= Math.Max(100, s.DailyLimit / 20);
        var dailyOut = s.DailyLimit > 0 && s.DailyRemaining <= 0;

        // Afterburn: the bottom bar shows only the daily allowance; the hourly numbers stay in the tooltip.
        if (limitedRecently)
        {
            var reset = s.DailyReset != default ? $" · resets {Time(s.DailyReset)}" : "";
            RateLimitText = $"Rate Meter: limited{reset}";
        }
        else if (s.HasData)
        {
            RateLimitText = dailyOut
                ? $"Rate Meter: daily limit used up · resets {Time(s.DailyReset)}"
                : $"Rate Meter: {N(s.DailyRemaining)}/{N(s.DailyLimit)} today";
        }
        else
        {
            RateLimitText = $"Rate Meter: {N(s.RequestsThisSession)} calls this session";
        }

        IsRateLimitLow = limitedRecently || dailyLow || dailyOut;

        var lines = new List<string>
        {
            "Rate Meter · Nexus Mods API usage",
            s.HasData ? $"Hourly: {N(s.HourlyRemaining)} of {N(s.HourlyLimit)} left · resets {Time(s.HourlyReset)}" : "Hourly: unknown",
            s.HasData ? $"Daily: {N(s.DailyRemaining)} of {N(s.DailyLimit)} left · resets {Time(s.DailyReset)}" : "Daily: unknown",
            $"Calls this session: {N(s.RequestsThisSession)}",
            $"Rate-limited (429) this session: {N(s.RateLimitedThisSession)}",
        };
        if (s.LastRateLimitedAt is { } last) lines.Add($"Last rate-limited: {last.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}");
        if (s.LastUpdated is { } upd) lines.Add($"Updated: {upd.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}");
        lines.Add("Tip: lower Max Parallel Downloads if you keep hitting the limit.");
        RateLimitDetails = string.Join(Environment.NewLine, lines);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Time(DateTime value) =>
        value == default ? "unknown" : value.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
}
