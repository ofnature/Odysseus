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
            _items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            log?.Invoke($"Gatherable index failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public int Count => _items.Count;

    public string NameOf(uint itemId) => _names.TryGetValue(itemId, out var name) ? name : $"item {itemId}";

    /// <summary>Names containing the filter, alphabetically, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<(uint Id, string Name)> Search(string filter, int max)
        => _items.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(max).ToList();
}
