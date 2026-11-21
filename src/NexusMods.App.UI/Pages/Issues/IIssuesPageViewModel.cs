using System.Reactive;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Sdk.Loadouts;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Issues;

public interface IIssuesPageViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    /// <summary>One-line totals for the whole page.</summary>
    string Summary { get; }

    /// <summary>What the page is doing, or the result of the last action.</summary>
    string Status { get; }

    bool IsBusy { get; }

    /// <summary>Filters every list on the page (mod name, file path, message text).</summary>
    string SearchText { get; set; }

    string ModsHeader { get; }
    string ConflictsHeader { get; }
    string HealthHeader { get; }
    string LogsHeader { get; }

    IReadOnlyList<ModConflictRow> VisibleMods { get; }
    IReadOnlyList<FileConflictRow> VisibleConflicts { get; }
    IReadOnlyList<HealthIssueRow> VisibleHealth { get; }
    IReadOnlyList<LogIssueRow> VisibleLogs { get; }

    ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    ReactiveCommand<Unit, Unit> CopyReportCommand { get; }
    ReactiveCommand<Unit, Unit> SaveReportCommand { get; }
    ReactiveCommand<Unit, Unit> AutoResolveCommand { get; }
}
