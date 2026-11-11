using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.DataModel;

/// <summary>
/// Afterburn: automatic file-conflict rules.
/// <para>
/// When two mods ship the same file, the one with the higher priority wins. By default the newest install wins, and a
/// collection's own load-order rules adjust that. This adds a few rules on top, so the right mod wins without having
/// to sort it out by hand:
/// <list type="number">
/// <item>Mods from the same collection keep the order the collection author set.</item>
/// <item>A mod that names the other mod (for example "Foo - Patch for Bar") wins over it.</item>
/// <item>Patches, fixes, compatibility files, add-ons and overrides win over regular mods.</item>
/// <item>A mod you installed yourself wins over a mod that came from a collection.</item>
/// </list>
/// Everything else keeps its current order. Rules that would contradict each other are skipped, so the result is
/// always a valid order, and nothing changes when everything already agrees.
/// </para>
/// </summary>
internal partial class LoadoutManager
{
    public async ValueTask AutoResolveFileConflicts(LoadoutId loadoutId)
    {
        try
        {
            using var tx = _connection.BeginTransaction();
            tx.Add(new AutoResolveFileConflictsTxFunc(loadoutId));
            await tx.Commit();
        }
        catch (Exception e)
        {
            // Never let automatic rules break an install.
            _logger.LogWarning(e, "Automatic file-conflict rules couldn't be applied");
        }
    }

    private sealed class AutoResolveFileConflictsTxFunc : ITxFunction
    {
        private static readonly HashSet<string> PatchWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "patch", "patches", "patched", "fix", "fixes", "fixed", "hotfix", "bugfix",
            "compat", "compatibility", "addon", "addons", "override", "overrides",
        };

        private readonly LoadoutId _loadoutId;

        public AutoResolveFileConflictsTxFunc(LoadoutId loadoutId) => _loadoutId = loadoutId;

        public bool Equals(ITxFunction? obj) => obj is AutoResolveFileConflictsTxFunc other && other._loadoutId == _loadoutId;
        public override int GetHashCode() => _loadoutId.GetHashCode();

        private readonly record struct ModInfo(string Name, string[] Words, LoadoutItemGroupId Parent, bool HasParent, bool IsFromCollection, bool IsPatch);

        public void Apply(ITransaction tx, IDb basis)
        {
            var priorities = LoadoutItemGroupPriority
                .FindByLoadout(basis, _loadoutId)
                .OrderBy(static model => model.Priority)
                .ToList();
            if (priorities.Count < 2) return;

            var indexOf = new Dictionary<LoadoutItemGroupId, int>(priorities.Count);
            for (var i = 0; i < priorities.Count; i++) indexOf[priorities[i].TargetId] = i;

            // Which mods own each file path.
            var owners = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in LoadoutItem.FindByLoadout(basis, _loadoutId).OfTypeLoadoutItemWithTargetPath())
            {
                var item = file.AsLoadoutItem();
                if (!item.IsEnabled() || !item.HasParent()) continue;
                if (!indexOf.TryGetValue(item.ParentId, out var index)) continue;

                var path = file.TargetPath.Item3.ToString().Replace('\\', '/');
                if (!owners.TryGetValue(path, out var list)) owners[path] = list = new List<int>(2);
                if (!list.Contains(index)) list.Add(index);
            }

            var pairs = new HashSet<(int, int)>();
            foreach (var list in owners.Values)
            {
                if (list.Count < 2) continue;
                for (var a = 0; a < list.Count; a++)
                for (var b = a + 1; b < list.Count; b++)
                {
                    var (x, y) = list[a] < list[b] ? (list[a], list[b]) : (list[b], list[a]);
                    pairs.Add((x, y));
                }
            }
            if (pairs.Count == 0) return;

            var infos = new Dictionary<int, ModInfo>();
            ModInfo Info(int index)
            {
                if (infos.TryGetValue(index, out var info)) return info;
                var group = priorities[index].Target.AsLoadoutItem();
                var name = group.Name;
                var words = SplitWords(name);
                var hasParent = group.HasParent();
                var isFromCollection = false;
                if (hasParent)
                {
                    var parent = CollectionGroup.Load(basis, group.ParentId);
                    isFromCollection = parent.IsValid() && parent.IsReadOnly;
                }

                info = new ModInfo(name, words, hasParent ? group.ParentId : default, hasParent, isFromCollection, words.Any(PatchWords.Contains));
                infos[index] = info;
                return info;
            }

            // "winner must come after loser" (a higher priority wins).
            var after = new Dictionary<int, List<int>>();
            bool Reaches(int from, int to)
            {
                if (from == to) return true;
                var seen = new HashSet<int> { from };
                var stack = new Stack<int>();
                stack.Push(from);
                while (stack.Count > 0)
                {
                    if (!after.TryGetValue(stack.Pop(), out var next)) continue;
                    foreach (var n in next)
                    {
                        if (n == to) return true;
                        if (seen.Add(n)) stack.Push(n);
                    }
                }

                return false;
            }

            var changed = false;
            foreach (var (x, y) in pairs.OrderBy(static p => p.Item1).ThenBy(static p => p.Item2))
            {
                var winner = Decide(x, Info(x), y, Info(y));
                if (winner < 0) continue;
                var loser = winner == x ? y : x;

                // Skip a rule that contradicts the ones already kept.
                if (Reaches(winner, loser)) continue;
                if (!after.TryGetValue(loser, out var list)) after[loser] = list = new List<int>();
                list.Add(winner);
                if (loser > winner) changed = true;
            }

            if (!changed) return;

            // Stable re-order: keep the current order wherever no rule says otherwise.
            var incoming = new int[priorities.Count];
            foreach (var targets in after.Values)
                foreach (var t in targets) incoming[t]++;

            var ready = new PriorityQueue<int, int>();
            for (var i = 0; i < priorities.Count; i++)
                if (incoming[i] == 0) ready.Enqueue(i, i);

            var order = new List<int>(priorities.Count);
            while (ready.TryDequeue(out var current, out _))
            {
                order.Add(current);
                if (!after.TryGetValue(current, out var next)) continue;
                foreach (var n in next)
                    if (--incoming[n] == 0) ready.Enqueue(n, n);
            }

            if (order.Count != priorities.Count) return; // should not happen (rules are acyclic); leave things as they are

            var priority = 0UL;
            foreach (var index in order)
            {
                tx.Add(priorities[index].Id, LoadoutItemGroupPriority.Priority, ConflictPriority.From(++priority));
            }
        }

        /// <summary>Returns the index of the mod that should win, or -1 to keep the current order.</summary>
        private static int Decide(int x, ModInfo a, int y, ModInfo b)
        {
            // Same collection: the collection author's order stands.
            if (a.HasParent && b.HasParent && a.Parent.Equals(b.Parent) && a.IsFromCollection) return -1;

            var aNamesB = Mentions(a, b);
            var bNamesA = Mentions(b, a);
            if (aNamesB != bNamesA) return aNamesB ? x : y;

            if (a.IsPatch != b.IsPatch) return a.IsPatch ? x : y;

            if (a.IsFromCollection != b.IsFromCollection) return a.IsFromCollection ? y : x;

            return -1;
        }

        /// <summary>True when <paramref name="a"/>'s name contains <paramref name="b"/>'s full name (as whole words).</summary>
        private static bool Mentions(ModInfo a, ModInfo b)
        {
            if (b.Words.Length == 0 || a.Words.Length <= b.Words.Length) return false;
            if (string.Join(' ', b.Words).Length < 4) return false;
            for (var start = 0; start + b.Words.Length <= a.Words.Length; start++)
            {
                var match = true;
                for (var k = 0; k < b.Words.Length; k++)
                {
                    if (!string.Equals(a.Words[start + k], b.Words[k], StringComparison.OrdinalIgnoreCase)) { match = false; break; }
                }

                if (match) return true;
            }

            return false;
        }

        private static string[] SplitWords(string name)
        {
            return name.Split(new[] { ' ', '-', '_', '.', '(', ')', '[', ']', '/', '+', ',', ':', '&' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
