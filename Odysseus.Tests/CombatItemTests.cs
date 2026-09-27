using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>
/// "Weaken it, then use the item" — Questionable's <c>CombatItemUse</c>, 60 steps across 34 quests.
/// Are They Ill-tempered (2883) is the one that surfaced it: use item 2002315 on mob 6599 once it is
/// under half health. The converter dropped the field, so the step simply killed the mob.
/// </summary>
public class CombatItemTests
{
    private const uint Mob = 6599;
    private const uint Net = 2002315;

    /// <summary>The 2883 step, as the Questionable bundle has it.</summary>
    private const string AreTheyIllTempered = """
        {
          "QuestSequence": [
            { "Sequence": 1, "Steps": [
              { "Position": { "X": -197.38828, "Y": 3.820687, "Z": 288.09766 }, "TerritoryId": 621,
                "InteractionType": "Combat", "KillEnemyDataIds": [ 6599 ], "EnemySpawnType": "OverworldEnemies",
                "CombatItemUse": { "ItemId": 2002315, "Condition": "Health%", "Value": 50 } } ] }
          ]
        }
        """;

    private static QuestStep Converted(string json)
    {
        var path = QuestionableImporter.Parse("2883_Are They Ill-tempered.json", "QuestPaths/4.x - Stormblood/Aether Currents", json, out _)!;
        return path.Block(1)!.Steps[0];
    }

    // ── Conversion ──

    [Fact]
    public void The_converter_keeps_the_item_its_condition_and_its_threshold()
    {
        var step = Converted(AreTheyIllTempered);
        Assert.Equal(StepKind.Combat, step.Kind);
        Assert.Equal(new CombatItemUse(Net, CombatItemCondition.HealthPercent, 50), step.CombatItemUse);
    }

    [Theory]
    [InlineData("Health%", CombatItemCondition.HealthPercent)]
    [InlineData("Incapacitated", CombatItemCondition.Incapacitated)]
    [InlineData("MissingStatus", CombatItemCondition.MissingStatus)]
    [InlineData("SomethingNew", CombatItemCondition.Unknown)]
    public void Every_condition_the_bundle_uses_is_read(string written, CombatItemCondition expected)
    {
        var step = Converted(AreTheyIllTempered.Replace("\"Health%\"", $"\"{written}\""));
        Assert.Equal(expected, step.CombatItemUse!.Condition);
    }

    [Fact]
    public void It_survives_the_shipped_library_s_round_trip()
    {
        var path = QuestionableImporter.Parse("2883_x.json", "QuestPaths/x", AreTheyIllTempered, out _)!;
        using var stream = new MemoryStream();
        PathPack.Write(stream, [path]);
        stream.Position = 0;
        var back = Assert.Single(PathPack.Read(stream));
        Assert.Equal(path.Block(1)!.Steps[0].CombatItemUse, back.Block(1)!.Steps[0].CombatItemUse);
    }

    /// <summary>A path converted before the field existed carries a combat step that lost it: worth re-converting.</summary>
    [Fact]
    public void A_path_converted_before_the_field_existed_is_worth_converting_again()
    {
        var old = new QuestPath
        {
            FormatVersion = 4,
            Sequences = [new QuestSequence { Sequence = 1, Steps = [new QuestStep { Kind = StepKind.Combat }] }],
        };
        Assert.True(old.NeedsReconvert);
    }

    // ── When the mob is ready ──

    private static CombatTargetReading Reading(float health = 100, bool knee = false, params uint[] statuses)
        => new(Mob, health, knee, statuses);

    [Theory]
    [InlineData(80f, false)]
    [InlineData(50f, false)]      // at the line is not under it
    [InlineData(49.9f, true)]
    public void A_health_threshold_is_met_below_it(float health, bool ready)
        => Assert.Equal(ready, StepExecutor.CombatItemReady(new CombatItemUse(Net, CombatItemCondition.HealthPercent, 50), Reading(health)));

    [Fact]
    public void Incapacitated_is_the_knee_whatever_the_health()
    {
        var use = new CombatItemUse(Net, CombatItemCondition.Incapacitated, 0);
        Assert.False(StepExecutor.CombatItemReady(use, Reading(health: 5)));
        Assert.True(StepExecutor.CombatItemReady(use, Reading(health: 60, knee: true)));
    }

    [Fact]
    public void A_missing_status_is_met_once_it_is_gone()
    {
        var use = new CombatItemUse(Net, CombatItemCondition.MissingStatus, 1234);
        Assert.False(StepExecutor.CombatItemReady(use, Reading(statuses: [1234, 99])));
        Assert.True(StepExecutor.CombatItemReady(use, Reading(statuses: [99])));
    }

    // ── In the fight ──

    private static QuestStep Fight(CombatItemCondition condition, int value = 50) => new()
    {
        Kind = StepKind.Combat, KindName = "Combat", TerritoryId = 400, Position = Vector3.Zero,
        EnemySpawnType = EnemySpawnType.OverworldEnemies, KillEnemyDataIds = [Mob],
        CombatItemUse = new CombatItemUse(Net, condition, value),
    };

    private static void Ticks(StepExecutor ex, FakeStepWorld w, int n)
    {
        for (var i = 0; i < n && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
    }

    /// <summary>
    /// The field report. At level 100 one Daedalus GCD killed the mob from full, so it never sat
    /// under half long enough to take the item. Daedalus is held from the start of the fight;
    /// auto-attack brings the mob down; the item goes on under the line — and not before it.
    /// </summary>
    [Fact]
    public void A_health_threshold_holds_daedalus_all_fight_and_uses_the_item_under_the_line()
    {
        var w = new FakeStepWorld { InCombat = true, Target = Reading(100) };
        var ex = new StepExecutor(w);
        ex.Begin(Fight(CombatItemCondition.HealthPercent));

        Ticks(ex, w, 4);
        Assert.True(w.ActionsHeld);                                      // held before anyone swings
        Assert.DoesNotContain($"UseItem {Net}", w.Calls);

        w.Target = Reading(65);                                          // auto-attack chipping it
        Ticks(ex, w, 4);
        Assert.DoesNotContain($"UseItem {Net}", w.Calls);

        w.Target = Reading(44);
        Ticks(ex, w, 1);
        Assert.Contains($"UseItem {Net}", w.Calls);
        Assert.True(w.ActionsHeld);                                      // still held while it goes on
    }

    [Fact]
    public void The_item_is_not_pressed_every_frame_while_it_goes_on()
    {
        var w = new FakeStepWorld { InCombat = true, Target = Reading(30) };
        var ex = new StepExecutor(w);
        ex.Begin(Fight(CombatItemCondition.HealthPercent));

        for (var i = 0; i < 20 && !w.Calls.Contains($"UseItem {Net}"); i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Single(w.Calls, c => c == $"UseItem {Net}");            // the first press

        Ticks(ex, w, 3);                                                 // 1.5s on: not again yet
        Assert.Single(w.Calls, c => c == $"UseItem {Net}");
        Ticks(ex, w, 2);                                                 // past the 2s retry: pressed again
        Assert.Equal(2, w.Calls.Count(c => c == $"UseItem {Net}"));
    }

    /// <summary>An incapacitated mob cannot die, so there is no race: Daedalus fights freely until the knee.</summary>
    [Fact]
    public void Incapacitated_lets_daedalus_fight_and_holds_only_while_the_item_goes_on()
    {
        var w = new FakeStepWorld { InCombat = true, Target = Reading(40) };
        var ex = new StepExecutor(w);
        ex.Begin(Fight(CombatItemCondition.Incapacitated));

        Ticks(ex, w, 4);
        Assert.False(w.ActionsHeld);
        Assert.DoesNotContain($"UseItem {Net}", w.Calls);

        w.Target = Reading(40, knee: true);
        Ticks(ex, w, 1);
        Assert.True(w.ActionsHeld);
        Assert.Contains($"UseItem {Net}", w.Calls);
    }

    /// <summary>
    /// Held, in the fight, and nobody targeting the mob: Daedalus is holding, so it will not pick it
    /// up. Odysseus engages it, and auto-attack does the rest.
    /// </summary>
    [Fact]
    public void Held_with_no_target_odysseus_engages_the_mob_itself()
    {
        var w = new FakeStepWorld { InCombat = true, Target = null };
        w.AttackResults.Enqueue(true);
        var ex = new StepExecutor(w);
        ex.Begin(Fight(CombatItemCondition.HealthPercent));

        Ticks(ex, w, 2);
        Assert.Contains("Attack", w.Calls);
    }

    /// <summary>The hold never outlives the step: done, failed or cancelled, Daedalus gets it back at once.</summary>
    [Fact]
    public void Leaving_the_step_gives_daedalus_its_actions_back()
    {
        var w = new FakeStepWorld { InCombat = true, Target = Reading(100) };
        var ex = new StepExecutor(w);
        ex.Begin(Fight(CombatItemCondition.HealthPercent));
        Ticks(ex, w, 2);
        Assert.True(w.ActionsHeld);

        ex.Cancel();
        Assert.False(w.ActionsHeld);
        Assert.Contains("Hold False", w.Calls);
    }

    /// <summary>
    /// 2883 run solo on v0.2.9: the mob died to Daedalus without the item. A mob that aggroes on the
    /// approach is fought before the fight phase starts, so the hold starts with the step itself.
    /// </summary>
    [Fact]
    public void The_hold_starts_with_the_step_not_with_the_fight()
    {
        var w = new FakeStepWorld { TerritoryId = 400, PlayerPosition = new Vector3(300, 0, 300) };
        var ex = new StepExecutor(w);
        ex.Begin(Fight(CombatItemCondition.HealthPercent));

        ex.Tick();
        Assert.True(w.ActionsHeld);                                      // from the first tick

        Ticks(ex, w, 6);
        Assert.Contains(w.Calls, c => c.StartsWith("Move") || c == "Mount"); // still on the way there
        Assert.DoesNotContain("Attack", w.Calls);
        Assert.True(w.ActionsHeld);
    }

    [Fact]
    public void A_hold_daedalus_does_not_take_is_said_once()
    {
        var w = new FakeStepWorld { InCombat = true, Target = Reading(100), HoldAccepted = false };
        var ex = new StepExecutor(w);
        ex.Begin(Fight(CombatItemCondition.HealthPercent));

        Ticks(ex, w, 8);
        Assert.Single(w.Calls, c => c.StartsWith("Log") && c.Contains("did not take the hold"));
    }

    [Fact]
    public void A_fight_that_ends_without_the_item_says_where_the_mob_was_last_seen()
    {
        var w = new FakeStepWorld { InCombat = true, Target = Reading(100) };
        var ex = new StepExecutor(w);
        ex.Begin(Fight(CombatItemCondition.HealthPercent));
        Ticks(ex, w, 4);

        w.Target = Reading(62);
        Ticks(ex, w, 2);
        w.Target = null;                                                 // one swing: gone
        w.InCombat = false;
        Ticks(ex, w, 40);

        Assert.DoesNotContain($"UseItem {Net}", w.Calls);
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("at 62%"));
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("without item") && c.Contains("last seen at 62%"));
    }

    [Fact]
    public void An_unknown_condition_is_fought_as_an_ordinary_fight_and_said_once()
    {
        var w = new FakeStepWorld { InCombat = true, Target = Reading(10) };
        var ex = new StepExecutor(w);
        ex.Begin(Fight(CombatItemCondition.Unknown));

        Ticks(ex, w, 6);
        Assert.False(w.ActionsHeld);
        Assert.DoesNotContain($"UseItem {Net}", w.Calls);
        Assert.Single(w.Calls, c => c.StartsWith("Log") && c.Contains("does not know"));
    }

    [Fact]
    public void A_fight_with_no_item_is_untouched()
    {
        var w = new FakeStepWorld { InCombat = true, Target = Reading(10) };
        var ex = new StepExecutor(w);
        var step = Fight(CombatItemCondition.HealthPercent);
        step.CombatItemUse = null;
        ex.Begin(step);

        Ticks(ex, w, 6);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Hold") || c.StartsWith("UseItem"));
    }
}
