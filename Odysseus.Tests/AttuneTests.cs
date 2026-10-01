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
