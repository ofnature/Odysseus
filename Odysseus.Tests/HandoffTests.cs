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

    // ── Cutscenes hold readiness clocks ──

    [Fact]
    public void A_cutscene_holds_the_readiness_clock_instead_of_timing_the_step_out()
    {
        // Steppe Child's finale was still playing when the roll-on reached the next accept:
        // thirty seconds of ReadyWait ticked against a cutscene and faulted a healthy run.
        var w = new FakeStepWorld { TerritoryId = 957, PlayerPosition = Vector3.Zero, IsReady = false, InCutscene = true };
        w.Spawned.Add(1041332);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AcceptQuest, KindName = "AcceptQuest", DataId = 1041332, TerritoryId = 957, Position = Vector3.Zero });

        for (var i = 0; i < 120 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(1); }
        Assert.Equal(StepStatus.Running, ex.Status);   // 120s of cutscene, no fault

        // The finale ends: the clock runs again, and a genuinely stuck player still faults.
        w.InCutscene = false;
        for (var i = 0; i < 40 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(1); }
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("never became ready", ex.FailReason);
    }

    // ── Travel vs BossMod's movement controller ──

    [Fact]
    public void Travel_commands_the_ai_off_once_and_a_fight_turns_it_back_on()
    {
        // BossMod's AI refuses "off mesh" legs and fights vnavmesh for the character: travel
        // belongs to us, the fight is its. One chat command per transition, not one per step.
        var w = new FakeStepWorld { TerritoryId = 400, ArriveOnMove = true, BossModAiInitially = true };
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 400, Position = new Vector3(50, 0, 0) });
        Assert.False(w.BossModAi);
        Assert.Equal(1, w.Calls.Count(c => c.StartsWith("BmrAi")));

        // A second travel step repeats nothing — the state is already commanded.
        Ticks(ex, w, 10);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 400, Position = new Vector3(80, 0, 0) });
        Assert.Equal(1, w.Calls.Count(c => c.StartsWith("BmrAi")));

        // A fight commands it on.
        w.PlayerPosition = new Vector3(100, 0, 0);
        ex.Begin(new QuestStep { Kind = StepKind.Combat, KindName = "Combat", EnemySpawnType = EnemySpawnType.FinishCombatIfAny, TerritoryId = 400, Position = new Vector3(100, 0, 0) });
        Ticks(ex, w, 10);
        Assert.True(w.BossModAi);
        Assert.Contains("BmrAi True", w.Calls);
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
    public void An_eight_player_trial_stops_by_name_before_theseus_is_asked()
    {
        var w = new FakeStepWorld { TheseusCanEnterDuty = true };
        w.Duties[239] = new Odysseus.Services.Quest.DutyDescription(239, "the Royal Menagerie", IsDungeon: false, PartySize: 8);
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.Duty, cfc: 239));
        Ticks(ex, w, 2);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("the Royal Menagerie", ex.FailReason);
        Assert.Contains("8-player trial", ex.FailReason);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("TheseusEnter"));
    }

    [Fact]
    public void A_dungeon_still_goes_to_theseus()
    {
        var w = new FakeStepWorld { TheseusCanEnterDuty = true };
        w.Duties[247] = new Odysseus.Services.Quest.DutyDescription(247, "Ala Mhigo", IsDungeon: true, PartySize: 4);
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.Duty, cfc: 247));
        Ticks(ex, w, 2);
        Assert.Contains("TheseusEnter 247", w.Calls);
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
        Ticks(ex, w, 2);
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
        Ticks(ex, w, 8);
        Assert.Equal("CombatWait", ex.PhaseName);
    }

    [Fact]
    public void EquipRecommended_prepares_waits_and_equips()
    {
        var w = new FakeStepWorld { RecommendedGearReady = false };
        var ex = new StepExecutor(w);
        ex.Begin(Step(StepKind.EquipRecommended));
        Ticks(ex, w, 3);
        Assert.Contains("PrepareGear", w.Calls);
        Assert.DoesNotContain("EquipGear", w.Calls);
        w.RecommendedGearReady = true;
        Ticks(ex, w, 5);
        Assert.Contains("EquipGear", w.Calls);
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void UseItem_with_a_missing_target_fails_naming_it()
    {
        var w = new FakeStepWorld();
        var ex = new StepExecutor(w);
        var step = Step(StepKind.UseItem, dataId: 12);
        step.ItemId = 1;
        ex.Begin(step);
        Ticks(ex, w, 30); // a target that is not there is asked for a few times before giving up
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("12", ex.FailReason);
    }
}
