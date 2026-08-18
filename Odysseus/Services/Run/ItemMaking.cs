using System;
using System.Collections.Generic;
using Odysseus.Services.Deliveries;

namespace Odysseus.Services.Run;

/// <summary>
/// The craft and gather handoffs, put in the terms a quest step asks in: an item and a count.
///
/// <para>
/// Artisan works in recipes and GatherBuddy works in its own lists, while a step only ever names
/// an item. Everything that bridges the two lives here, so <see cref="GameStepWorld"/> stays a
/// translation layer and the executor keeps holding one seam. The pieces are the ones the delivery
/// runner already proved in the field — <see cref="IRecipeLookup"/>, <see cref="RecipePicker"/>
/// and <see cref="IIngredientSource"/> — reused rather than reimplemented.
/// </para>
/// </summary>
public sealed class ItemMaking
{
    private readonly ICrafter _crafter;
    private readonly IGatherer _gatherer;
    private readonly IRecipeLookup _recipes;
    private readonly IIngredientSource _ingredients;
    private readonly Func<int> _preferredCraftType;
    private readonly Func<int> _currentCraftType;
    private readonly Func<uint, int> _held;

    /// <param name="preferredCraftType">The configured crafting job as a CraftType index, or -1 for none.</param>
    /// <param name="currentCraftType">The crafting job the character is on, or -1.</param>
    /// <param name="held">How many of an item are in the bags.</param>
    public ItemMaking(
        ICrafter crafter, IGatherer gatherer, IRecipeLookup recipes, IIngredientSource ingredients,
        Func<int> preferredCraftType, Func<int> currentCraftType, Func<uint, int> held)
    {
        _crafter = crafter;
        _gatherer = gatherer;
        _recipes = recipes;
        _ingredients = ingredients;
        _preferredCraftType = preferredCraftType;
        _currentCraftType = currentCraftType;
        _held = held;
    }

    public bool CrafterReady => _crafter.Available;

    public bool IsCrafting => _crafter.IsCrafting;

    /// <summary>
    /// Which job makes an item. The choice matters because Artisan switches the character onto
    /// whatever the recipe belongs to — the configured job first, then the one already equipped,
    /// so standing there as a Culinarian and cooking something does not yank you onto Carpenter.
    /// </summary>
    public RecipeOption? RecipeFor(uint itemId)
        => RecipePicker.Pick(_recipes.OptionsFor(itemId), _preferredCraftType(), _currentCraftType());

    /// <summary>
    /// How deep a recipe tree is followed. Three levels covers ore → ingot → part → product, which
    /// is deeper than anything a class quest asks for, and bounds a cycle in bad sheet data.
    /// </summary>
    private const int MaxCraftDepth = 4;

    /// <summary>
    /// The recipe to actually run right now to get closer to making <paramref name="count"/> of an
    /// item — deepest first.
    ///
    /// <para>
    /// Artisan crafts one recipe: ask it for twelve Copper Rings with no ingots in the bag and it
    /// makes nothing. Its crafting lists do resolve sub-crafts, but the IPC can only start a list
    /// that already exists, so the tree is walked here instead. An ingredient that is itself
    /// craftable and missing is returned before the thing that needs it, and the caller comes back
    /// for the next one once it lands.
    /// </para>
    ///
    /// <para>
    /// Null when the item is already held, or when nothing craftable is missing — which means
    /// what is left has to be bought or gathered, and the caller says so.
    /// </para>
    /// </summary>
    public (uint ItemId, int Count)? NextCraft(uint itemId, int count) => NextCraft(itemId, count, 0);

    private (uint ItemId, int Count)? NextCraft(uint itemId, int count, int depth)
    {
        var missing = count - _held(itemId);
        if (missing <= 0 || depth >= MaxCraftDepth)
            return null;
        if (RecipeFor(itemId) is not { } recipe)
            return null; // not something we can make at all

        foreach (var need in _ingredients.Plan(recipe.RecipeId, missing, _held))
        {
            if (need.Missing <= 0) continue;
            if (NextCraft(need.ItemId, need.Needed, depth + 1) is { } deeper)
                return deeper;
        }
        return (itemId, missing);
    }

    public string? StartCraft(uint itemId, int count)
    {
        if (RecipeFor(itemId) is not { } recipe)
            return null;
        return _crafter.CraftItem(recipe.RecipeId, count) ? recipe.JobName : null;
    }

    public void StopCrafting() => _crafter.StopCrafting();

    /// <summary>
    /// What making <paramref name="count"/> of an item consumes, whether or not it is in the bags —
    /// the bill of materials wants the whole requirement, not just the shortfall.
    /// </summary>
    public IReadOnlyList<(uint ItemId, string Name, int Needed)> Ingredients(uint itemId, int count)
    {
        if (RecipeFor(itemId) is not { } recipe)
            return [];
        var list = new List<(uint, string, int)>();
        foreach (var need in _ingredients.Plan(recipe.RecipeId, count, _held))
            list.Add((need.ItemId, need.Name, need.Needed));
        return list;
    }

    /// <summary>
    /// What is actually missing, followed to the bottom of the recipe tree.
    ///
    /// <para>
    /// The direct ingredient is rarely the useful answer. Twelve Copper Rings that stopped one
    /// ingot short are not short of an ingot — the ingot is craftable, and the loop would have made
    /// it — they are short of the <i>ore</i> the ingot needs. Reporting the immediate ingredient
    /// sends you to buy the one thing you did not need.
    /// </para>
    ///
    /// <para>
    /// A missing ingredient that is itself craftable is therefore recursed into, and only what
    /// cannot be made is named. Totals are aggregated, because one base material commonly feeds
    /// several branches.
    /// </para>
    /// </summary>
    public IReadOnlyList<MaterialShortfall> CraftShortfall(uint itemId, int count)
    {
        var totals = new Dictionary<uint, (string Name, int Missing)>();
        Collect(itemId, count, 0, totals);

        var list = new List<MaterialShortfall>(totals.Count);
        foreach (var (id, entry) in totals)
            list.Add(new MaterialShortfall(id, entry.Name, entry.Missing));
        return list;
    }

    /// <summary>Everyone who sells an item, in sheet order. The caller picks one it can reach.</summary>
    public IReadOnlyList<(uint VendorDataId, uint ShopId, string VendorName, uint Cost)> VendorsFor(uint itemId)
    {
        var list = new List<(uint, uint, string, uint)>();
        foreach (var v in _ingredients.VendorsFor(itemId))
            if (v.VendorDataId != 0 && v.ShopId != 0)
                list.Add((v.VendorDataId, v.ShopId, v.VendorName, v.Cost));
        return list;
    }

    private void Collect(uint itemId, int count, int depth, Dictionary<uint, (string Name, int Missing)> into)
    {
        if (count <= 0 || RecipeFor(itemId) is not { } recipe)
            return;

        foreach (var need in _ingredients.Plan(recipe.RecipeId, count, _held))
        {
            if (need.Missing <= 0)
                continue;

            // Craftable and missing: what is really short is whatever making it needs. Recursing
            // and finding nothing is not a reason to name it — it means its own materials are all
            // there, so the loop can simply make it.
            if (depth < MaxCraftDepth && RecipeFor(need.ItemId) is not null)
            {
                Collect(need.ItemId, need.Missing, depth + 1, into);
                continue;
            }

            into.TryGetValue(need.ItemId, out var have);
            into[need.ItemId] = (need.Name, have.Missing + need.Missing);
        }
    }

    public bool GathererReady => _gatherer.Available;

    public bool IsGathering => _gatherer.IsRunning;

    public bool GathererIdle => _gatherer.IsWaiting;

    public string GathererStatus => _gatherer.Status;

    public bool StartGathering() => _gatherer.Start();

    public void StopGathering() => _gatherer.Stop();
}
