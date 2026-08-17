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

    public string CraftShortfall(uint itemId, int count)
    {
        if (RecipeFor(itemId) is not { } recipe)
            return string.Empty;
        var parts = new List<string>();
        foreach (var need in _ingredients.Plan(recipe.RecipeId, count, _held))
            if (need.Missing > 0)
                parts.Add($"{need.Missing} × {need.Name}");
        return string.Join(", ", parts);
    }

    public bool GathererReady => _gatherer.Available;

    public bool IsGathering => _gatherer.IsRunning;

    public bool GathererIdle => _gatherer.IsWaiting;

    public string GathererStatus => _gatherer.Status;

    public bool StartGathering() => _gatherer.Start();

    public void StopGathering() => _gatherer.Stop();
}
