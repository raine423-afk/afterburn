using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using DynamicData;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.DatomIterators;
using NexusMods.MnemonicDB.Abstractions.Query;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Pages;

/// <summary>
/// Afterburn: everything the Library list shows about Nexus Mods downloads for one game and loadout, read from the
/// database in one pass.
/// </summary>
public sealed class LibrarySnapshot
{
    private static readonly IReadOnlyDictionary<EntityId, NexusModsLibraryItem.ReadOnly[]> NoItems = new Dictionary<EntityId, NexusModsLibraryItem.ReadOnly[]>();
    private static readonly IReadOnlyDictionary<EntityId, LoadoutItem.ReadOnly[]> NoLinked = new Dictionary<EntityId, LoadoutItem.ReadOnly[]>();
    private static readonly IReadOnlyDictionary<EntityId, CollectionMetadata.ReadOnly[]> NoCollections = new Dictionary<EntityId, CollectionMetadata.ReadOnly[]>();

    public static readonly LibrarySnapshot Empty = new(NoItems, NoLinked, NoCollections);

    public LibrarySnapshot(
        IReadOnlyDictionary<EntityId, NexusModsLibraryItem.ReadOnly[]> itemsByModPage,
        IReadOnlyDictionary<EntityId, LoadoutItem.ReadOnly[]> linkedByLibraryItem,
        IReadOnlyDictionary<EntityId, CollectionMetadata.ReadOnly[]> collectionsByFileMetadata)
    {
        ItemsByModPage = itemsByModPage;
        LinkedByLibraryItem = linkedByLibraryItem;
        CollectionsByFileMetadata = collectionsByFileMetadata;
    }

    /// <summary>Downloaded files of this game, grouped by mod page.</summary>
    public IReadOnlyDictionary<EntityId, NexusModsLibraryItem.ReadOnly[]> ItemsByModPage { get; }

    /// <summary>Where each downloaded file is installed in the current loadout.</summary>
    public IReadOnlyDictionary<EntityId, LoadoutItem.ReadOnly[]> LinkedByLibraryItem { get; }

    /// <summary>Collections that include each file.</summary>
    public IReadOnlyDictionary<EntityId, CollectionMetadata.ReadOnly[]> CollectionsByFileMetadata { get; }

    public NexusModsLibraryItem.ReadOnly[] ItemsFor(EntityId modPageId) =>
        ItemsByModPage.TryGetValue(modPageId, out var items) ? items : [];

    public LoadoutItem.ReadOnly[] LinkedFor(EntityId libraryItemId) =>
        LinkedByLibraryItem.TryGetValue(libraryItemId, out var items) ? items : [];

    public LoadoutItem.ReadOnly[] LinkedForModPage(EntityId modPageId) =>
        ItemsFor(modPageId).SelectMany(item => LinkedFor(item.Id)).ToArray();

    public CollectionMetadata.ReadOnly[] CollectionsFor(EntityId fileMetadataId) =>
        CollectionsByFileMetadata.TryGetValue(fileMetadataId, out var items) ? items : [];

    public CollectionMetadata.ReadOnly[] CollectionsForModPage(EntityId modPageId) =>
        ItemsFor(modPageId)
            .SelectMany(item => CollectionsFor(item.FileMetadata.Id))
            .DistinctBy(static c => c.Id)
            .ToArray();

    public MatchesData MatchesFor(EntityId modPageId)
    {
        var items = ItemsFor(modPageId);
        var installed = 0;
        foreach (var item in items)
        {
            if (LinkedFor(item.Id).Length > 0) installed++;
        }

        return new MatchesData(installed, items.Length);
    }
}

/// <summary>
/// Afterburn: shares one live <see cref="LibrarySnapshot"/> stream per library filter.
/// <para>
/// The original Library list gave every mod page and every downloaded file its own database watchers. Every database
/// change made all of them re-check, which froze the app for seconds with a large library. Here a few whole-attribute
/// watchers trigger one re-read (at most a few times a second) and rows only hear about changes that concern them.
/// </para>
/// </summary>
public static class LibrarySnapshots
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(150);
    private static readonly ConcurrentDictionary<string, IObservable<LibrarySnapshot>> Shared = new();

    public static IObservable<LibrarySnapshot> Observe(IConnection connection, LibraryFilter filter)
    {
        var key = $"{filter.LoadoutId.Value}/{filter.Game.NexusModsGameId}";
        return Shared.GetOrAdd(key, _ =>
        {
            var changes = Observable.Merge(
                connection.ObserveDatoms(SliceDescriptor.Create(connection.AttributeCache.GetAttributeId(NexusModsLibraryItem.ModPageMetadata.Id))).Select(static _ => 0),
                connection.ObserveDatoms(SliceDescriptor.Create(connection.AttributeCache.GetAttributeId(LibraryLinkedLoadoutItem.LibraryItem.Id))).Select(static _ => 0),
                connection.ObserveDatoms(SliceDescriptor.Create(connection.AttributeCache.GetAttributeId(CollectionDownloadNexusMods.FileMetadata.Id))).Select(static _ => 0)
            );

            return changes
                .StartWith(0)
                .Throttle(Coalesce, TaskPoolScheduler.Default)
                .Select(_ => Build(connection.Db, filter))
                .Replay(1)
                .RefCount();
        });
    }

    public static LibrarySnapshot Build(IDb db, LibraryFilter filter)
    {
        var gameId = filter.Game.NexusModsGameId;

        var itemsByModPage = new Dictionary<EntityId, List<NexusModsLibraryItem.ReadOnly>>();
        foreach (var item in NexusModsLibraryItem.All(db))
        {
            if (!item.IsValid()) continue;
            var modPage = item.ModPageMetadata;
            if (modPage.Uid.GameId != gameId) continue;
            if (!itemsByModPage.TryGetValue(modPage.Id, out var list)) itemsByModPage[modPage.Id] = list = [];
            list.Add(item);
        }

        var linkedByLibraryItem = new Dictionary<EntityId, List<LoadoutItem.ReadOnly>>();
        foreach (var linked in LibraryLinkedLoadoutItem.All(db))
        {
            var loadoutItem = LoadoutItem.Load(db, linked.Id);
            if (!loadoutItem.IsValid()) continue;
            if (!loadoutItem.LoadoutId.Equals(filter.LoadoutId)) continue;
            var libraryItemId = linked.LibraryItem.Id;
            if (!linkedByLibraryItem.TryGetValue(libraryItemId, out var list)) linkedByLibraryItem[libraryItemId] = list = [];
            list.Add(loadoutItem);
        }

        var collectionsByFile = new Dictionary<EntityId, List<CollectionMetadata.ReadOnly>>();
        foreach (var download in CollectionDownloadNexusMods.All(db))
        {
            CollectionMetadata.ReadOnly collection;
            try
            {
                collection = NexusMods.Abstractions.NexusModsLibrary.Models.CollectionDownload.Load(db, download.Id).CollectionRevision.Collection;
            }
            catch
            {
                continue;
            }

            var fileId = download.FileMetadata.Id;
            if (!collectionsByFile.TryGetValue(fileId, out var list)) collectionsByFile[fileId] = list = [];
            if (!list.Any(c => c.Id.Equals(collection.Id))) list.Add(collection);
        }

        return new LibrarySnapshot(
            itemsByModPage.ToDictionary(static kv => kv.Key, static kv => kv.Value.ToArray()),
            linkedByLibraryItem.ToDictionary(static kv => kv.Key, static kv => kv.Value.ToArray()),
            collectionsByFile.ToDictionary(static kv => kv.Key, static kv => kv.Value.ToArray()));
    }
}
