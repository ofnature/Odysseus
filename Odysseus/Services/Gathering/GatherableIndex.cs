using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Gathering;

/// <summary>
/// Every item a gatherer can take from a node, by name — the search behind "add an item" on a
/// gather list. Built once from <c>GatheringItem</c>; fish are the fishing side's business.
/// </summary>
public sealed class GatherableIndex
{
    private readonly List<(uint Id, string Name)> _items = new();
    private readonly Dictionary<uint, string> _names = new();

    /// <summary>Build from explicit rows — what the tests use.</summary>
    public GatherableIndex(IEnumerable<(uint Id, string Name)> rows)
    {
        foreach (var (id, name) in rows)
        {
            if (id == 0 || name.Length == 0 || !_names.TryAdd(id, name))
                continue;
            _items.Add((id, name));
        }
        _items.Sort(Order);
    }

    public GatherableIndex(IDataManager data, Action<string>? log = null)
    {
        try
        {
            var items = data.GetExcelSheet<Item>();
            foreach (var g in data.GetExcelSheet<GatheringItem>())
            {
                var id = g.Item.RowId;
                if (id == 0 || _names.ContainsKey(id))
                    continue;
                var name = items.GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty;
                if (name.Length == 0)
                    continue;
                _names[id] = name;
                _items.Add((id, name));
            }
            _items.Sort(Order);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Gatherable index failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public int Count => _items.Count;

    public string NameOf(uint itemId) => _names.TryGetValue(itemId, out var name) ? name : $"item {itemId}";

    /// <summary>
    /// Names containing the filter, plainest first, at most <paramref name="max"/>.
    ///
    /// <para>
    /// <paramref name="exclude"/> is dropped <b>before</b> the cut, not after. Filtering afterwards
    /// let items already on a list eat the visible slots: a "shard" search with four shards already
    /// listed spent four of its eight on them and never reached Water Shard or Wind Shard, which
    /// sort last — so the only two shards missing from the list were also the only two that could
    /// not be found to add.
    /// </para>
    ///
    /// <para>
    /// Order is exact match, then names starting with the filter, then the shortest — a plain
    /// "Water Shard" is what someone typing "shard" wants, not "Grade 2 Skybuilders' Umbral
    /// Levinshard", and among gathering items the shorter name is reliably the plainer thing.
    /// </para>
    /// </summary>
    public IReadOnlyList<(uint Id, string Name)> Search(string filter, int max, IReadOnlyCollection<uint>? exclude = null)
    {
        var found = new List<(uint Id, string Name)>();
        foreach (var item in _items)
        {
            if (!item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (exclude is not null && exclude.Contains(item.Id))
                continue;
            found.Add(item);
        }
        found.Sort((a, b) => Rank(a.Name, filter).CompareTo(Rank(b.Name, filter)) is var byRank && byRank != 0
            ? byRank
            : Order(a, b));
        return found.Count <= max ? found : found.GetRange(0, max);
    }

    private static int Rank(string name, string filter)
        => name.Equals(filter, StringComparison.OrdinalIgnoreCase) ? 0
            : name.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ? 1
            : 2;

    /// <summary>Shortest first, then alphabetical — a stable order for names of equal rank.</summary>
    private static int Order((uint Id, string Name) a, (uint Id, string Name) b)
        => a.Name.Length != b.Name.Length
            ? a.Name.Length.CompareTo(b.Name.Length)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
}
