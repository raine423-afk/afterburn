using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// Afterburn: a cheap "something changed" signal for a few attributes. Watching a whole attribute with ObserveDatoms makes
/// the database build a snapshot of every value of that attribute inside its single write loop, which held up startup
/// for several seconds. This only looks at the values each new transaction adds or removes.
/// </summary>
public static class AttributeChangeSignal
{
    /// <summary>Emits once for each transaction that adds or removes a value of any of the attributes. No initial value.</summary>
    public static IObservable<int> Observe(IConnection connection, params IAttribute[] attributes)
    {
        var ids = new HashSet<AttributeId>();
        foreach (var attribute in attributes)
        {
            if (connection.AttributeCache.TryGetAttributeId(attribute.Id, out var id)) ids.Add(id);
        }
        return new Signal(connection, ids);
    }

    private sealed class Signal(IConnection connection, HashSet<AttributeId> ids) : IObservable<int>
    {
        public IDisposable Subscribe(IObserver<int> observer) => connection.Revisions.Subscribe(new Filter(observer, ids));
    }

    private sealed class Filter(IObserver<int> observer, HashSet<AttributeId> ids) : IObserver<IDb>
    {
        public void OnNext(IDb db)
        {
            var added = db.RecentlyAdded;
            for (var i = 0; i < added.Count; i++)
            {
                if (!ids.Contains(added[i].A)) continue;
                observer.OnNext(0);
                return;
            }
        }

        public void OnError(Exception error) => observer.OnError(error);

        public void OnCompleted() => observer.OnCompleted();
    }
}
