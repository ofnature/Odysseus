using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>One way to make an item. <paramref name="CraftType"/> is 0..7 — CRP, BSM, ARM, GSM, LTW, WVR, ALC, CUL.</summary>
public sealed record RecipeOption(ushort RecipeId, int CraftType, ushort Level)
{
    public static readonly string[] JobNames = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];

    public string JobName => CraftType >= 0 && CraftType < JobNames.Length ? JobNames[CraftType] : "?";
}

/// <summary>Finds the recipe that makes an item.</summary>
public interface IRecipeLookup
{
    /// <summary>Every recipe that produces the item — usually one, but some items have several jobs.</summary>
    IReadOnlyList<RecipeOption> OptionsFor(uint itemId);
}

/// <summary>
/// Item id → recipes, built once from the <c>Recipe</c> sheet.
///
/// <para>
/// Which one is chosen matters more than it looks: Artisan switches the character to whatever job
/// the recipe belongs to. Picking blindly meant being yanked onto Carpenter mid-run, so the choice
/// is made deliberately — see <see cref="RecipePicker"/>.
/// </para>
/// </summary>
public sealed class RecipeLookup : IRecipeLookup
{
    private readonly Dictionary<uint, List<RecipeOption>> _byItem = new();

    public RecipeLookup(IDataManager data, Action<string>? log = null)
    {
        try
        {
            foreach (var recipe in data.GetExcelSheet<Recipe>())
            {
                var item = recipe.ItemResult.RowId;
                if (item == 0 || recipe.RowId > ushort.MaxValue) continue;
                if (!_byItem.TryGetValue(item, out var list))
                    _byItem[item] = list = [];
                list.Add(new RecipeOption((ushort)recipe.RowId, (int)recipe.CraftType.RowId,
                    recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0));
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Recipe lookup failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public IReadOnlyList<RecipeOption> OptionsFor(uint itemId)
        => _byItem.TryGetValue(itemId, out var list) ? list : [];
}

/// <summary>
/// Chooses which job crafts a delivery.
///
/// <para>
/// Order: the job configured for deliveries, then the job already equipped, then whatever the item
/// offers. Staying on the current job is the quiet default — if you are standing there as a
/// Culinarian and the item can be cooked, nothing should move you.
/// </para>
/// </summary>
public static class RecipePicker
{
    /// <summary>-1 for <paramref name="preferred"/> means "no preference".</summary>
    public static RecipeOption? Pick(IReadOnlyList<RecipeOption> options, int preferred, int current)
    {
        if (options.Count == 0) return null;
        if (preferred >= 0 && options.FirstOrDefault(o => o.CraftType == preferred) is { } wanted) return wanted;
        if (current >= 0 && options.FirstOrDefault(o => o.CraftType == current) is { } here) return here;
        return options[0];
    }
}
