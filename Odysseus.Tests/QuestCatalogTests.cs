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
    public void Frontier_is_the_next_unfinished_unlocked_msq_quest()
    {
        var done = new HashSet<ushort> { 1618, 1619, 1620 };
        Assert.Equal((ushort)1621, Catalog.CurrentMainScenario(done.Contains)!.QuestId);
        done.UnionWith([1621, 1622]);
        Assert.Equal((ushort)1623, Catalog.CurrentMainScenario(done.Contains)!.QuestId);
    }

    [Fact]
    public void Frontier_skips_untaken_alternates_and_untouched_roots()
    {
        // Three city starts; the character took Gridania (39 -> 85), the class variants 123/124 were never taken.
        var catalog = new QuestCatalog(
        [
            new QuestListing(39, "Coming to Gridania", 1, 0, true, [], 0),
            new QuestListing(107, "Coming to Limsa Lominsa", 1, 0, true, [], 0),
            new QuestListing(85, "Close to Home", 1, 0, true, [39], 1),
            new QuestListing(123, "Close to Home", 1, 0, true, [39], 1),
            new QuestListing(124, "Close to Home", 1, 0, true, [39], 1),
            new QuestListing(86, "Next", 2, 0, true, [85, 123, 124], 2),
            new QuestListing(87, "After", 3, 0, true, [86], 1),
        ]);
        var done = new HashSet<ushort> { 39, 85, 86 };
        // 107 is a root (no prerequisites) — not offered. 123/124 have a completed successor (86) — dead alternates.
        Assert.Equal((ushort)87, catalog.CurrentMainScenario(done.Contains)!.QuestId);

        // Brand-new character: lowest root.
        Assert.Equal((ushort)39, catalog.CurrentMainScenario(_ => false)!.QuestId);
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
