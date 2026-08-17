using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class StepConditionsTests
{
    private static readonly QuestSnapshot Snap = new(1622, 1, new byte[] { 16, 16, 0, 0, 0, 32 });

    [Fact]
    public void An_empty_condition_never_holds()
    {
        Assert.False(StepConditions.Holds(null, new FakeStepWorld(), Snap));
        Assert.False(StepConditions.Holds(new StepCondition(), new FakeStepWorld(), Snap));
    }

    [Fact]
    public void Territory_clauses()
    {
        var world = new FakeStepWorld { TerritoryId = 400 };
        Assert.True(StepConditions.Holds(new StepCondition { InTerritory = [400, 401] }, world, Snap));
        Assert.False(StepConditions.Holds(new StepCondition { InTerritory = [401] }, world, Snap));
        Assert.True(StepConditions.Holds(new StepCondition { NotInTerritory = [401] }, world, Snap));
        Assert.False(StepConditions.Holds(new StepCondition { NotInTerritory = [400] }, world, Snap));
    }

    [Fact]
    public void Quest_clauses_require_all_listed()
    {
        var world = new FakeStepWorld();
        world.CompletedQuests.Add(1);
        Assert.True(StepConditions.Holds(new StepCondition { QuestsCompleted = [1] }, world, Snap));
        Assert.False(StepConditions.Holds(new StepCondition { QuestsCompleted = [1, 2] }, world, Snap));
    }

    [Fact]
    public void Flying_clause_reads_the_zone()
    {
        Assert.True(StepConditions.Holds(new StepCondition { Flying = "Unlocked" }, new FakeStepWorld { CanFlyHere = true }, Snap));
        Assert.False(StepConditions.Holds(new StepCondition { Flying = "Unlocked" }, new FakeStepWorld { CanFlyHere = false }, Snap));
        Assert.True(StepConditions.Holds(new StepCondition { Flying = "Locked" }, new FakeStepWorld { CanFlyHere = false }, Snap));
    }

    [Fact]
    public void Flag_clause_uses_the_snapshot()
    {
        var world = new FakeStepWorld();
        Assert.True(StepConditions.Holds(new StepCondition { CompletionQuestVariablesFlags = [null, null, null, null, null, 32] }, world, Snap));
        Assert.False(StepConditions.Holds(new StepCondition { CompletionQuestVariablesFlags = [null, null, null, null, null, 64] }, world, Snap));
    }

    [Fact]
    public void All_clauses_must_hold_together()
    {
        var world = new FakeStepWorld { TerritoryId = 400, CanFlyHere = false };
        var cond = new StepCondition { InTerritory = [400], Flying = "Unlocked" };
        Assert.False(StepConditions.Holds(cond, world, Snap));
        world.CanFlyHere = true;
        Assert.True(StepConditions.Holds(cond, world, Snap));
    }
}

/// <summary>
/// The bundle marks 257 of its 402 Craft steps "skip if the item is already held". Parsing the
/// wrong shape meant the clause was never evaluated, so Odysseus remade things it already had and
/// re-bought materials after a restart — the exact behaviour the data exists to prevent.
/// </summary>
public class ItemSkipConditionTests
{
    private const uint Item = 8131;

    private static QuestStep Craft(int count, bool notInInventory = false) => new()
    {
        Kind = StepKind.Craft, KindName = "Craft", TerritoryId = 154, ItemId = Item, ItemCount = count,
        SkipConditions = new SkipConditions { StepIf = new StepCondition { Item = new ItemCondition { NotInInventory = notInInventory } } },
    };

    private static bool Skip(QuestStep step, int held)
    {
        var world = new FakeStepWorld { TerritoryId = 154 };
        world.Bag[Item] = held;
        return StepConditions.ShouldSkipStep(step, world, QuestSnapshot.Unavailable);
    }

    [Fact]
    public void Enough_in_the_bag_skips_the_craft()
    {
        Assert.True(Skip(Craft(1), held: 1));
        Assert.True(Skip(Craft(3), held: 5));   // more than asked for is still enough
    }

    [Fact]
    public void Too_few_still_crafts()
    {
        Assert.False(Skip(Craft(1), held: 0));
        Assert.False(Skip(Craft(3), held: 2));  // a partial stack is not a reason to skip
    }

    /// <summary>HQ counts: the game takes it, so remaking an NQ copy would be waste.</summary>
    [Fact]
    public void The_count_is_whatever_the_world_reports_regardless_of_quality()
        => Assert.True(Skip(Craft(1), held: 1));

    [Fact]
    public void The_clause_inverts_when_the_bundle_asks_it_to()
    {
        Assert.True(Skip(Craft(1, notInInventory: true), held: 0));
        Assert.False(Skip(Craft(1, notInInventory: true), held: 1));
    }

    /// <summary>Without an item on the step there is nothing to ask, and a skip must never be a guess.</summary>
    [Fact]
    public void A_clause_with_no_item_on_the_step_does_not_skip()
    {
        var step = Craft(1);
        step.ItemId = null;
        Assert.False(Skip(step, held: 99));
    }
}
