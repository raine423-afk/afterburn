using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Sdk;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.DevelopmentBuildBanner;

public class DevelopmentBuildBannerViewModel : AViewModel<IDevelopmentBuildBannerViewModel>, IDevelopmentBuildBannerViewModel
{
    private static readonly Uri GiveFeedbackUri = AfterburnBrand.IssuesUri;

    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand { get; }

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

    public DevelopmentBuildBannerViewModel(IOSInterop osInterop, NexusApiRateLimitTracker rateLimitTracker)
    {
        GiveFeedbackCommand = ReactiveCommand.Create(() => osInterop.OpenUri(GiveFeedbackUri));

        Apply(rateLimitTracker.Current);

        // The bottom bar lives as long as the main window, so subscribe for the view model's lifetime.
        rateLimitTracker.Changed += snapshot => Dispatcher.UIThread.Post(() => Apply(snapshot));
    }

    private void Apply(NexusApiRateLimitSnapshot s)
    {
        if (s.RequestsThisSession == 0) return;

        var limitedRecently = s.LastRateLimitedAt is { } t && DateTime.Now - t < TimeSpan.FromMinutes(2);
        var hourlyLow = s.HourlyLimit > 0 && s.HourlyRemaining <= Math.Max(25, s.HourlyLimit / 20);
        var dailyOut = s.DailyLimit > 0 && s.DailyRemaining <= 0;

        if (limitedRecently)
        {
            var reset = s.HourlyReset != default ? $" · resets {Time(s.HourlyReset)}" : "";
            RateLimitText = $"Rate Meter: limited{reset}";
        }
        else if (s.HasData)
        {
            RateLimitText = dailyOut
                ? $"Rate Meter: {N(s.HourlyRemaining)}/{N(s.HourlyLimit)} per hr (daily used up)"
                : $"Rate Meter: {N(s.HourlyRemaining)}/{N(s.HourlyLimit)} per hr · {N(s.DailyRemaining)}/{N(s.DailyLimit)} today";
        }
        else
        {
            RateLimitText = $"Rate Meter: {N(s.RequestsThisSession)} calls this session";
        }

        IsRateLimitLow = limitedRecently || hourlyLow || (dailyOut && s.HourlyRemaining <= Math.Max(25, s.HourlyLimit / 20));

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
