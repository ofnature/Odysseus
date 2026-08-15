using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class HandoffTests
{
    private static QuestStep Step(StepKind kind, uint? dataId = null, uint? cfc = null) => new()
    {
        Kind = kind, KindName = kind.ToString(), DataId = dataId, TerritoryId = 400, Position = Vector3.Zero,
        ContentFinderConditionId = cfc,
    };

    private static void Ticks(StepExecutor ex, FakeStepWorld w, int n, double s = 0.5)
    {
        for (var i = 0; i < n && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(s); }
    }

    // ── SinglePlayerDuty → BossMod ──

    [Fact]
    public void Solo_duty_talks_to_the_npc_hands_the_instance_to_bossmod_and_takes_it_back()
    {
        var w = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        w.Spawned.Add(1016034);
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.SinglePlayerDuty, dataId: 1016034));

        Ticks(ex, w, 3);
        Assert.Contains("Interact 1016034", w.Calls);

        // Commence prompt (occupied), then the instance loads.
        w.IsOccupied = true; Ticks(ex, w, 2); w.IsOccupied = false;
        Ticks(ex, w, 2);
        Assert.False(w.BossModAi);              // not yet inside
        w.InDuty = true; Ticks(ex, w, 2);
        Assert.True(w.BossModAi);               // inside: AI on
        Assert.Equal(StepStatus.Running, ex.Status);

        // Fight for a while, die once — that is BossMod's problem, not a fail.
        w.IsDead = true; Ticks(ex, w, 5); w.IsDead = false;
        Assert.Equal(StepStatus.Running, ex.Status);

        // Out.
        w.InDuty = false; Ticks(ex, w, 5);
        Assert.False(w.BossModAi);              // AI off again
        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.Equal(1, w.Calls.Count(c => c == "Interact 1016034")); // never re-entered
    }

    [Fact]
    public void Solo_duty_that_never_loads_fails_with_a_reason()
    {
        var w = new FakeStepWorld();
        w.Spawned.Add(5);
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.SinglePlayerDuty, dataId: 5));
        Ticks(ex, w, 400, s: 1);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("did not start", ex.FailReason);
        Assert.False(w.BossModAi);
    }

    [Fact]
    public void Resuming_inside_a_solo_duty_turns_the_ai_on_without_interacting()
    {
        var w = new FakeStepWorld { InDuty = true };
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.SinglePlayerDuty, dataId: 5));
        Ticks(ex, w, 2);
        Assert.True(w.BossModAi);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Interact"));
    }

    // ── Duty → Theseus ──

    [Fact]
    public void Duty_asks_theseus_waits_for_it_to_finish_and_is_done()
    {
        var w = new FakeStepWorld { TheseusCanEnterDuty = true };
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.Duty, cfc: 247));

        Ticks(ex, w, 2);
        Assert.Contains("TheseusEnter 247", w.Calls);
        Assert.Equal(StepStatus.Running, ex.Status);

        w.InDuty = true; Ticks(ex, w, 10);
        Assert.Equal(StepStatus.Running, ex.Status);

        w.TheseusBusy = false; w.InDuty = false; Ticks(ex, w, 5);
        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.Equal(1, w.Calls.Count(c => c.StartsWith("TheseusEnter")));
    }

    [Fact]
    public void Duty_without_theseus_stops_and_says_so()
    {
        var w = new FakeStepWorld { TheseusCanEnterDuty = false };
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.Duty, cfc: 247));
        Ticks(ex, w, 2);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("Theseus", ex.FailReason);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("TheseusEnter"));
    }

    [Fact]
    public void Duty_theseus_refuses_names_the_cfc()
    {
        var w = new FakeStepWorld { TheseusCanEnterDuty = true, TheseusEnterAccepted = false };
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.Duty, cfc: 239));
        Ticks(ex, w, 2);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("239", ex.FailReason);
    }

    [Fact]
    public void Duty_step_with_no_cfc_fails_at_once()
    {
        var w = new FakeStepWorld { TheseusCanEnterDuty = true };
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.Duty));
        Ticks(ex, w, 2);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("ContentFinderCondition", ex.FailReason);
    }

    // ── long tail ──

    [Fact]
    public void Emote_targets_the_npc_and_sends_the_slash_command()
    {
        var w = new FakeStepWorld();
        w.Spawned.Add(9);
        var ex = new StepExecutor(w);
        var step = Step(StepKind.Emote, dataId: 9);
        step.Emote = "psych";
        ex.Begin(step);
        Ticks(ex, w, 10);
        Assert.Contains("Target 9", w.Calls);
        Assert.Contains("Chat /psych", w.Calls);
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void Jump_is_a_general_action()
    {
        var w = new FakeStepWorld();
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.Jump));
        Ticks(ex, w, 10);
        Assert.Contains("Chat /generalaction Jump", w.Calls);
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void UseItem_targets_uses_and_waits_out_any_dialogue()
    {
        var w = new FakeStepWorld();
        w.Spawned.Add(11);
        var ex = new StepExecutor(w);
        var step = Step(StepKind.UseItem, dataId: 11);
        step.ItemId = 2002199;
        ex.Begin(step);
        Ticks(ex, w, 1);
        Assert.Contains("Target 11", w.Calls);
        Assert.Contains("UseItem 2002199", w.Calls);

        w.IsOccupied = true; Ticks(ex, w, 2); w.IsOccupied = false;
        Ticks(ex, w, 5);
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void UseItem_that_spawns_enemies_goes_to_combat()
    {
        var w = new FakeStepWorld();
        var ex = new StepExecutor(w);
        var step = Step(StepKind.UseItem);
        step.ItemId = 30362;
        step.EnemySpawnType = EnemySpawnType.AfterItemUse;
        ex.Begin(step);
        Ticks(ex, w, 6);
        Assert.Equal("CombatWait", ex.PhaseName);
    }

    [Fact]
    public void UseItem_with_a_missing_target_fails_naming_it()
    {
        var w = new FakeStepWorld();
        var ex = new StepExecutor(w);
        var step = Step(StepKind.UseItem, dataId: 12);
        step.ItemId = 1;
        ex.Begin(step);
        Ticks(ex, w, 2);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("12", ex.FailReason);
    }
}
