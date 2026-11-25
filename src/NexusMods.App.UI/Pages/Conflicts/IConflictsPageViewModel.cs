using System.Reactive;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Sdk.Loadouts;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Conflicts;

public interface IConflictsPageViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    string Summary { get; }
    string Status { get; }
    bool IsBusy { get; }

    /// <summary>Filters every list (file path, mod name, hash, download, collection).</summary>
    string SearchText { get; set; }

    /// <summary>Only show files where at least one copy from a collection is hidden.</summary>
    bool OnlyHiddenCollectionFiles { get; set; }

    string FilesHeader { get; }
    string PairsHeader { get; }
    string ModsHeader { get; }
    string GroupsHeader { get; }

    IReadOnlyList<ConflictFile> VisibleFiles { get; }
    IReadOnlyList<ModPairRow> VisiblePairs { get; }
    IReadOnlyList<ConflictModRow> VisibleMods { get; }
    IReadOnlyList<ConflictGroupRow> VisibleGroups { get; }

    ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    ReactiveCommand<Unit, Unit> AutoResolveCommand { get; }
    ReactiveCommand<Unit, Unit> CopyReportCommand { get; }
    ReactiveCommand<Unit, Unit> SaveReportCommand { get; }
    ReactiveCommand<Unit, Unit> SaveCsvCommand { get; }
}
