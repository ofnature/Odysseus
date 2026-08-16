using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>
/// Item id → recipe id, built once from the <c>Recipe</c> sheet.
///
/// <para>
/// An item can have more than one recipe (different crafting jobs make the same thing). The first
/// match wins; Artisan picks the job it can actually run, so choosing here would only get in its
/// way.
/// </para>
/// </summary>
public sealed class RecipeLookup : IRecipeLookup
{
    private readonly Dictionary<uint, ushort> _byItem = new();

    public RecipeLookup(IDataManager data, Action<string>? log = null)
    {
        try
        {
            foreach (var recipe in data.GetExcelSheet<Recipe>())
            {
                var item = recipe.ItemResult.RowId;
                if (item == 0 || recipe.RowId > ushort.MaxValue) continue;
                _byItem.TryAdd(item, (ushort)recipe.RowId);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Recipe lookup failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ushort? ForItem(uint itemId) => _byItem.TryGetValue(itemId, out var id) ? id : null;
}
