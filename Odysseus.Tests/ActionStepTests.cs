using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class ActionStepTests
{
    private static void Ticks(StepExecutor ex, FakeStepWorld w, int n, double s = 0.5)
    {
        for (var i = 0; i < n && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(s); }
    }

    [Fact]
    public void Importer_keeps_action_name_ground_target_and_required_variables()
    {
        const string json = """
            { "QuestSequence": [ { "Sequence": 1, "Steps": [
              { "DataId": 2003241, "Position": { "X": 1, "Y": 2, "Z": 3 }, "TerritoryId": 152, "InteractionType": "Action", "Action": "Big Sneeze",
                "RequiredQuestVariables": [null,null,null,[ {"High": 3},{"High": 4} ],null,null] },
              { "Position": { "X": 1, "Y": 2, "Z": 3 }, "TerritoryId": 152, "InteractionType": "Action", "Action": "Seed", "GroundTarget": true,
                "RequiredQuestVariables": [null, 32, null, null, null, {"Low": 1}] } ] } ] }
            """;
        var path = QuestionableImporter.Parse("1_x.json", "QuestPaths/x", json, out var unknown)!;
        Assert.Equal(0, unknown);
        var a = path.Block(1)!.Steps[0];
        Assert.Equal(StepKind.Action, a.Kind);
        Assert.Equal("Big Sneeze", a.ActionName);
        Assert.False(a.GroundTarget);
        Assert.NotNull(a.RequiredQuestVariables);
        Assert.Equal(2, a.RequiredQuestVariables![3]!.Count);
        Assert.Null(a.RequiredQuestVariables[0]);

        var b = path.Block(1)!.Steps[1];
        Assert.True(b.GroundTarget);
        Assert.Equal((byte)32, b.RequiredQuestVariables![1]![0].Exact);
        Assert.Equal((byte)1, b.RequiredQuestVariables[5]![0].Low);
    }

    [Fact]
    public void Required_variables_select_steps_by_nibble_or_byte()
    {
        var step = new QuestStep { RequiredQuestVariables = [null, null, null, [new VariableMatch(null, 3, null), new VariableMatch(null, 4, null)], null, null] };
        Assert.True(step.RequiredVariablesMet(new byte[] { 0, 0, 0, 0x30, 0, 0 }));
        Assert.True(step.RequiredVariablesMet(new byte[] { 0, 0, 0, 0x4F, 0, 0 }));
        Assert.False(step.RequiredVariablesMet(new byte[] { 0, 0, 0, 0x20, 0, 0 }));

        var exact = new QuestStep { RequiredQuestVariables = [null, [new VariableMatch(32, null, null)], null, null, null, null] };
        Assert.True(exact.RequiredVariablesMet(new byte[] { 0, 32, 0, 0, 0, 0 }));
        Assert.False(exact.RequiredVariablesMet(new byte[] { 0, 33, 0, 0, 0, 0 }));

        Assert.True(new QuestStep().RequiredVariablesMet(new byte[6])); // no requirement
    }

    [Fact]
    public void Action_step_targets_uses_the_resolved_action_and_settles()
    {
        var w = new FakeStepWorld();
        w.Spawned.Add(2003241);
        w.Actions["Big Sneeze"] = 12345;
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Action, KindName = "Action", DataId = 2003241, Position = Vector3.Zero, TerritoryId = 400, ActionName = "Big Sneeze" });
        Ticks(ex, w, 12);
        Assert.Contains("Target 2003241", w.Calls);
        Assert.Contains("UseAction 12345", w.Calls);
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void Ground_target_action_uses_the_position_and_no_target()
    {
        var w = new FakeStepWorld();
        w.Actions["Seed"] = 777;
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Action, KindName = "Action", Position = new Vector3(5, 0, 5), TerritoryId = 400, ActionName = "Seed", GroundTarget = true });
        w.PlayerPosition = new Vector3(5, 0, 5);
        Ticks(ex, w, 12);
        Assert.Contains("UseAction 777 @(5,0,5)", w.Calls);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Target"));
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void Unknown_action_name_fails_naming_it()
    {
        var w = new FakeStepWorld();
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Action, KindName = "Action", Position = Vector3.Zero, TerritoryId = 400, ActionName = "Made Up" });
        Ticks(ex, w, 3);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("Made Up", ex.FailReason);
    }

    [Fact]
    public void Instruction_and_status_off_are_no_ops()
    {
        var w = new FakeStepWorld();
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Instruction, KindName = "Instruction", TerritoryId = 400, Comment = "read me" });
        Ticks(ex, w, 3);
        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.Contains(w.Calls, c => c.Contains("read me"));
        ex.Begin(new QuestStep { Kind = StepKind.StatusOff, KindName = "StatusOff", TerritoryId = 400 });
        Ticks(ex, w, 3);
        Assert.Equal(StepStatus.Done, ex.Status);
    }
}
