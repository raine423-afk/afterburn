using System.Collections.ObjectModel;
using System.Reactive;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.Sdk.Loadouts;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Storage;

public interface IStoragePageViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    /// <summary>Summary of where Afterburn keeps its archives and how big they are.</summary>
    string StorageSummary { get; }

    /// <summary>Free space on the drive that holds the archives.</summary>
    string FreeSpaceText { get; }

    /// <summary>Result of the last clean-up, or what clean-up does.</summary>
    string CleanupStatus { get; }

    /// <summary>One-line summary of the game-folder files Afterburn didn't install.</summary>
    string UnmanagedSummary { get; }

    ReadOnlyObservableCollection<UnmanagedFolderRow> UnmanagedFolders { get; }

    ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    ReactiveCommand<Unit, Unit> CleanupCommand { get; }
    ReactiveCommand<Unit, Unit> OpenArchivesFolderCommand { get; }
}
