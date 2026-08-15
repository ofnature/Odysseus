using Odysseus.Services.Quest;

namespace Odysseus.Tests;

public class QuestCatalogTests
{
    // The Moghome fan from the real sheet: 1619 -> {1620, 1621, 1622} (any order) -> 1623 (needs all three).
    private static readonly QuestCatalog Catalog = new(
    [
        new QuestListing(1619, "Mountaintop Diplomacy", 54, 1, true, [1618], 1),
        new QuestListing(1620, "Moghan's Trial", 54, 1, true, [1619], 1),
        new QuestListing(1621, "Mogmug's Trial", 54, 1, true, [1619], 1),
        new QuestListing(1622, "Mogwin's Trial", 54, 1, true, [1619], 1),
        new QuestListing(1623, "Moglin's Judgment", 54, 1, true, [1620, 1621, 1622], 1),
        new QuestListing(1624, "Leaving Moghome", 54, 1, true, [1623], 1),
        new QuestListing(9001, "Some Sidequest", 54, 1, false, [1619], 1),
    ]);

    [Fact]
    public void Linear_link_goes_to_the_successor()
    {
        var done = new HashSet<ushort> { 1618, 1619, 1620, 1621, 1622, 1623 };
        Assert.Equal((ushort)1624, Catalog.NextMainScenario(1623, done.Contains));
    }

    [Fact]
    public void A_fan_takes_the_siblings_first_then_the_join()
    {
        var done = new HashSet<ushort> { 1618, 1619 };
        Assert.Equal((ushort)1620, Catalog.NextMainScenario(1619, done.Contains));

        done.Add(1620);
        // 1623 is 1620's successor but not ready; step back to 1619's fan and take the next sibling.
        Assert.Equal((ushort)1621, Catalog.NextMainScenario(1620, done.Contains));

        done.Add(1621);
        Assert.Equal((ushort)1622, Catalog.NextMainScenario(1621, done.Contains));

        done.Add(1622);
        Assert.Equal((ushort)1623, Catalog.NextMainScenario(1622, done.Contains));
    }

    [Fact]
    public void Side_quests_are_never_the_next_msq_quest()
    {
        var done = new HashSet<ushort> { 1618, 1619, 1620, 1621, 1622 };
        // 9001 hangs off 1619 too, but it is not MSQ.
        Assert.NotEqual((ushort)9001, Catalog.NextMainScenario(1619, done.Contains));
    }

    [Fact]
    public void End_of_the_chain_is_null()
    {
        var done = new HashSet<ushort> { 1618, 1619, 1620, 1621, 1622, 1623, 1624 };
        Assert.Null(Catalog.NextMainScenario(1624, done.Contains));
    }

    [Fact]
    public void Join_semantics()
    {
        var all = new QuestListing(1, "x", 1, 0, true, [10, 11], 1);
        var any = new QuestListing(2, "x", 1, 0, true, [10, 11], 2);
        var none = new QuestListing(3, "x", 1, 0, true, [], 0);
        Func<ushort, bool> only10 = id => id == 10;
        Assert.False(all.IsUnlockedBy(only10));
        Assert.True(any.IsUnlockedBy(only10));
        Assert.True(none.IsUnlockedBy(_ => false));
    }
}
