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
