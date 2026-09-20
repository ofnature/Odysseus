using System.Numerics;
using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

public class GatherReadinessTests
{
    private static GatheringTarget Node(uint job, ushort level) =>
        new(5111, 100, 400, job, level, [Vector3.Zero]);

    private static Func<uint, int> Levels(int miner = 0, int botanist = 0)
        => job => job == GatherReadiness.Miner ? miner : job == GatherReadiness.Botanist ? botanist : 0;

    [Fact]
    public void A_node_this_character_can_work_is_a_yes()
        => Assert.True(GatherReadiness.CanGather(Node(GatherReadiness.Miner, 50), Levels(miner: 90)));

    [Fact]
    public void Exactly_the_nodes_level_is_enough()
        => Assert.True(GatherReadiness.CanGather(Node(GatherReadiness.Botanist, 90), Levels(botanist: 90)));

    /// <summary>
    /// The case the promise exists for: a crafting plugin queues the smelt on the strength of this
    /// answer, so "there is a node" is not enough — it has to be a node this character can work.
    /// </summary>
    [Fact]
    public void A_node_above_this_characters_level_is_a_no_that_says_both_numbers()
    {
        var why = GatherReadiness.WhyNot(Node(GatherReadiness.Miner, 90), Levels(miner: 54), "Copper Ore");
        Assert.NotNull(why);
        Assert.Contains("Miner 90", why);
        Assert.Contains("Miner 54", why);
    }

    [Fact]
    public void A_class_that_is_not_unlocked_is_a_no_that_says_so()
    {
        var why = GatherReadiness.WhyNot(Node(GatherReadiness.Botanist, 5), Levels(miner: 100), "Maple Log");
        Assert.NotNull(why);
        Assert.Contains("not unlocked", why);
        Assert.Contains("Botanist", why);
    }

    [Fact]
    public void Fish_are_a_no_until_Odysseus_fishes()
    {
        var why = GatherReadiness.WhyNot(Node(GatherReadiness.Fisher, 1), Levels(miner: 100, botanist: 100), "Dusk Bass");
        Assert.NotNull(why);
        Assert.Contains("does not fish", why);
    }

    [Fact]
    public void An_item_with_no_workable_node_is_a_no_rather_than_a_crash()
    {
        var why = GatherReadiness.WhyNot(null, Levels(miner: 100), "Darksteel Ore");
        Assert.NotNull(why);
        Assert.Contains("Darksteel Ore", why);
        Assert.False(GatherReadiness.CanGather(null, Levels(miner: 100)));
    }
}
