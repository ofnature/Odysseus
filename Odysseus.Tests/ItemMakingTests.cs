using Odysseus.Services.Deliveries;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>
/// The recipe-tree walking, against quest 613's real tree:
/// 12 Copper Rings ← 12 Copper Ingot ← 36 Copper Ore. Shards are left out because
/// <see cref="IngredientSource"/> skips crystals — nobody is ever short of those.
/// </summary>
public class ItemMakingTests
{
    private const uint Rings = 5086;
    private const uint Ingot = 5062;
    private const uint Ore = 5106;

    /// <summary>Item → recipe id, and recipe id → what one craft consumes.</summary>
    private static readonly Dictionary<uint, ushort> RecipeOf = new() { [Rings] = 666, [Ingot] = 663 };
    private static readonly Dictionary<ushort, (uint ItemId, string Name, int Per)[]> Consumes = new()
    {
        [666] = [(Ingot, "Copper Ingot", 1)],
        [663] = [(Ore, "Copper Ore", 3)],
    };

    private sealed class Recipes : IRecipeLookup
    {
        public IReadOnlyList<RecipeOption> OptionsFor(uint itemId)
            => RecipeOf.TryGetValue(itemId, out var id) ? [new RecipeOption(id, 3, 1)] : [];
    }

    private sealed class Ingredients : IIngredientSource
    {
        public IReadOnlyList<(uint ShopId, uint VendorDataId, string VendorName, uint Cost)> VendorsFor(uint itemId) => [];

        public IReadOnlyList<IngredientNeed> Plan(ushort recipeId, int crafts, Func<uint, int> held)
        {
            var list = new List<IngredientNeed>();
            foreach (var (itemId, name, per) in Consumes.GetValueOrDefault(recipeId, []))
                list.Add(new IngredientNeed(itemId, name, per, per * crafts, held(itemId), 0, 0, "", 0));
            return list;
        }
    }

    private sealed class Crafter : ICrafter
    {
        public bool Available => true;
        public bool IsCrafting => false;
        public bool CraftItem(ushort recipeId, int amount) => true;
        public void StopCrafting() { }
    }

    private sealed class Gatherer : IGatherer
    {
        public bool Available => false;
        public int Version => 0;
        public bool IsRunning => false;
        public bool IsWaiting => false;
        public string Status => "";
        public bool Start() => false;
        public void Stop() { }
    }

    private static string Text(IReadOnlyList<MaterialShortfall> missing)
        => string.Join(", ", missing.Select(m => $"{m.Missing} × {m.Name}"));

    private static ItemMaking Making(Dictionary<uint, int> bag)
        => new(new Crafter(), new Gatherer(), new Recipes(), new Ingredients(),
            () => -1, () => 3, id => bag.GetValueOrDefault(id));

    [Fact]
    public void With_nothing_in_the_bag_the_deepest_missing_thing_is_named()
    {
        var making = Making([]);
        // 12 rings need 12 ingots need 36 ore — and the ore is what you actually have to go and get.
        Assert.Equal("36 × Copper Ore", Text(making.CraftShortfall(Rings, 12)));
    }

    /// <summary>
    /// The case from the live run: eleven ingots made, the twelfth blocked. Reporting the ingot
    /// sends you to buy the one thing the crafter could have made; reporting the ore is the answer.
    /// </summary>
    [Fact]
    public void An_ingot_short_reports_the_ore_beneath_it_not_the_ingot()
    {
        var making = Making(new Dictionary<uint, int> { [Ingot] = 11, [Ore] = 0 });
        Assert.Equal("3 × Copper Ore", Text(making.CraftShortfall(Rings, 12)));
    }

    /// <summary>Ore in hand for the last ingot: nothing is short, because the loop can make it.</summary>
    [Fact]
    public void Enough_ore_for_the_missing_ingot_is_no_shortfall_at_all()
    {
        var making = Making(new Dictionary<uint, int> { [Ingot] = 11, [Ore] = 3 });
        Assert.Equal(string.Empty, Text(making.CraftShortfall(Rings, 12)));
    }

    /// <summary>Something with no recipe under it is named directly — there is nothing to recurse into.</summary>
    [Fact]
    public void A_base_material_is_named_as_itself()
    {
        var making = Making([]);
        Assert.Equal("3 × Copper Ore", Text(making.CraftShortfall(Ingot, 1)));
    }

    /// <summary>
    /// Seven NPCs sell Copper Ore and the Goldsmiths' Guild one is sixth. Returning only the first
    /// meant declining a sale from a merchant standing three paces away, so every candidate is
    /// offered and the caller picks the one it can actually see.
    /// </summary>
    [Fact]
    public void Every_vendor_for_an_item_is_offered_not_just_the_first()
    {
        var making = new ItemMaking(new Crafter(), new Gatherer(), new Recipes(), new ManyVendors(),
            () => -1, () => 3, _ => 0);

        var vendors = making.VendorsFor(Ore);
        Assert.Equal([1000236u, 1004419u], vendors.Select(v => v.VendorDataId));
    }

    /// <summary>Two NPCs sell the ore; the near one is second, as in the real sheet.</summary>
    private sealed class ManyVendors : IIngredientSource
    {
        public IReadOnlyList<IngredientNeed> Plan(ushort recipeId, int crafts, Func<uint, int> held) => [];

        public IReadOnlyList<(uint ShopId, uint VendorDataId, string VendorName, uint Cost)> VendorsFor(uint itemId)
            => itemId == Ore
                ? [(262100u, 1000236u, "Somebody Far Away", 9u), (262176u, 1004419u, "Alaric", 9u)]
                : [];
    }

    // ── What to craft next ──

    [Fact]
    public void The_deepest_craftable_shortfall_comes_first()
    {
        var making = Making(new Dictionary<uint, int> { [Ore] = 36 });
        Assert.Equal((Ingot, 12), making.NextCraft(Rings, 12));
    }

    [Fact]
    public void With_the_sub_component_in_hand_the_target_itself_is_next()
    {
        var making = Making(new Dictionary<uint, int> { [Ingot] = 12 });
        Assert.Equal((Rings, 12), making.NextCraft(Rings, 12));
    }

    [Fact]
    public void Already_held_means_nothing_to_craft()
        => Assert.Null(Making(new Dictionary<uint, int> { [Rings] = 12 }).NextCraft(Rings, 12));

    /// <summary>
    /// Ore cannot be made, so the walk bottoms out at the ingot and lets Artisan be the one to
    /// report it made nothing — which is what the shortfall above then explains.
    /// </summary>
    [Fact]
    public void An_uncraftable_base_material_stops_the_walk()
    {
        var making = Making([]);
        Assert.Equal((Ingot, 12), making.NextCraft(Rings, 12));
    }
}
