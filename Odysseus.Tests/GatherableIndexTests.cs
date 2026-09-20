using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

public class GatherableIndexTests
{
    /// <summary>Every item in the game whose name contains "shard" and can be gathered, as the sheets have them.</summary>
    private static GatherableIndex Shards() => new(
    [
        (2u, "Fire Shard"), (3u, "Ice Shard"), (4u, "Wind Shard"), (5u, "Earth Shard"),
        (6u, "Lightning Shard"), (7u, "Water Shard"),
        (27_801u, "Grade 2 Skybuilders' Umbral Levinshard"),
        (27_802u, "Grade 3 Skybuilders' Umbral Magma Shard"),
        (38_930u, "Splendorous Earth Shard"),
        (38_931u, "Splendorous Water Shard"),
    ]);

    /// <summary>
    /// The field report: a Shards list held Earth, Fire, Ice and Lightning, and searching "shard"
    /// to add the last two offered four Skybuilders' and Splendorous items and neither Water Shard
    /// nor Wind Shard. The four already listed were being cut from the results <i>after</i> the
    /// eight-row limit, so they spent four of the eight slots and the two that sort last fell off
    /// the end — the only two shards missing from the list were the only two unaddable.
    /// </summary>
    [Fact]
    public void Items_already_on_the_list_do_not_eat_the_slots_of_the_ones_that_are_not()
    {
        var onTheList = new uint[] { 5, 2, 3, 6 };   // Earth, Fire, Ice, Lightning
        var found = Shards().Search("shard", 8, onTheList).Select(i => i.Name).ToList();

        Assert.Contains("Water Shard", found);
        Assert.Contains("Wind Shard", found);
        Assert.DoesNotContain("Earth Shard", found);
    }

    /// <summary>What someone typing "shard" wants is a shard, not a Skybuilders' Umbral Levinshard.</summary>
    [Fact]
    public void The_plainest_names_come_first()
    {
        // Shortest first; "Fire Shard" and "Wind Shard" are the same length, so alphabetical breaks it.
        var found = Shards().Search("shard", 4).Select(i => i.Name).ToList();
        Assert.Equal(["Ice Shard", "Fire Shard", "Wind Shard", "Earth Shard"], found);
    }

    [Fact]
    public void An_exact_name_wins_over_anything_it_is_a_part_of()
    {
        var found = Shards().Search("Water Shard", 3).Select(i => i.Name).ToList();
        Assert.Equal("Water Shard", found[0]);
        Assert.Contains("Splendorous Water Shard", found);
    }

    [Fact]
    public void The_cut_is_still_honoured_and_the_search_is_case_blind()
    {
        Assert.Equal(2, Shards().Search("SHARD", 2).Count);
        Assert.Empty(Shards().Search("nothing here", 8));
    }
}
