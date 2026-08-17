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

/// <summary>
/// The journal grouping is what makes a raid or trial unlock chain findable. The engine runs any
/// quest; without the section and category nothing could locate them among four thousand.
/// </summary>
public class JournalGroupingTests
{
    private static readonly QuestCatalog Catalog = new(
    [
        new QuestListing(3000, "Alexander's Heart", 60, 1, false, [], 0)
            { Section = "Chronicles of a New Era", Category = "Alexander" },
        new QuestListing(3001, "Heart of the Creator", 70, 1, false, [3000], 1)
            { Section = "Chronicles of a New Era", Category = "Alexander" },
        new QuestListing(3002, "Bloody Reprisal", 60, 1, false, [], 0)
            { Section = "Chronicles of a New Era", Category = "Warring Triad" },
        new QuestListing(1619, "Mountaintop Diplomacy", 54, 1, true, [], 0)
            { Section = "Main Scenario (A Realm Reborn through Endwalker)", Category = "Heavensward" },
        new QuestListing(4000, "A Hingan Tale", 61, 2, false, [], 0),
    ]);

    [Fact]
    public void Quests_group_by_section_and_category()
    {
        var chronicles = Catalog.All.Where(q => q.Section == "Chronicles of a New Era").ToList();
        Assert.Equal(3, chronicles.Count);
        Assert.Equal(["Alexander", "Warring Triad"],
            chronicles.Select(q => q.Category).Distinct().OrderBy(x => x));
    }

    [Fact]
    public void A_quest_with_no_journal_row_still_lists_rather_than_vanishing()
    {
        var loose = Catalog.All.Single(q => q.QuestId == 4000);
        Assert.Equal(string.Empty, loose.Section);
        Assert.Equal(string.Empty, loose.Category);
        Assert.Equal(5, Catalog.All.Count());
    }

    /// <summary>Grouping must not disturb what the section is actually used for.</summary>
    [Fact]
    public void The_msq_test_is_unchanged_by_carrying_the_category()
    {
        Assert.True(Catalog.ById(1619)!.IsMainScenario);
        Assert.False(Catalog.ById(3000)!.IsMainScenario);
    }
}

/// <summary>
/// The journal puts all 126 crafter quests in one category, eight interleaved lines deep. Splitting
/// by class is what makes that readable, so the split has to be exact about when it applies.
/// </summary>
public class JobGroupingTests
{
    private static QuestListing Job(ushort id, string name, string job, ushort level) =>
        new(id, name, level, 2, false, [], 0) { Section = "Class & Job Quests", Category = "Disciple of the Hand Quests", JobName = job };

    private static readonly QuestCatalog Catalog = new(
    [
        Job(205, "My First Saw", "Carpenter", 1),
        Job(106, "A Test of Technique", "Carpenter", 5),
        Job(292, "My First Cross-pein Hammer", "Blacksmith", 1),
        Job(535, "My First Needle", "Weaver", 1),
        new QuestListing(9000, "Nothing In Particular", 1, 2, false, [], 0)
            { Section = "Class & Job Quests", Category = "Disciple of the Hand Quests" },
    ]);

    [Fact]
    public void A_mixed_category_splits_into_one_group_per_class()
    {
        var byJob = Catalog.All.GroupBy(q => q.JobName).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, byJob["Carpenter"]);
        Assert.Equal(1, byJob["Blacksmith"]);
        Assert.Equal(1, byJob["Weaver"]);
        Assert.Equal(1, byJob[string.Empty]);   // falls into "Other" rather than disappearing
    }

    [Fact]
    public void A_class_line_reads_in_the_order_it_is_done()
    {
        var carpenter = Catalog.All.Where(q => q.JobName == "Carpenter")
            .OrderBy(q => q.ClassJobLevel).ThenBy(q => q.QuestId).Select(q => q.Name).ToList();
        Assert.Equal(["My First Saw", "A Test of Technique"], carpenter);
    }
}
