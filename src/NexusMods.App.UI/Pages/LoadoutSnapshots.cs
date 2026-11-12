using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.DatomIterators;
using NexusMods.MnemonicDB.Abstractions.Query;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Pages;

/// <summary>
/// Afterburn: one installed mod (a library-linked loadout item) as seen in a <see cref="LoadoutSnapshot"/>.
/// </summary>
public readonly record struct SnapshotItem(
    LoadoutItem.ReadOnly Item,
    bool IsEnabled,
    bool IsLocked,
    bool IsInCollection,
    bool IsParentCollectionDisabled,
    EntityId ParentId)
{
    /// <summary>Identity for "did anything shown in the list change?".</summary>
    public bool SameStateAs(SnapshotItem other) =>
        Item.Id.Equals(other.Item.Id)
        && IsEnabled == other.IsEnabled
        && IsLocked == other.IsLocked
        && IsInCollection == other.IsInCollection
        && IsParentCollectionDisabled == other.IsParentCollectionDisabled
        && ParentId.Equals(other.ParentId);
}

/// <summary>
/// Afterburn: the installed mods of a loadout (optionally one collection), grouped by Nexus Mods mod page,
/// read from the database in one pass.
/// </summary>
public sealed class LoadoutSnapshot
{
    private static readonly IReadOnlyDictionary<EntityId, SnapshotItem[]> None = new Dictionary<EntityId, SnapshotItem[]>();

    public static readonly LoadoutSnapshot Empty = new(None, None, None);

    public LoadoutSnapshot(
        IReadOnlyDictionary<EntityId, SnapshotItem[]> byNexusModPage,
        IReadOnlyDictionary<EntityId, SnapshotItem[]> byLocalFile,
        IReadOnlyDictionary<EntityId, SnapshotItem[]> byManualArchive)
    {
        ByNexusModPage = byNexusModPage;
        ByLocalFile = byLocalFile;
        ByManualArchive = byManualArchive;
    }

    /// <summary>Installed Nexus Mods files, grouped by their mod page.</summary>
    public IReadOnlyDictionary<EntityId, SnapshotItem[]> ByNexusModPage { get; }

    /// <summary>Installed files added from disk, grouped by their library item.</summary>
    public IReadOnlyDictionary<EntityId, SnapshotItem[]> ByLocalFile { get; }

    /// <summary>Installed manually created archives, grouped by their library item.</summary>
    public IReadOnlyDictionary<EntityId, SnapshotItem[]> ByManualArchive { get; }

    public SnapshotItem[] ItemsFor(EntityId modPageId) =>
        ByNexusModPage.TryGetValue(modPageId, out var items) ? items : [];

    public SnapshotItem[] LocalItemsFor(EntityId libraryItemId) =>
        ByLocalFile.TryGetValue(libraryItemId, out var items) ? items : [];

    public SnapshotItem[] ManualItemsFor(EntityId libraryItemId) =>
        ByManualArchive.TryGetValue(libraryItemId, out var items) ? items : [];
}

/// <summary>
/// Afterburn: builds <see cref="LoadoutSnapshot"/>s and shares one live snapshot stream per loadout filter.
/// <para>
/// The original lists gave every row its own set of database watchers (thousands for a big collection). Every
/// database change then made all of them re-check, which took seconds and froze the app while "Recording changes".
/// Here a handful of whole-attribute watchers trigger one re-read of the loadout (at most a few times a second), and
/// rows only receive the changes that actually concern them.
/// </para>
/// </summary>
public static class LoadoutSnapshots
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(150);
    private static readonly ConcurrentDictionary<string, IObservable<LoadoutSnapshot>> Shared = new();

    private static string Key(LoadoutFilter filter) =>
        filter.CollectionGroupId.HasValue
            ? $"{filter.LoadoutId.Value}/{filter.CollectionGroupId.Value.Value}"
            : $"{filter.LoadoutId.Value}";

    private static readonly ConcurrentDictionary<string, IObservable<BaseEntry[]>> SharedBase = new();

    private enum Kind : byte { NexusModPage, LocalFile, ManualArchive }

    /// <summary>One installed mod of a loadout with everything the per-page filters need.</summary>
    private readonly record struct BaseEntry(SnapshotItem Item, Kind Kind, EntityId Key, EntityId[] Ancestors);

    /// <summary>Live snapshots for the filter. All filters of one loadout share a single database read.</summary>
    public static IObservable<LoadoutSnapshot> Observe(IConnection connection, LoadoutFilter filter)
    {
        return Shared.GetOrAdd(Key(filter), _ =>
        {
            // Afterburn: every list (All, My Mods, each collection) and every count in the left menu used to read the
            // whole loadout on its own after each database change. Now one read per loadout is shared and each page
            // only filters it, which is cheap.
            var collectionGroupId = filter.CollectionGroupId.HasValue
                ? filter.CollectionGroupId.Value.Value
                : Optional<EntityId>.None;

            return ObserveBase(connection, filter.LoadoutId)
                .Select(entries => FromEntries(entries, collectionGroupId))
                .Replay(1)
                .RefCount();
        });
    }

    private static IObservable<BaseEntry[]> ObserveBase(IConnection connection, LoadoutId loadoutId)
    {
        return SharedBase.GetOrAdd($"{loadoutId.Value}", _ =>
        {
            // Anything that can change which mods are installed, which mod page they belong to, or whether they (or
            // their collection) are enabled or required.
            var changes = AttributeChangeSignal.Observe(connection,
                LibraryLinkedLoadoutItem.LibraryItem,
                LoadoutItem.Disabled,
                NexusModsLibraryItem.ModPageMetadata,
                NexusCollectionItemLoadoutGroup.IsRequired
            );

            return changes
                .StartWith(0)
                .Throttle(Coalesce, TaskPoolScheduler.Default)
                .Select(_ => BuildBase(connection.Db, loadoutId))
                .Replay(1)
                .RefCount();
        });
    }

    /// <summary>Reads the snapshot from a database revision.</summary>
    public static LoadoutSnapshot Build(IDb db, LoadoutFilter filter)
    {
        var collectionGroupId = filter.CollectionGroupId.HasValue
            ? filter.CollectionGroupId.Value.Value
            : Optional<EntityId>.None;
        return FromEntries(BuildBase(db, filter.LoadoutId), collectionGroupId);
    }

    private static BaseEntry[] BuildBase(IDb db, LoadoutId loadoutId)
    {
        var entries = new List<BaseEntry>();
        var ancestors = new List<EntityId>();

        foreach (var linked in LibraryLinkedLoadoutItem.All(db))
        {
            var item = LoadoutItem.Load(db, linked.Id);
            if (!item.IsValid()) continue;
            if (!item.LoadoutId.Equals(loadoutId)) continue;

            var isInCollection = false;
            var isParentCollectionDisabled = false;
            if (item.HasParent() && item.Parent.TryGetAsCollectionGroup(out var group))
            {
                isInCollection = true;
                isParentCollectionDisabled = group.AsLoadoutItemGroup().AsLoadoutItem().IsDisabled;
            }

            var snapshotItem = new SnapshotItem(
                Item: item,
                IsEnabled: item.IsEnabled(),
                IsLocked: LoadoutDataProviderHelper.IsLocked(item),
                IsInCollection: isInCollection,
                IsParentCollectionDisabled: isParentCollectionDisabled,
                ParentId: item.HasParent() ? item.ParentId.Value : default
            );

            Kind kind;
            EntityId key;
            if (linked.LibraryItem.TryGetAsNexusModsLibraryItem(out var nexusItem))
            {
                kind = Kind.NexusModPage;
                key = nexusItem.ModPageMetadata.Id;
            }
            else if (LocalFile.Load(db, linked.LibraryItem.Id).IsValid())
            {
                kind = Kind.LocalFile;
                key = linked.LibraryItem.Id;
            }
            else if (ManuallyCreatedArchive.Load(db, linked.LibraryItem.Id).IsValid())
            {
                kind = Kind.ManualArchive;
                key = linked.LibraryItem.Id;
            }
            else
            {
                continue;
            }

            // The same walk as LoadoutItemExtensions.IsChildOf, done once so filters don't repeat it.
            ancestors.Clear();
            var current = item;
            while (current.Contains(LoadoutItem.Parent))
            {
                var parent = current.Parent;
                ancestors.Add(parent.Id);
                current = parent.AsLoadoutItem();
            }

            entries.Add(new BaseEntry(snapshotItem, kind, key, ancestors.ToArray()));
        }

        return entries.ToArray();
    }

    private static LoadoutSnapshot FromEntries(BaseEntry[] entries, Optional<EntityId> collectionGroupId)
    {
        var byModPage = new Dictionary<EntityId, List<SnapshotItem>>();
        var byLocal = new Dictionary<EntityId, List<SnapshotItem>>();
        var byManual = new Dictionary<EntityId, List<SnapshotItem>>();

        foreach (var entry in entries)
        {
            if (collectionGroupId.HasValue && Array.IndexOf(entry.Ancestors, collectionGroupId.Value) < 0) continue;

            var target = entry.Kind switch
            {
                Kind.NexusModPage => byModPage,
                Kind.LocalFile => byLocal,
                _ => byManual,
            };

            if (!target.TryGetValue(entry.Key, out var list)) target[entry.Key] = list = [];
            list.Add(entry.Item);
        }

        return new LoadoutSnapshot(Freeze(byModPage), Freeze(byLocal), Freeze(byManual));
    }

    private static IReadOnlyDictionary<EntityId, SnapshotItem[]> Freeze(Dictionary<EntityId, List<SnapshotItem>> source) =>
        source.ToDictionary(static kv => kv.Key, static kv => kv.Value.ToArray());

    /// <summary>
    /// The keys (rows) of one group of the snapshot as a change set, plus a per-row stream of that row's items.
    /// </summary>
    public static IObservable<IChangeSet<EntityId, EntityId>> RowKeys(
        this IObservable<LoadoutSnapshot> snapshots,
        Func<LoadoutSnapshot, IReadOnlyDictionary<EntityId, SnapshotItem[]>> group)
    {
        return snapshots
            .Select(snapshot => (IEnumerable<EntityId>)group(snapshot).Keys.ToArray())
            .ToDiffChangeSet(static id => id, static (a, b) => a.Equals(b));
    }

    /// <summary>The items of one row, re-emitted on every snapshot (consumers de-duplicate).</summary>
    public static IObservable<SnapshotItem[]> RowItems(
        this IObservable<LoadoutSnapshot> snapshots,
        Func<LoadoutSnapshot, SnapshotItem[]> selector)
    {
        return snapshots.Select(selector).Replay(1).RefCount();
    }

    /// <summary>A row's items as a change set of loadout items that only reports real changes.</summary>
    public static IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> LinkedItems(this IObservable<SnapshotItem[]> rowItems)
    {
        return rowItems
            .Select(static items => (IEnumerable<SnapshotItem>)items)
            .ToDiffChangeSet(static x => x.Item.Id, static (a, b) => a.SameStateAs(b))
            .Transform(static x => x.Item)
            .RefCount();
    }

    /// <summary>
    /// Turns a stream of "current set" values into a DynamicData change set that only reports what was added,
    /// removed, or changed (according to <paramref name="sameState"/>) since the previous value.
    /// </summary>
    public static IObservable<IChangeSet<TValue, TKey>> ToDiffChangeSet<TValue, TKey>(
        this IObservable<IEnumerable<TValue>> source,
        Func<TValue, TKey> keySelector,
        Func<TValue, TValue, bool> sameState)
        where TKey : notnull
        where TValue : notnull
    {
        return Observable.Create<IChangeSet<TValue, TKey>>(observer =>
        {
            var cache = new SourceCache<TValue, TKey>(keySelector);
            var output = cache.Connect().Subscribe(observer);
            var input = source.Subscribe(
                values => cache.EditDiff(values, (a, b) => sameState(a, b)),
                observer.OnError);
            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                input.Dispose();
                output.Dispose();
                cache.Dispose();
            });
        });
    }
}
