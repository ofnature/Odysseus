using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

public class GatherPresetsTests
{
    [Fact]
    public void The_three_families_are_the_six_elements_each_and_the_fourth_is_all_of_them()
    {
        var byName = GatherPresets.All.ToDictionary(p => p.Name, p => p.ItemIds);

        Assert.Equal([2u, 3, 4, 5, 6, 7], byName["Shards"]);
        Assert.Equal([8u, 9, 10, 11, 12, 13], byName["Crystals"]);
        Assert.Equal([14u, 15, 16, 17, 18, 19], byName["Clusters"]);
        Assert.Equal(18, byName["Shards, crystals and clusters"].Count);
        Assert.Equal(Enumerable.Range(2, 18).Select(i => (uint)i), byName["Shards, crystals and clusters"]);
    }

    [Fact]
    public void No_preset_names_the_same_item_twice()
        => Assert.All(GatherPresets.All, p => Assert.Equal(p.ItemIds.Count, p.ItemIds.Distinct().Count()));

    [Fact]
    public void A_built_list_is_an_ordinary_list_named_after_its_preset()
    {
        var preset = GatherPresets.All.Single(p => p.Name == "Crystals");
        var list = GatherPresets.Build(preset);

        Assert.Equal("Crystals", list.Name);
        Assert.True(list.Enabled);
        Assert.Equal(preset.ItemIds, list.Items.Select(i => i.ItemId));
        // The same target an item added by hand gets — a starting point, not a decision.
        Assert.All(list.Items, i => Assert.Equal(new GatherListItem().TargetCount, i.TargetCount));
    }

    [Fact]
    public void Building_twice_makes_two_lists_that_share_nothing()
    {
        var preset = GatherPresets.All[0];
        var first = GatherPresets.Build(preset);
        var second = GatherPresets.Build(preset);

        first.Items[0].TargetCount = 4242;
        first.Name = "renamed";

        Assert.Equal(new GatherListItem().TargetCount, second.Items[0].TargetCount);
        Assert.Equal("Shards", second.Name);
    }
}
