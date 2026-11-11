using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Aggregation;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Sdk.Resources;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Pages.LibraryPage;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.DatomIterators;
using NexusMods.MnemonicDB.Abstractions.Query;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Networking.NexusWebApi.UpdateFilters;
using NuGet.Versioning;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using R3;

namespace NexusMods.App.UI.Pages;

public class NexusModsDataProvider : ILibraryDataProvider, ILoadoutDataProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnection _connection;
    private readonly IModUpdateService _modUpdateService;
    private readonly IModUpdateFilterService _modUpdateFilterService;
    private readonly Lazy<IResourceLoader<EntityId, Bitmap>> _thumbnailLoader;

    public NexusModsDataProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _modUpdateService = serviceProvider.GetRequiredService<IModUpdateService>();
        _modUpdateFilterService = serviceProvider.GetRequiredService<IModUpdateFilterService>();

        _thumbnailLoader = new Lazy<IResourceLoader<EntityId, Bitmap>>(() => ImagePipelines.GetModPageThumbnailPipeline(serviceProvider));
    }

    public LibraryFile.ReadOnly[] GetAllFiles(GameId gameId, IDb? db = null)
    {
        var nexusModsGameId = _serviceProvider.GetServices<IGameData>().First(x => x.GameId == gameId).NexusModsGameId;
        if (!nexusModsGameId.HasValue) return [];

        db ??= _connection.Db;

        var libraryItems = NexusModsLibraryItem
            .All(db)
            .Where(libraryItem => libraryItem.AsLibraryItem().TryGetAsLibraryFile(out _))
            .GroupBy(libraryItem => libraryItem.FileMetadataId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var files = NexusModsFileMetadata
            .All(db)
            .Where(modPage => modPage.Uid.GameId == nexusModsGameId)
            .Select(fileMetadata => libraryItems.GetValueOrDefault(fileMetadata))
            .Where(static arr => arr is not null)
            .SelectMany(static x => x!)
            .Select(libraryItem => new LibraryFile.ReadOnly(db, libraryItem))
            .ToArray();

        return files;
    }

    private IObservable<IChangeSet<NexusModsModPageMetadata.ReadOnly, EntityId>> FilterLibraryItems(LibraryFilter libraryFilter)
    {
        // Afterburn: driven by the shared library snapshot instead of database watchers per mod page.
        return LibrarySnapshots.Observe(_connection, libraryFilter)
            .Select(static snapshot => (IEnumerable<EntityId>)snapshot.ItemsByModPage.Keys.ToArray())
            .ToDiffChangeSet(static id => id, static (a, b) => a.Equals(b))
            .Transform(id => NexusModsModPageMetadata.Load(_connection.Db, id));
    }

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLibraryItems(LibraryFilter libraryFilter)
    {
        return FilterLibraryItems(libraryFilter).Transform(modPage => ToLibraryItemModel(modPage, libraryFilter));
    }

    public IObservable<int> CountLibraryItems(LibraryFilter libraryFilter)
    {
        return FilterLibraryItems(libraryFilter).QueryWhenChanged(query => query.Count).Prepend(0);
    }

    private CompositeItemModel<EntityId> ToLibraryItemModel(NexusModsModPageMetadata.ReadOnly modPage, LibraryFilter libraryFilter)
    {
        // Afterburn: everything this row shows comes from the shared library snapshot.
        var snapshots = LibrarySnapshots.Observe(_connection, libraryFilter);
        var modPageId = modPage.Id;

        var libraryItems = snapshots
            .Select(snapshot => (IEnumerable<NexusModsLibraryItem.ReadOnly>)snapshot.ItemsFor(modPageId))
            .ToDiffChangeSet(static item => item.Id, static (a, b) => a.Id.Equals(b.Id))
            .RefCount();

        var linkedLoadoutItemsObservable = snapshots
            .Select(snapshot => (IEnumerable<LoadoutItem.ReadOnly>)snapshot.LinkedForModPage(modPageId))
            .ToDiffChangeSet(static item => item.Id, static (a, b) => a.Id.Equals(b.Id) && a.HasParent() == b.HasParent() && (!a.HasParent() || a.ParentId.Equals(b.ParentId)))
            .RefCount();

        var hasChildrenObservable = libraryItems.IsNotEmpty();
        var childrenObservable = libraryItems.Transform(libraryItem => ToLibraryItemModel(libraryItem, libraryFilter));

        var parentItemModel = new CompositeItemModel<EntityId>(modPage.Id)
        {
            HasChildrenObservable = hasChildrenObservable,
            ChildrenObservable = childrenObservable,
        };
        
        parentItemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: modPage.Name));
        parentItemModel.Add(SharedColumns.Name.ImageComponentKey, ImageComponent.FromPipeline(_thumbnailLoader.Value, modPage.Id, initialValue: ImagePipelines.ModPageThumbnailFallback));

        // Size: sum of library files
        var sizeObservable = libraryItems
            .TransformImmutable(static item => LibraryFile.Size.GetOptional(item).ValueOr(() => Size.Zero))
            .ForAggregation()
            .Sum(static size => (long)size.Value)
            .Select(static size => Size.FromLong(size));

        parentItemModel.Add(SharedColumns.ItemSize.ComponentKey, new SizeComponent(
            initialValue: Size.Zero,
            valueObservable: sizeObservable
        ));

        // Downloaded date: most recent downloaded file date
        var downloadedDateObservable = libraryItems
            .TransformImmutable(static item => item.GetCreatedAt())
            .QueryWhenChanged(query => query.Items.OptionalMaxBy(item => item).ValueOr(DateTimeOffset.MinValue));

        parentItemModel.Add(LibraryColumns.DownloadedDate.ComponentKey, new DateComponent(
            initialValue: modPage.GetCreatedAt(),
            valueObservable: downloadedDateObservable
        ));

        // Version: highest version number
        var currentVersionObservable = libraryItems
            .TransformImmutable(static item =>
            {
                // NOTE(erri120, sewer): simplest version parsing for now
                var rawVersion = item.FileMetadata.Version;
                _ = NuGetVersion.TryParse(rawVersion, out var parsedVersion);

                return (rawVersion, parsedVersion: Optional<NuGetVersion>.Create(parsedVersion));
            })
            .QueryWhenChanged(static query =>
            {
                var max = query.Items.OptionalMaxBy(static tuple => tuple.parsedVersion.ValueOr(new NuGetVersion(0, 0, 0)));
                if (!max.HasValue) return string.Empty;
                return max.Value.rawVersion;
            });

        parentItemModel.Add(LibraryColumns.ItemVersion.CurrentVersionComponentKey, new VersionComponent(
            initialValue: string.Empty,
            valueObservable: currentVersionObservable
        ));
        
        // Collections column
        AddRelatedCollectionsComponentToParent(modPageId, libraryFilter, linkedLoadoutItemsObservable, parentItemModel);
        
        // Update available
        var newestModPageObservable = _modUpdateService.GetNewestModPageVersionObservable(modPage);
        var currentUpdateVersionObservable = newestModPageObservable
            .Select(static optional => !optional.HasValue ? "" : optional.Value.MappingWithNewestFile().File.Version)
            .OnUI();
        var newestVersionObservable = newestModPageObservable
            .Select(static optional => optional.Convert(static updatesOnPage => updatesOnPage.NewestFile().Version))
            .OnUI();

        parentItemModel.AddObservable(
            key: LibraryColumns.ItemVersion.NewVersionComponentKey,
            observable: newestVersionObservable,
            componentFactory: (valueObservable, initialValue) => new LibraryComponents.NewVersionAvailable(
                currentVersion: new VersionComponent(
                    initialValue: string.Empty,
                    valueObservable: currentUpdateVersionObservable
                ),
                newVersion: initialValue,
                newVersionObservable: valueObservable
            )
        );

        // Update button
        var updatesOnPage = _modUpdateService.GetNewestModPageVersionObservable(modPage);
        parentItemModel.AddObservable(
            key: LibraryColumns.Actions.UpdateComponentKey,
            observable: updatesOnPage,
            componentFactory: static (valueObservable, initialValue) => new LibraryComponents.UpdateAction(
                initialValue,
                valueObservable
            )
        );

        LibraryDataProviderHelper.AddInstalledDateComponent(parentItemModel, linkedLoadoutItemsObservable);

        var matchesObservable = snapshots
            .Select(snapshot => snapshot.MatchesFor(modPageId))
            .DistinctUntilChanged();
        
        
        parentItemModel.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(libraryItems.TransformImmutable(static x => x.AsLibraryItem().LibraryItemId)));
        LibraryDataProviderHelper.AddInstallActionComponent(parentItemModel, matchesObservable);
        LibraryDataProviderHelper.AddViewChangelogActionComponent(parentItemModel);
        LibraryDataProviderHelper.AddViewModPageActionComponent(parentItemModel);

        var updatesOnPageUnfiltered = _modUpdateService.GetNewestModPageVersionObservableUnfiltered(modPage);
        
        // Note(sewer):
        // Hide the button if there are no updates available for any files on this mod page
        // BUT
        // We have to be very careful here! We only hide if we have the latest version of the mods
        // from the mod page! If there is an update that's hidden, we still have to show the button,
        // else the user has no way to unhide the update.
        var isVisibleObservable = updatesOnPageUnfiltered
            .Select(optional => optional.HasValue && optional.Value.HasAnyUpdates)
            .ToObservable();
        LibraryDataProviderHelper.AddHideUpdatesActionComponent(parentItemModel, updatesOnPageUnfiltered, _modUpdateFilterService, isVisibleObservable);
        
        LibraryDataProviderHelper.AddDeleteItemActionComponent(parentItemModel);

        return parentItemModel;
    }

    private CompositeItemModel<EntityId> ToLibraryItemModel(NexusModsLibraryItem.ReadOnly libraryItem, LibraryFilter libraryFilter)
    {
        // Afterburn: from the shared library snapshot instead of a database watcher per file.
        var libraryItemId = libraryItem.Id;
        var linkedLoadoutItemsObservable = LibrarySnapshots.Observe(_connection, libraryFilter)
            .Select(snapshot => (IEnumerable<LoadoutItem.ReadOnly>)snapshot.LinkedFor(libraryItemId))
            .ToDiffChangeSet(static item => item.Id, static (a, b) => a.Id.Equals(b.Id) && a.HasParent() == b.HasParent() && (!a.HasParent() || a.ParentId.Equals(b.ParentId)))
            .RefCount();

        var fileMetadata = libraryItem.FileMetadata;

        var itemModel = new CompositeItemModel<EntityId>(libraryItem.Id);

        itemModel.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(libraryItem.AsLibraryItem()));
        itemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: fileMetadata.Name));
        itemModel.Add(LibraryColumns.DownloadedDate.ComponentKey, new DateComponent(value: libraryItem.GetCreatedAt()));
        itemModel.Add(LibraryColumns.ItemVersion.CurrentVersionComponentKey, new VersionComponent(value: fileMetadata.Version));

        if (libraryItem.FileMetadata.Size.TryGet(out var size))
            itemModel.Add(SharedColumns.ItemSize.ComponentKey, new SizeComponent(value: size));

        LibraryDataProviderHelper.AddInstalledDateComponent(itemModel, linkedLoadoutItemsObservable);
        LibraryDataProviderHelper.AddInstallActionComponent(itemModel, linkedLoadoutItemsObservable);
        LibraryDataProviderHelper.AddViewChangelogActionComponent(itemModel);
        LibraryDataProviderHelper.AddViewModPageActionComponent(itemModel);
        
        // Collections column
        AddRelatedCollectionsComponentToChild(libraryItem, libraryFilter, linkedLoadoutItemsObservable, itemModel);

        // Update available
        var newestVersionObservable = _modUpdateService
            .GetNewestFileVersionObservable(fileMetadata)
            .Select(static optional => optional.Convert(static updateOnPage => updateOnPage.NewestFile.Version))
            .OnUI();

        itemModel.AddObservable(
            key: LibraryColumns.ItemVersion.NewVersionComponentKey,
            observable: newestVersionObservable,
            componentFactory: (valueObservable, initialValue) => new LibraryComponents.NewVersionAvailable(
                currentVersion: new VersionComponent(value: libraryItem.FileMetadata.Version),
                newVersion: initialValue,
                newVersionObservable: valueObservable
            )
        );

        // Update button
        var newestFile = _modUpdateService.GetNewestFileVersionObservable(fileMetadata);

        itemModel.AddObservable(
            key: LibraryColumns.Actions.UpdateComponentKey,
            observable: newestFile,
            componentFactory: static (valueObservable, initialValue) => new LibraryComponents.UpdateAction(
                initialValue,
                valueObservable
            )
        );

        var newestFileUnfiltered = _modUpdateService.GetNewestFileVersionObservableUnfiltered(fileMetadata);
        
        // Note(sewer):
        // Hide the button if there are no updates available for this mod.
        // BUT
        // We have to be very careful here! We only hide if we have the latest version of the mods
        // from the mod page! If there is an update that's hidden, we still have to show the button,
        // else the user has no way to unhide the update.
        var isVisibleObservable = newestFileUnfiltered
            .Select(optional => optional.HasValue)
            .ToObservable();
        
        LibraryDataProviderHelper.AddHideUpdatesActionComponent(itemModel, newestFileUnfiltered, _modUpdateFilterService, isVisibleObservable);
        
        LibraryDataProviderHelper.AddDeleteItemActionComponent(itemModel);

        return itemModel;
    }

    private void AddRelatedCollectionsComponentToChild(
        NexusModsLibraryItem.ReadOnly libraryItem,
        LibraryFilter libraryFilter,
        IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedLoadoutItemsObservable,
        CompositeItemModel<EntityId> itemModel)
    {
        var fileMetadataId = libraryItem.FileMetadata.Id;
        var relatedDownloadedCollectionsObservable = LibrarySnapshots.Observe(_connection, libraryFilter)
            .Select(snapshot => (IEnumerable<CollectionMetadata.ReadOnly>)snapshot.CollectionsFor(fileMetadataId))
            .ToDiffChangeSet(static c => c.Id, static (a, b) => a.Id.Equals(b.Id));

        LibraryDataProviderHelper.AddRelatedCollectionsComponent(itemModel, linkedLoadoutItemsObservable, relatedDownloadedCollectionsObservable);
    }

    private void AddRelatedCollectionsComponentToParent(
        EntityId modPageId,
        LibraryFilter libraryFilter,
        IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedLoadoutItemsObservable,
        CompositeItemModel<EntityId> itemModel)
    {
        var relatedDownloadedCollectionsObservable = LibrarySnapshots.Observe(_connection, libraryFilter)
            .Select(snapshot => (IEnumerable<CollectionMetadata.ReadOnly>)snapshot.CollectionsForModPage(modPageId))
            .ToDiffChangeSet(static c => c.Id, static (a, b) => a.Id.Equals(b.Id));

        LibraryDataProviderHelper.AddRelatedCollectionsComponent(itemModel, linkedLoadoutItemsObservable, relatedDownloadedCollectionsObservable);
    }

    private IObservable<IChangeSet<NexusModsModPageMetadata.ReadOnly, EntityId>> FilterLoadoutItems(LoadoutFilter loadoutFilter)
    {
        // Afterburn: one shared snapshot of the loadout instead of database watchers for every mod page and file.
        return LoadoutSnapshots.Observe(_connection, loadoutFilter)
            .RowKeys(static snapshot => snapshot.ByNexusModPage)
            .Transform(id => NexusModsModPageMetadata.Load(_connection.Db, id));
    }

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLoadoutItems(LoadoutFilter loadoutFilter)
    {
        var snapshots = LoadoutSnapshots.Observe(_connection, loadoutFilter);

        return FilterLoadoutItems(loadoutFilter).Transform(modPage =>
        {
            var modPageId = modPage.Id;

            // Afterburn: everything a row shows comes from the shared snapshot. A row only hears about a change when
            // one of its own files was added, removed, enabled, disabled or moved.
            var rowItems = snapshots.RowItems(snapshot => snapshot.ItemsFor(modPageId));
            var linkedItemsObservable = rowItems.LinkedItems();

            var hasChildrenObservable = linkedItemsObservable.IsNotEmpty();
            var childrenObservable = linkedItemsObservable.Transform(loadoutItem => ToNexusChildLoadoutItemModel(_connection, loadoutItem));

            var parentItemModel = new CompositeItemModel<EntityId>(modPage.Id)
            {
                HasChildrenObservable = hasChildrenObservable,
                ChildrenObservable = childrenObservable,
            };

            parentItemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: modPage.Name));
            parentItemModel.Add(SharedColumns.Name.ImageComponentKey, ImageComponent.FromPipeline(_thumbnailLoader.Value, modPage.Id, initialValue: ImagePipelines.ModPageThumbnailFallback));

            LoadoutDataProviderHelper.AddDateComponent(parentItemModel, modPage.GetCreatedAt(), linkedItemsObservable);
            LoadoutDataProviderHelper.AddCollections(parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddParentCollectionsDisabled(parentItemModel, rowItems);
            LoadoutDataProviderHelper.AddMixLockedAndParentDisabled(parentItemModel, rowItems);
            LoadoutDataProviderHelper.AddLockedEnabledStates(parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddEnabledStateToggle(parentItemModel, rowItems);
            LoadoutDataProviderHelper.AddLoadoutItemIds(parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddViewModPageActionComponent(parentItemModel, isEnabled: true);
            LoadoutDataProviderHelper.AddViewModFilesActionComponent(parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddUninstallItemComponent(parentItemModel, linkedItemsObservable);

            return parentItemModel;
        });
    }

    private static CompositeItemModel<EntityId> ToNexusChildLoadoutItemModel(IConnection connection, LoadoutItem.ReadOnly loadoutItem)
    {
        var childModel = LoadoutDataProviderHelper.ToChildItemModel(connection, loadoutItem);
        LoadoutDataProviderHelper.AddViewModPageActionComponent(childModel, isEnabled: true);
        return childModel;
    }

    public IObservable<int> CountLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutItems(loadoutFilter).QueryWhenChanged(static query => query.Count).Prepend(0);
    }
}

file class DatomEntityIdEqualityComparer : IEqualityComparer<Datom>
{
    public static readonly IEqualityComparer<Datom> Instance = new DatomEntityIdEqualityComparer();

    public bool Equals(Datom x, Datom y)
    {
        return x.E == y.E;
    }

    public int GetHashCode(Datom obj)
    {
        return obj.E.GetHashCode();
    }
}
