using System.Reactive.Linq;
using DynamicData;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts;

public class LoadoutQueries2
{
    /// <summary>
    /// Returns all mutable collection groups in a loadout.
    /// </summary>
    public static Query<(EntityId GroupId, string Name)> MutableCollections(IConnection connection, LoadoutId id) =>
        connection.Query<(EntityId, string)>($"SELECT Id, Name FROM mdb_CollectionGroup(Db=>{connection}) WHERE IsReadOnly = false AND Loadout = {id} ORDER BY Id");

    /// <summary>
    /// Returns an IObservable of a loadout with the given id, refreshing it whenever a child entity is updated.
    /// </summary>
    public static IObservable<Loadout.ReadOnly> RevisionsWithChildUpdates(IConnection connection, LoadoutId id)
    {
        // Afterburn: the query behind this scans every datom of every item in the loadout each time the database
        // changes. The Apply button, Health Check and the mod list page each asked for their own copy; share one.
        return SharedRevisions.GetOrAdd((connection, id), static key => CreateRevisionsWithChildUpdates(key.Connection, key.Id)
            .Replay(1)
            .RefCount());
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(IConnection Connection, LoadoutId Id), IObservable<Loadout.ReadOnly>> SharedRevisions = new();

    private static IObservable<Loadout.ReadOnly> CreateRevisionsWithChildUpdates(IConnection connection, LoadoutId id)
    {
        return connection.Query<(EntityId LoadoutId, ulong Max, long Count)>($"""
                SELECT {id}, MAX(d.T), COUNT(d.E) FROM 
                loadouts.TrackedEntitiesForLoadout({connection}, {id}) ents
                LEFT JOIN mdb_Datoms() d ON d.E = ents.Id
                """)
            .Observe(x => x.LoadoutId)
            .QueryWhenChanged(_ => Loadout.Load(connection.Db, id));
    }
}
