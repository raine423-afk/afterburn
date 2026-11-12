using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// Afterburn: gives the UI the full result of the pre-launch conflict check, so it can be shown on the mod list page.
/// </summary>
public interface IConflictReportProvider
{
    /// <summary>The latest report for a loadout, from this session or the last one saved in the game folder. Null if none.</summary>
    ConflictReport? GetLatest(LoadoutId loadoutId);

    /// <summary>Runs the conflict check now and returns its report.</summary>
    Task<ConflictReport> RunNowAsync(LoadoutId loadoutId, CancellationToken cancellationToken);

    /// <summary>Raised (on a background thread) whenever a new report is made for a loadout.</summary>
    event Action<LoadoutId, ConflictReport>? ReportChanged;
}

/// <summary>One run of the conflict check.</summary>
public sealed record ConflictReport(DateTimeOffset CheckedAt, string Summary, IReadOnlyList<ConflictReportSection> Sections);

/// <summary>A titled list of findings, for example "Script conflicts".</summary>
public sealed record ConflictReportSection(string Title, string Explanation, IReadOnlyList<string> Items);
