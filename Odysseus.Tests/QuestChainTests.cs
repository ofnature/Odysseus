using Odysseus.Services.Quest;

namespace Odysseus.Tests;

public class QuestChainTests
{
    // Shaped like the real Zhloe unlock: a target with prerequisites that have prerequisites.
    private static readonly QuestCatalog Catalog = new(
    [
        new QuestListing(10, "Root", 1, 0, false, [], 0),
        new QuestListing(20, "Middle", 30, 0, false, [10], 1),
        new QuestListing(30, "Other", 40, 0, false, [10], 1),
        new QuestListing(40, "Target", 60, 0, false, [20, 30], 1),
        new QuestListing(50, "EitherA", 20, 0, false, [10], 1),
        new QuestListing(60, "EitherB", 20, 0, false, [10, 20, 30], 1),
        new QuestListing(70, "AnyOf", 50, 0, false, [50, 60], 2),
        new QuestListing(80, "Cyclic", 10, 0, false, [90], 1),
        new QuestListing(90, "Cyclic2", 10, 0, false, [80], 1),
    ]);

    private static ChainPlan Resolve(ushort target, IEnumerable<ushort>? complete = null, IEnumerable<ushort>? noPath = null)
    {
        var done = new HashSet<ushort>(complete ?? []);
        var missing = new HashSet<ushort>(noPath ?? []);
        return QuestChain.Resolve(target, Catalog, done.Contains, id => !missing.Contains(id));
    }

    [Fact]
    public void Prerequisites_come_first_and_the_target_is_last()
    {
        var plan = Resolve(40);
        Assert.Equal(new ushort[] { 10, 20, 30, 40 }, plan.Steps.Select(s => s.QuestId));
        Assert.True(plan.IsRunnable);
        Assert.Equal("4 quests", plan.Summary);
    }

    [Fact]
    public void Completed_prerequisites_drop_out()
    {
        var plan = Resolve(40, complete: [10, 20]);
        Assert.Equal(new ushort[] { 30, 40 }, plan.Steps.Select(s => s.QuestId));
    }

    [Fact]
    public void A_completed_target_is_already_done()
    {
        var plan = Resolve(40, complete: [40]);
        Assert.True(plan.AlreadyDone);
        Assert.Empty(plan.Steps);
        Assert.Equal("already done", plan.Summary);
    }

    [Fact]
    public void Any_of_takes_one_branch_and_skips_it_entirely_when_one_is_done()
    {
        // 70 needs 50 or 60; neither done → the shallower branch (50) is taken, not both.
        var plan = Resolve(70);
        Assert.Contains((ushort)50, plan.Steps.Select(s => s.QuestId));
        Assert.DoesNotContain((ushort)60, plan.Steps.Select(s => s.QuestId));

        // 60 done → nothing from either branch.
        var satisfied = Resolve(70, complete: [60]);
        Assert.Equal(new ushort[] { 70 }, satisfied.Steps.Select(s => s.QuestId));
    }

    [Fact]
    public void Missing_paths_are_reported_and_block_running()
    {
        var plan = Resolve(40, noPath: [20]);
        Assert.False(plan.IsRunnable);
        Assert.Equal((ushort)20, Assert.Single(plan.MissingPaths).QuestId);
        Assert.Contains("without a path", plan.Summary);
    }

    [Fact]
    public void A_cycle_terminates_instead_of_hanging()
    {
        var plan = Resolve(80);
        Assert.Contains((ushort)80, plan.Steps.Select(s => s.QuestId));
        Assert.True(plan.Steps.Count <= 2);
    }

    [Fact]
    public void An_unobtainable_prerequisite_is_reported()
    {
        // The Grand Company fork: taking one locks the others out.
        var catalog = new QuestCatalog(
        [
            new QuestListing(680, "Twin Adder", 20, 0, true, [], 0, [681, 682], 2),
            new QuestListing(681, "Maelstrom", 20, 0, true, [], 0, [680, 682], 2),
            new QuestListing(700, "After", 20, 0, true, [681], 1),
        ]);
        var done = new HashSet<ushort> { 680 };
        var plan = QuestChain.Resolve(700, catalog, done.Contains, _ => true);
        Assert.Contains((ushort)681, plan.Unobtainable);
        Assert.False(plan.IsRunnable);
        Assert.Contains("unobtainable", plan.Summary);
    }

    [Fact]
    public void Queueing_puts_the_chain_on_the_priority_list_in_order()
    {
        var quests = new FakeQuestStateReader();
        var priority = new PriorityList(Catalog, null, persist: false, save: null);
        var log = new List<string>();
        var planner = new UnlockPlanner(Catalog, quests, priority, _ => true, log.Add);

        var plan = planner.Queue(40, "Test society");
        Assert.Equal(new ushort[] { 10, 20, 30, 40 }, priority.Ids);
        Assert.Equal(4, plan.Steps.Count);
        Assert.Contains(log, l => l.Contains("Test society") && l.Contains("Root → Middle → Other → Target"));

        // Queueing again adds nothing (already listed).
        planner.Queue(40, "Test society");
        Assert.Equal(4, priority.Count);
    }
}
