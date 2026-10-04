using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;
using Odysseus.Services.Travel;

namespace Odysseus.Tests;

/// <summary>
/// Attuning aetherytes and shards. Before format 7 an attune step had no name to go to and finished
/// in a frame — the newtoons' Doman Enclave was never attuned, and its teleport was refused later.
/// </summary>
public class AttuneTests
{
    private const uint Enclave = 127;

    private static void Ticks(StepExecutor ex, FakeStepWorld w, int n)
    {
        for (var i = 0; i < n && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
    }

    [Fact]
    public void The_converter_keeps_which_aetheryte_an_attune_step_means()
    {
        const string json = """
            { "QuestSequence": [ { "Sequence": 4, "Steps": [
              { "TerritoryId": 759, "InteractionType": "AttuneAetheryte", "Aetheryte": "Doman Enclave" },
              { "TerritoryId": 132, "InteractionType": "AttuneAethernetShard", "AethernetShard": "[Gridania] Archers' Guild" } ] } ] }
            """;
        var path = QuestionableImporter.Parse("3026_x.json", "QuestPaths/x", json, out _)!;
        Assert.Equal("Doman Enclave", path.Block(4)!.Steps[0].AttuneName);
        Assert.Equal("[Gridania] Archers' Guild", path.Block(4)!.Steps[1].AttuneName);
    }

    [Fact]
    public void An_attune_step_walks_to_the_aetheryte_and_attunes_it()
    {
        var w = new FakeStepWorld { TerritoryId = 759, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(30, 0, -10);
        w.Attunables[Enclave] = ("The Doman Enclave", new Vector3(42, 0, -15));
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AttuneAetheryte, KindName = "AttuneAetheryte", TerritoryId = 759, AttuneName = "The Doman Enclave" });

        Ticks(ex, w, 30);
        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.Contains(Enclave, w.AttunedIds);
        Assert.Contains("InteractAetheryte", w.Calls);
        Assert.Contains("CloseMenus", w.Calls);
    }

    [Fact]
    public void An_aetheryte_attuned_already_is_passed_straight_away()
    {
        var w = new FakeStepWorld { TerritoryId = 759 };
        w.Attunables[Enclave] = ("The Doman Enclave", new Vector3(42, 0, -15));
        w.AttunedIds.Add(Enclave);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AttuneAetheryte, KindName = "AttuneAetheryte", TerritoryId = 759, AttuneName = "The Doman Enclave" });
        Ticks(ex, w, 3);
        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.DoesNotContain("InteractAetheryte", w.Calls);
    }

    /// <summary>Passing one on the way to a quest step: attuned first, then the step runs as it would have.</summary>
    [Fact]
    public void An_unattuned_aetheryte_in_passing_is_attuned_before_the_step()
    {
        var w = new FakeStepWorld { TerritoryId = 759, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(30, 0, -10);
        w.Attunables[Enclave] = ("The Doman Enclave", new Vector3(42, 0, -15));
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 759, Position = new Vector3(0, 0, 60) });

        Ticks(ex, w, 60);
        Assert.Contains(Enclave, w.AttunedIds);
        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.Contains(w.Calls, c => c.StartsWith("Move 0,0,60"));   // and the step itself after
    }

    [Fact]
    public void A_hop_to_a_shard_not_attuned_in_this_zone_walks_instead()
    {
        var w = new FakeStepWorld { TerritoryId = 130, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(0, 0, 0);
        w.AethernetAccess[130] = new Vector3(2, 0, 2);
        w.AethernetTerritories["[Ul'dah] Sapphire Avenue Exchange"] = 130;
        w.UnattunedHops.Add("[Ul'dah] Sapphire Avenue Exchange");
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 130, Position = new Vector3(300, 0, 300),
            AethernetShortcut = ["[Ul'dah] Aetheryte Plaza", "[Ul'dah] Sapphire Avenue Exchange"],
        });
        Ticks(ex, w, 10);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Aethernet"));
        Assert.Contains(w.Calls, c => c.StartsWith("Move 300,0,300"));
    }

    /// <summary>
    /// Ul'dah's Airship Landing has no shard — it joins the aethernet once the whole city is
    /// attuned. Before that the walk ended at the lift doors; now the lift is ridden up.
    /// </summary>
    [Fact]
    public void An_airship_landing_not_on_the_aethernet_is_reached_by_the_lift()
    {
        var w = new FakeStepWorld { TerritoryId = 130, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(-22, 10, -40);
        w.AethernetAccess[130] = new Vector3(-20, 10, -38);
        w.AethernetTerritories["[Ul'dah] Airship Landing"] = 130;
        w.UnattunedHops.Add("[Ul'dah] Airship Landing");
        w.Spawned.Add(1001834);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 130, Position = new Vector3(-23.6f, 83.2f, -2.3f),
            AethernetShortcut = ["[Ul'dah] Adventurers' Guild", "[Ul'dah] Airship Landing"],
        });

        for (var i = 0; i < 120 && ex.Status == StepStatus.Running; i++)
        {
            ex.Tick(); w.Advance(0.5);
            if (w.Calls.Contains("Interact 1001834") && !w.VisibleAddons.Contains("SelectIconString") && w.PlayerPosition.Y < 20)
            {
                w.VisibleAddons.Add("SelectIconString");
                w.IconEntries.Clear();
                w.IconEntries.AddRange(["Ride Lift to the Airship Landing", "Ride Lift to the Ruby Road Exchange", "Nothing"]);
            }
            if (w.Calls.Contains("IconSelect 0") && w.VisibleAddons.Remove("SelectIconString"))
                w.PlayerPosition = new Vector3(-26, 81.8f, -32);
        }
        Assert.Contains("IconSelect 0", w.Calls);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Aethernet"));
        Assert.Contains(w.Calls, c => c.StartsWith("Move -24,83,-2"));
    }

    [Fact]
    public void A_lift_menu_the_path_names_no_stop_for_takes_the_zone_it_crosses_into()
    {
        var w = new FakeStepWorld { TerritoryId = 130 };
        w.Spawned.Add(1004339);
        w.PlayerPosition = new Vector3(-25, 81.8f, -31);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.Interact, KindName = "Interact", DataId = 1004339, TerritoryId = 130,
            Position = new Vector3(-26, 81.8f, -32), TargetTerritoryId = 131,
        }, questId: 4063);
        Ticks(ex, w, 3);

        w.IsOccupied = true;
        w.VisibleAddons.Add("SelectIconString");
        w.IconEntries.AddRange(["Ride Lift to the Steps of Nald", "Ride Lift to the Ruby Road Exchange", "Nothing"]);
        ex.Tick();
        Assert.Contains("IconSelect 1", w.Calls);
    }

    /// <summary>
    /// The Crystarium's Cabinet of Curiosity: its map marker has no height, the guess put it on the
    /// level above, and the walk there was given up as no path with the shard right below. The real
    /// object, once in view, is where the character goes.
    /// </summary>
    [Fact]
    public void A_shard_on_another_level_than_its_guessed_height_is_still_reached()
    {
        const uint Cabinet = 155;
        var w = new FakeStepWorld { TerritoryId = 819, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(-52, 20, -173);
        w.Attunables[Cabinet] = ("The Cabinet of Curiosity", new Vector3(-54, -37, -241));
        w.MarkerHeight[Cabinet] = 20;
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AttuneAethernetShard, KindName = "AttuneAethernetShard", TerritoryId = 819, AttuneId = Cabinet });

        Ticks(ex, w, 60);
        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.Contains(Cabinet, w.AttunedIds);
        Assert.Contains(w.Calls, c => c.StartsWith("Move -54,-37,-241"));
    }

    /// <summary>
    /// A hunt mark wandered into the attune (Maultasche, A Still Tide): the attune kept walking to the
    /// aetheryte through the fight and ran its 90 seconds. Held instead, then attuned after.
    /// </summary>
    [Fact]
    public void A_fight_during_an_attune_is_waited_out()
    {
        var w = new FakeStepWorld { TerritoryId = 814, ArriveOnMove = true, InCombat = true };
        w.PlayerPosition = new Vector3(30, 0, -10);
        w.Attunables[Enclave] = ("Wright", new Vector3(42, 0, -15));
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AttuneAetheryte, KindName = "AttuneAetheryte", TerritoryId = 814, AttuneName = "Wright" });

        Ticks(ex, w, 400);   // over three minutes of fighting
        Assert.Equal(StepStatus.Running, ex.Status);
        Assert.DoesNotContain("InteractAetheryte", w.Calls);

        w.InCombat = false;
        Ticks(ex, w, 60);
        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.Contains(Enclave, w.AttunedIds);
    }

    /// <summary>Eulmore: the shard beside you first, not the aetheryte up the stairs that came first in the catalog.</summary>
    [Fact]
    public void The_button_takes_the_nearest_first()
    {
        var w = new FakeStepWorld { TerritoryId = 820, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(10, 40, 10);
        w.Attunables[134] = ("Eulmore", new Vector3(0, 82, 1));
        w.Attunables[157] = ("The Mainstay", new Vector3(12, 40, 14));
        var runner = new AttuneRunner(w, new StepExecutor(w), _ => { });

        Assert.True(runner.Start());
        for (var i = 0; i < 200 && !w.AttunedIds.Contains(157u) && !w.AttunedIds.Contains(134u); i++) { runner.Tick(); w.Advance(0.5); }
        Assert.Contains(157u, w.AttunedIds);
        Assert.DoesNotContain(134u, w.AttunedIds);
    }

    /// <summary>
    /// newtoon2 on Eulmore's bottom floor: the aetheryte, three floors up but near the middle across the
    /// ground, came out "nearest" and was climbed to first. Shards first, the aetheryte last.
    /// </summary>
    [Fact]
    public void Shards_come_before_the_zones_aetheryte()
    {
        var w = new FakeStepWorld { TerritoryId = 820, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(5, 0, 5);
        w.Attunables[134] = ("Eulmore", new Vector3(0, 82, 1));
        w.Attunables[135] = ("Southeast Derelicts", new Vector3(60, 0, 60));
        w.ShardIds.Add(135);
        var runner = new AttuneRunner(w, new StepExecutor(w), _ => { });

        Assert.True(runner.Start());
        for (var i = 0; i < 200 && !w.AttunedIds.Contains(135u) && !w.AttunedIds.Contains(134u); i++) { runner.Tick(); w.Advance(0.5); }
        Assert.Contains(135u, w.AttunedIds);
        Assert.DoesNotContain(134u, w.AttunedIds);
    }

    /// <summary>
    /// Eulmore's stairs wind away from a shard before reaching it: walking the whole time, the straight-
    /// line distance did not drop, and two reachable shards were given up on newtoon2 — and remembered,
    /// so the button called the zone done. Walking is progress; an unreachable one is not remembered.
    /// </summary>
    [Fact]
    public void Walking_the_long_way_round_is_not_giving_up()
    {
        var w = new FakeStepWorld { TerritoryId = 820 };
        w.PlayerPosition = new Vector3(0, 0, 0);
        w.Attunables[135] = ("Southeast Derelicts", new Vector3(20, 0, 20));
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AttuneAethernetShard, KindName = "AttuneAethernetShard", TerritoryId = 820, AttuneId = 135 });
        for (var i = 0; i < 60; i++)   // thirty seconds walking a circle round it, never nearer
        {
            ex.Tick(); w.Advance(0.5);
            var a = i * 0.2f;
            w.PlayerPosition = new Vector3(20 - 28.3f * MathF.Cos(a), 0, 20 - 28.3f * MathF.Sin(a));
        }
        Assert.Equal(StepStatus.Running, ex.Status);
        Assert.DoesNotContain(135u, w.RefusedAttunes);
    }

    /// <summary>The game says why on the first press — "special permission is required" — and that is the refusal.</summary>
    [Fact]
    public void The_games_refusal_message_ends_the_attune_at_once()
    {
        var w = new FakeStepWorld { TerritoryId = 820, ArriveOnMove = true, InteractAttunes = false };
        w.PlayerPosition = new Vector3(1, 82, 3);
        w.Attunables[134] = ("Eulmore", new Vector3(0, 82, 1));
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AttuneAetheryte, KindName = "AttuneAetheryte", TerritoryId = 820, AttuneId = 134 });
        for (var i = 0; i < 40 && !w.Calls.Contains("InteractAetheryte"); i++) { ex.Tick(); w.Advance(0.5); }
        w.LastAttuneRefusal = w.UtcNow;   // the game's reply to that first press
        ex.Tick(); w.Advance(0.5); ex.Tick();

        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Single(w.Calls, c => c == "InteractAetheryte");
        Assert.Contains(134u, w.RefusedAttunes);
    }

    /// <summary>Somewhere the story has not opened: no closer in twenty seconds is said, not paced for ninety.</summary>
    [Fact]
    public void An_attune_that_gets_no_closer_gives_up_early()
    {
        var w = new FakeStepWorld { TerritoryId = 820 };
        w.PlayerPosition = new Vector3(10, 40, 10);
        w.Attunables[134] = ("Eulmore", new Vector3(0, 82, 1));
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AttuneAetheryte, KindName = "AttuneAetheryte", TerritoryId = 820, AttuneId = 134 });
        Ticks(ex, w, 60);   // thirty seconds
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("cannot get to Eulmore", ex.FailReason);
    }

    /// <summary>
    /// Eulmore: the Mainstay shard sits right under the city aetheryte. Standing by the aetheryte,
    /// the attune matched the shard below across the ground and ran back down the stairs.
    /// </summary>
    [Fact]
    public void The_aetheryte_itself_is_found_not_the_shard_beneath_it()
    {
        var w = new FakeStepWorld { TerritoryId = 820, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(-6.8f, 83.1f, -37.2f);
        w.Attunables[157] = ("The Mainstay", new Vector3(0.5f, 48f, 1f));
        w.AttunedIds.Add(157);
        w.Attunables[134] = ("Eulmore", new Vector3(0, 82, 1));
        w.MarkerHeight[134] = 48;   // the guessed height lands on the floor below
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AttuneAetheryte, KindName = "AttuneAetheryte", TerritoryId = 820, AttuneId = 134 });

        Ticks(ex, w, 30);
        Assert.Contains(134u, w.AttunedIds);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Move 0,48") || c.StartsWith("Move 1,48"));
    }

    /// <summary>
    /// Eulmore's aetheryte is story-locked early in Shadowbringers: pressed at arm's length, nothing
    /// attunes. Five presses, said, and remembered — the button's count and attune-in-passing skip it.
    /// </summary>
    [Fact]
    public void A_story_locked_aetheryte_is_given_up_and_remembered()
    {
        var w = new FakeStepWorld { TerritoryId = 820, ArriveOnMove = true, InteractAttunes = false };
        w.PlayerPosition = new Vector3(1, 82, 3);
        w.Attunables[134] = ("Eulmore", new Vector3(0, 82, 1));
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AttuneAetheryte, KindName = "AttuneAetheryte", TerritoryId = 820, AttuneId = 134 });
        Ticks(ex, w, 40);   // twenty seconds

        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("will not attune yet", ex.FailReason);
        Assert.Contains(134u, w.RefusedAttunes);
        Assert.Empty(w.UnattunedHere());
    }

    [Fact]
    public void The_button_attunes_everything_in_the_zone_and_names_what_it_missed()
    {
        var w = new FakeStepWorld { TerritoryId = 759, ArriveOnMove = true };
        w.PlayerPosition = new Vector3(0, 0, 0);
        w.Attunables[127] = ("The Doman Enclave", new Vector3(42, 0, -15));
        w.Attunables[129] = ("The Northern Enclave", new Vector3(-62, 0, 90));
        var runner = new AttuneRunner(w, new StepExecutor(w), _ => { });

        Assert.True(runner.Start());
        for (var i = 0; i < 200 && runner.Running; i++) { runner.Tick(); w.Advance(0.5); }
        Assert.False(runner.Running);
        Assert.Equal(2, runner.Done);
        Assert.Contains(127u, w.AttunedIds);
        Assert.Contains(129u, w.AttunedIds);
    }
}
