using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>One ingredient a craft needs, and where it can be had.</summary>
/// <param name="Needed">How many the whole run requires.</param>
/// <param name="Held">How many are already in the bags.</param>
/// <param name="VendorDataId">The ENpc that sells it, 0 when nobody nearby does.</param>
public sealed record IngredientNeed(
    uint ItemId, string Name, int PerCraft, int Needed, int Held,
    uint ShopId, uint VendorDataId, string VendorName, uint GilCost)
{
    public int Missing => Math.Max(0, Needed - Held);
    /// <summary>A vendor sells it, so the shortfall can be bought rather than reported.</summary>
    public bool CanBuy => Missing > 0 && ShopId != 0 && VendorDataId != 0;
    public uint GilForMissing => (uint)Missing * GilCost;
}

/// <summary>Works out what a craft needs and who sells it.</summary>
public interface IIngredientSource
{
    /// <summary>
    /// What <paramref name="crafts"/> of a recipe needs. <paramref name="held"/> answers how many
    /// of an item are already in the bags.
    /// </summary>
    IReadOnlyList<IngredientNeed> Plan(ushort recipeId, int crafts, Func<uint, int> held);

    /// <summary>
    /// Everyone who sells an item, without asking about any recipe. Empty when nobody does. Which
    /// of them is within reach is the caller's question — this only says who they are.
    /// </summary>
    IReadOnlyList<(uint ShopId, uint VendorDataId, string VendorName, uint Cost)> VendorsFor(uint itemId);
}

/// <summary>
/// Ingredients from the <c>Recipe</c> sheet, vendors from <c>GilShopItem</c> and <c>ENpcBase</c>.
///
/// <para>
/// Every delivery client has a merchant standing near them selling exactly what their craft needs —
/// that is the intended way to do deliveries, and it is why vendor-only sourcing is enough. The
/// vendor is found by asking which shops sell the ingredient and which NPCs run those shops, then
/// letting the runner check which of those is actually spawned nearby. vsatisfy reads NPC positions
/// out of the zone's <c>planevent.lgb</c> up front; looking at what is loaded around you needs no
/// map files and cannot go stale.
/// </para>
///
/// <para>
/// Crystals are skipped. No vendor sells them and they are not what stops a delivery.
/// </para>
/// </summary>
public sealed class IngredientSource : IIngredientSource
{
    /// <summary>Shards, crystals and clusters — item ids 2 through 19.</summary>
    private static bool IsCrystal(uint itemId) => itemId is >= 2 and <= 19;

    private readonly IDataManager _data;
    private readonly Action<string>? _log;
    private readonly Dictionary<uint, List<(uint ShopId, uint VendorDataId, string VendorName, uint Cost)>> _vendorCache = new();
    private Dictionary<uint, List<uint>>? _shopsByNpc;

    public IngredientSource(IDataManager data, Action<string>? log = null)
    {
        _data = data;
        _log = log;
    }

    public IReadOnlyList<IngredientNeed> Plan(ushort recipeId, int crafts, Func<uint, int> held)
    {
        var needs = new List<IngredientNeed>();
        try
        {
            var recipe = _data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId);
            if (recipe is not { } r) return needs;

            for (var i = 0; i < r.Ingredient.Count; i++)
            {
                var itemId = r.Ingredient[i].RowId;
                var per = r.AmountIngredient[i];
                if (itemId == 0 || per == 0 || IsCrystal(itemId)) continue;

                var vendor = FindVendor(itemId);
                needs.Add(new IngredientNeed(
                    itemId,
                    r.Ingredient[i].ValueNullable?.Name.ExtractText() ?? $"item {itemId}",
                    per, per * crafts, held(itemId),
                    vendor.ShopId, vendor.VendorDataId, vendor.VendorName, vendor.Cost));
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Ingredient plan for recipe {recipeId} failed: {ex.GetType().Name}: {ex.Message}");
        }
        return needs;
    }

    /// <summary>
    /// Every NPC who sells an item, in sheet order.
    ///
    /// <para>
    /// All of them, not the first: seven NPCs sell Copper Ore and the one standing in the
    /// Goldsmiths' Guild is sixth. Taking the first and giving up when they are not here declined a
    /// sale from a merchant three paces away, which is the whole point of asking.
    /// </para>
    /// </summary>
    public IReadOnlyList<(uint ShopId, uint VendorDataId, string VendorName, uint Cost)> VendorsFor(uint itemId)
    {
        if (_vendorCache.TryGetValue(itemId, out var cached)) return cached;

        var found = new List<(uint, uint, string, uint)>();
        try
        {
            // Shops that stock it, with the price.
            var shops = new Dictionary<uint, uint>();
            foreach (var shop in _data.GetSubrowExcelSheet<GilShopItem>())
                foreach (var entry in shop)
                    if (entry.Item.RowId == itemId)
                    {
                        shops.TryAdd(shop.RowId, entry.Item.ValueNullable?.PriceMid ?? 0);
                        break;
                    }

            if (shops.Count > 0)
            {
                foreach (var (npcId, npcShops) in ShopsByNpc())
                {
                    var match = npcShops.FirstOrDefault(shops.ContainsKey);
                    if (match == 0) continue;
                    var name = _data.GetExcelSheet<ENpcResident>().GetRowOrDefault(npcId)?.Singular.ExtractText() ?? string.Empty;
                    found.Add((match, npcId, Capitalise(name), shops[match]));
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Vendor lookup for item {itemId} failed: {ex.Message}");
        }

        return _vendorCache[itemId] = found;
    }

    /// <summary>The first NPC who sells it, for callers that only want somewhere to point at.</summary>
    private (uint ShopId, uint VendorDataId, string VendorName, uint Cost) FindVendor(uint itemId)
        => VendorsFor(itemId) is { Count: > 0 } all ? all[0] : (0u, 0u, string.Empty, 0u);

    /// <summary>
    /// NPC → the shops it runs, built once. <c>ENpcData</c> holds event handler ids; the high half
    /// is the handler kind, and 4 is a shop.
    /// </summary>
    private Dictionary<uint, List<uint>> ShopsByNpc()
    {
        if (_shopsByNpc is not null) return _shopsByNpc;

        _shopsByNpc = [];
        const uint shopHandler = 4;
        foreach (var npc in _data.GetExcelSheet<ENpcBase>())
        {
            List<uint>? shops = null;
            foreach (var handler in npc.ENpcData)
            {
                if (handler.RowId >> 16 != shopHandler) continue;
                (shops ??= []).Add(handler.RowId);
            }
            if (shops is not null) _shopsByNpc[npc.RowId] = shops;
        }
        return _shopsByNpc;
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
