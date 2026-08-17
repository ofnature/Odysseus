using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>
/// The two verbs the allied-society dailies stall on — buy this, then be this class — and the
/// hand-over window a quest that wants items back puts up.
/// </summary>
public class ShopAndClassStepTests
{
    private const uint Vendor = 1000718;
    private const uint Shop = 262151;
    private const uint MothPupa = 2586;

    private static StepStatus Run(StepExecutor ex, FakeStepWorld world, int maxTicks = 300, double secondsPerTick = 0.5)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            var s = ex.Tick();
            if (s != StepStatus.Running) return s;
            world.Advance(secondsPerTick);
        }
        return ex.Status;
    }

    private static QuestStep Purchase(int count = 3, uint? shopId = Shop) => new()
    {
        Kind = StepKind.PurchaseItem, KindName = "PurchaseItem", TerritoryId = 400,
        DataId = Vendor, ItemId = MothPupa, ItemCount = count,
        PurchaseShopId = shopId, PurchaseShopSheet = shopId is null ? null : "GilShop",
    };

    private static QuestStep Switch(string target) => new()
    {
        Kind = StepKind.SwitchClass, KindName = "SwitchClass", TerritoryId = 400, TargetClass = target,
    };

    // ── Importer ──

    [Fact]
    public void Importer_keeps_the_purchase_menu_and_the_target_class()
    {
        const string json = """
            { "QuestSequence": [ { "Sequence": 1, "Steps": [
              { "DataId": 1000718, "Position": { "X": 1, "Y": 2, "Z": 3 }, "TerritoryId": 154, "InteractionType": "PurchaseItem",
                "PurchaseMenu": { "ExcelSheet": "GilShop", "Key": 262151 }, "ItemId": 2586, "ItemCount": 99,
                "SkipConditions": { "StepIf": { "Item": { "NotInInventory": false } } } },
              { "TerritoryId": 154, "InteractionType": "SwitchClass", "TargetClass": "ConfiguredCombatJob" },
              { "DataId": 1002393, "TerritoryId": 132, "InteractionType": "PurchaseItem", "ItemId": 6018, "ItemCount": 1 } ] } ] }
            """;
        var path = QuestionableImporter.Parse("1_x.json", "QuestPaths/x", json, out var unknown)!;
        Assert.Equal(0, unknown);

        var buy = path.Block(1)!.Steps[0];
        Assert.Equal(StepKind.PurchaseItem, buy.Kind);
        Assert.Equal(Shop, buy.PurchaseShopId);
        Assert.Equal("GilShop", buy.PurchaseShopSheet);
        Assert.Equal(99, buy.ItemCount);

        Assert.Equal("ConfiguredCombatJob", path.Block(1)!.Steps[1].TargetClass);

        // An NPC whose only purpose is the shop names no menu; that is not a parse failure.
        var chocobo = path.Block(1)!.Steps[2];
        Assert.Null(chocobo.PurchaseShopId);
        Assert.Null(chocobo.PurchaseShopSheet);
    }

    // ── PurchaseItem ──

    [Fact]
    public void Purchase_opens_the_shop_buys_the_shortfall_and_closes()
    {
        var world = new FakeStepWorld();
        world.Bag[MothPupa] = 1;
        var ex = new StepExecutor(world);
        ex.Begin(Purchase(count: 3));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains($"OpenShop {Vendor}/{Shop}", world.Calls);
        Assert.Contains($"Buy 2 x {MothPupa} from {Shop}", world.Calls);
        Assert.Contains("CloseShop", world.Calls);
        Assert.Equal(3, world.Bag[MothPupa]);
    }

    /// <summary>
    /// The count is a target total, which is what makes the step safe to replay: the data's own
    /// skip clause reads it that way, and a run resumed after a restart must not re-buy 99 of
    /// something already in the bag.
    /// </summary>
    [Fact]
    public void Purchase_buys_nothing_when_the_bag_already_holds_enough()
    {
        var world = new FakeStepWorld();
        world.Bag[MothPupa] = 5;
        var ex = new StepExecutor(world);
        ex.Begin(Purchase(count: 3));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("OpenShop"));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Buy"));
    }

    /// <summary>A vendor with a single shop is opened by interacting; the window says which shop it was.</summary>
    [Fact]
    public void Purchase_without_a_named_shop_learns_the_id_from_the_window()
    {
        var world = new FakeStepWorld { ShopOpensAs = 262999 };
        var ex = new StepExecutor(world);
        ex.Begin(Purchase(count: 1, shopId: null));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains($"OpenShop {Vendor}/0", world.Calls);
        Assert.Contains($"Buy 1 x {MothPupa} from 262999", world.Calls);
    }

    /// <summary>A short buy converges by re-reading the bag rather than trusting one order.</summary>
    [Fact]
    public void Purchase_buys_again_when_the_first_order_arrives_short()
    {
        var world = new FakeStepWorld { BuyDelivers = 1 };
        var ex = new StepExecutor(world);
        ex.Begin(Purchase(count: 3));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Equal(3, world.Bag[MothPupa]);
        Assert.Equal(3, world.Calls.Count(c => c.StartsWith("Buy ")));
    }

    /// <summary>A shop that takes every order and delivers nothing: the watchdog is the only way out.</summary>
    [Fact]
    public void Purchase_that_never_fills_the_bag_says_gil_or_stock()
    {
        var world = new FakeStepWorld { BuyDelivers = 0, Gil = 12 };
        var ex = new StepExecutor(world);
        ex.Begin(Purchase(count: 3));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("out of gil", ex.FailReason);
        Assert.Contains("still 0 of 3", ex.FailReason);
    }

    [Fact]
    public void Purchase_from_a_shop_that_does_not_stock_it_stops_at_once()
    {
        var world = new FakeStepWorld { BuyAccepted = false };
        var ex = new StepExecutor(world);
        ex.Begin(Purchase(count: 1));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("does not stock", ex.FailReason);
        Assert.Contains("CloseShop", world.Calls);
    }

    /// <summary>Only gil shops are handled; anything else is named rather than bought out of blind.</summary>
    [Fact]
    public void Purchase_from_an_unhandled_shop_kind_says_which()
    {
        var world = new FakeStepWorld();
        var step = Purchase(count: 1);
        step.PurchaseShopSheet = "SpecialShop";
        var ex = new StepExecutor(world);
        ex.Begin(step);

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("SpecialShop", ex.FailReason);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("OpenShop"));
    }

    // ── SwitchClass ──

    [Fact]
    public void SwitchClass_equips_the_gearset_for_a_named_class()
    {
        var world = new FakeStepWorld { CurrentClassJob = 24 };
        world.ClassJobs["Fisher"] = 18;
        world.SavedGearsets.Add(new GearsetInfo(4, 18, 0, 70, JobKind.Gatherer));
        var ex = new StepExecutor(world);
        ex.Begin(Switch("Fisher"));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Gearset 4", world.Calls);
        Assert.Equal(18u, world.CurrentClassJob);
    }

    /// <summary>
    /// A job satisfies the class it grew out of. Past level 30 the character has no Conjurer
    /// gearset at all, only White Mage — treating that as "no gearset" would stop every ARR class
    /// quest that asks for the class by its old name.
    /// </summary>
    [Fact]
    public void SwitchClass_takes_the_job_gearset_for_a_class_name()
    {
        var world = new FakeStepWorld { CurrentClassJob = 1 };
        world.ClassJobs["Conjurer"] = 6;
        world.SavedGearsets.Add(new GearsetInfo(2, 24, ParentClassJobId: 6, Level: 90, JobKind.Combat));
        var ex = new StepExecutor(world);
        ex.Begin(Switch("Conjurer"));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Gearset 2", world.Calls);
    }

    [Fact]
    public void SwitchClass_does_nothing_when_already_on_the_class()
    {
        var world = new FakeStepWorld { CurrentClassJob = 24 };
        world.ClassJobs["Conjurer"] = 6;
        world.SavedGearsets.Add(new GearsetInfo(2, 24, ParentClassJobId: 6, Level: 90, JobKind.Combat));
        var ex = new StepExecutor(world);
        ex.Begin(Switch("Conjurer"));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Gearset"));
    }

    [Fact]
    public void ConfiguredCombatJob_takes_the_highest_combat_gearset()
    {
        var world = new FakeStepWorld { CurrentClassJob = 18 };
        world.SavedGearsets.Add(new GearsetInfo(0, 21, 3, 62, JobKind.Combat));
        world.SavedGearsets.Add(new GearsetInfo(1, 24, 6, 90, JobKind.Combat));
        world.SavedGearsets.Add(new GearsetInfo(2, 8, 0, 100, JobKind.Crafter));
        var ex = new StepExecutor(world);
        ex.Begin(Switch("ConfiguredCombatJob"));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Gearset 1", world.Calls);
        Assert.Equal(24u, world.CurrentClassJob);
    }

    [Fact]
    public void QuestStartJob_switches_back_to_what_the_quest_was_taken_on()
    {
        var world = new FakeStepWorld { CurrentClassJob = 18 };
        world.QuestStartJobs[1494] = 24;
        world.SavedGearsets.Add(new GearsetInfo(1, 24, 6, 90, JobKind.Combat));
        var ex = new StepExecutor(world);
        ex.Begin(Switch("QuestStartJob"), questId: 1494);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Gearset 1", world.Calls);
    }

    [Fact]
    public void SwitchClass_with_no_gearset_stops_and_says_so()
    {
        var world = new FakeStepWorld { CurrentClassJob = 24 };
        world.ClassJobs["Fisher"] = 18;
        var ex = new StepExecutor(world);
        ex.Begin(Switch("Fisher"));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("no gearset for Fisher", ex.FailReason);
    }

    [Fact]
    public void SwitchClass_to_a_class_the_sheet_does_not_know_says_so()
    {
        var world = new FakeStepWorld { CurrentClassJob = 24 };
        var ex = new StepExecutor(world);
        ex.Begin(Switch("Blue Mage"));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("unknown class \"Blue Mage\"", ex.FailReason);
    }

    [Fact]
    public void SwitchClass_that_never_lands_faults_rather_than_hanging()
    {
        var world = new FakeStepWorld { CurrentClassJob = 18, EquipLands = false };
        world.ClassJobs["Culinarian"] = 15;
        world.SavedGearsets.Add(new GearsetInfo(3, 15, 0, 90, JobKind.Crafter));
        var ex = new StepExecutor(world);
        ex.Begin(Switch("Culinarian"));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("did not change to Culinarian", ex.FailReason);
    }

    // ── The Request window ──

    /// <summary>
    /// The chest is not the bags: a hand-in cannot be met out of it and a purchase step must not
    /// skip itself because the item is sitting there. It only ever changes what a stop says.
    /// </summary>
    [Fact]
    public void An_item_in_the_FC_chest_is_not_held_but_is_named_when_the_hand_in_fails()
    {
        var (ex, world) = AtHandOver(quantity: 3, satisfiable: false);
        world.FcChest[MothPupa] = 3;

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("3 × Moth Pupa (3 in the FC chest)", ex.FailReason);
        Assert.Equal(0, world.ItemCount(MothPupa));
    }

    [Fact]
    public void A_purchase_step_does_not_skip_because_the_chest_holds_it()
    {
        var world = new FakeStepWorld();
        world.FcChest[MothPupa] = 99;
        var ex = new StepExecutor(world);
        ex.Begin(Purchase(count: 3));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains($"Buy 3 x {MothPupa} from {Shop}", world.Calls);
    }

    /// <summary>Interact with an NPC, then have the hand-over window open on the conversation.</summary>
    private static (StepExecutor Executor, FakeStepWorld World) AtHandOver(int quantity, bool satisfiable)
    {
        var world = new FakeStepWorld { CanSatisfyHandOver = satisfiable };
        world.Spawned.Add(1001);
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", TerritoryId = 400, DataId = 1001 });
        ex.Tick();  // arrived → interact
        ex.Tick();  // interacted → dialogue
        world.IsOccupied = true;
        world.VisibleAddons.Add("Request");
        world.Requests.Add(new HandOverRequest(MothPupa, "Moth Pupa", quantity));
        return (ex, world);
    }

    [Fact]
    public void An_interact_that_opens_the_hand_over_window_fills_it_and_finishes()
    {
        var (ex, world) = AtHandOver(quantity: 3, satisfiable: true);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("HandOver", world.Calls);
    }

    /// <summary>
    /// The one thing worth failing fast on. Before this the run sat in the dialogue watchdog for
    /// two minutes and then said "dialogue never ended", which names neither the item nor the
    /// shortfall.
    /// </summary>
    [Fact]
    public void A_hand_over_the_bags_cannot_cover_stops_with_what_it_wanted()
    {
        var (ex, world) = AtHandOver(quantity: 3, satisfiable: false);

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("3 × Moth Pupa", ex.FailReason);
        Assert.DoesNotContain("HandOver", world.Calls);
    }

    /// <summary>TextAdvance gets the first go, exactly as it does at the reward window.</summary>
    [Fact]
    public void The_hand_over_window_is_left_alone_for_a_moment_first()
    {
        var (ex, world) = AtHandOver(quantity: 1, satisfiable: true);

        for (var i = 0; i < 4; i++) { ex.Tick(); world.Advance(0.5); } // ~2s
        Assert.DoesNotContain("HandOver", world.Calls);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("HandOver", world.Calls);
    }
}
