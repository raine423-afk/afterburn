using System.Net;
using NexusMods.Abstractions.NexusWebApi.DTOs;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// Snapshot of the Nexus Mods API (v1) rate limits, as reported by the last API response.
/// </summary>
public sealed record NexusApiRateLimitSnapshot(
    int HourlyLimit,
    int HourlyRemaining,
    DateTime HourlyReset,
    int DailyLimit,
    int DailyRemaining,
    DateTime DailyReset,
    long RequestsThisSession,
    long RateLimitedThisSession,
    DateTime? LastRateLimitedAt,
    DateTime? LastUpdated)
{
    /// <summary>True once at least one response with rate-limit headers has been seen.</summary>
    public bool HasData => HourlyLimit > 0 || DailyLimit > 0;

    /// <summary>Empty snapshot.</summary>
    public static readonly NexusApiRateLimitSnapshot Empty = new(0, 0, default, 0, 0, default, 0, 0, null, null);
}

/// <summary>
/// Keeps track of the Nexus Mods API rate limits by watching the headers of every API response.
/// </summary>
public sealed class NexusApiRateLimitTracker
{
    private readonly object _lock = new();
    private NexusApiRateLimitSnapshot _current = NexusApiRateLimitSnapshot.Empty;

    /// <summary>Raised (on a background thread) whenever the snapshot changes.</summary>
    public event Action<NexusApiRateLimitSnapshot>? Changed;

    /// <summary>The latest known state.</summary>
    public NexusApiRateLimitSnapshot Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Record the result of one API call.</summary>
    public void Report(ResponseMetadata metadata, HttpStatusCode statusCode)
    {
        NexusApiRateLimitSnapshot updated;
        lock (_lock)
        {
            var prev = _current;
            var limited = statusCode == HttpStatusCode.TooManyRequests;
            var hasHeaders = metadata.HourlyLimit > 0 || metadata.DailyLimit > 0;
            var now = DateTime.Now;

            updated = prev with
            {
                HourlyLimit = hasHeaders ? metadata.HourlyLimit : prev.HourlyLimit,
                HourlyRemaining = hasHeaders ? metadata.HourlyRemaining : (limited ? 0 : prev.HourlyRemaining),
                HourlyReset = hasHeaders && metadata.HourlyReset != default ? metadata.HourlyReset : prev.HourlyReset,
                DailyLimit = hasHeaders ? metadata.DailyLimit : prev.DailyLimit,
                DailyRemaining = hasHeaders ? metadata.DailyRemaining : prev.DailyRemaining,
                DailyReset = hasHeaders && metadata.DailyReset != default ? metadata.DailyReset : prev.DailyReset,
                RequestsThisSession = prev.RequestsThisSession + 1,
                RateLimitedThisSession = prev.RateLimitedThisSession + (limited ? 1 : 0),
                LastRateLimitedAt = limited ? now : prev.LastRateLimitedAt,
                LastUpdated = now,
            };
            _current = updated;
        }

        try { Changed?.Invoke(updated); }
        catch { /* a UI listener must never break API calls */ }
    }
}
