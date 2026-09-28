using Odysseus.Services.Deliveries;
using Odysseus.Services.Ipc;

namespace Odysseus.Tests;

/// <summary>The Settings "Crafting" choice: Artisan or Hephaestus behind the one crafting seam.</summary>
public class CrafterChoiceTests
{
    private sealed class Crafter(string name) : ICrafter
    {
        public string Name { get; } = name;
        public bool Available { get; set; } = true;
        public bool IsCrafting { get; set; }
        public List<uint> Asked { get; } = [];
        public int Stops { get; private set; }
        public bool CraftItem(uint recipeId, int amount) { Asked.Add(recipeId); IsCrafting = true; return true; }
        public void StopCrafting() { Stops++; IsCrafting = false; }
    }

    private readonly Crafter _artisan = new("Artisan");
    private readonly Crafter _hephaestus = new("Hephaestus");
    private string _setting = CrafterChoice.ArtisanProvider;

    private CrafterChoice Choice() => new(_artisan, _hephaestus, () => _setting);

    [Fact]
    public void The_craft_goes_to_the_crafter_settings_name()
    {
        var choice = Choice();
        _setting = CrafterChoice.HephaestusProvider;

        Assert.True(choice.CraftItem(38_500, 3));
        Assert.Equal([38_500u], _hephaestus.Asked);
        Assert.Empty(_artisan.Asked);
        Assert.Equal("Hephaestus", choice.Name);
    }

    [Fact]
    public void Availability_is_the_chosen_crafter_s()
    {
        var choice = Choice();
        _hephaestus.Available = false;
        Assert.True(choice.Available);

        _setting = CrafterChoice.HephaestusProvider;
        Assert.False(choice.Available);
    }

    /// <summary>
    /// Switching mid-craft must not read the other crafter's "idle" as the craft being over — the
    /// step would count the bag short and stop.
    /// </summary>
    [Fact]
    public void A_craft_under_way_stays_with_the_crafter_that_began_it()
    {
        var choice = Choice();
        choice.CraftItem(100, 1);

        _setting = CrafterChoice.HephaestusProvider;
        Assert.True(choice.IsCrafting);
        Assert.Equal("Artisan", choice.Name);

        choice.StopCrafting();
        Assert.Equal(1, _artisan.Stops);
        Assert.Equal(0, _hephaestus.Stops);
        Assert.Equal("Hephaestus", choice.Name);   // done: the setting is in charge again
    }
}
