using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>
/// The two verbs a crafter or gatherer class chain is built out of, and the bill of materials the
/// chain asks you to bring.
/// </summary>
public class CraftGatherStepTests
{
    private const uint Ingot = 5056;
    private const uint Ore = 5106;
    private const uint QuestOnly = 2001388;

    private static StepStatus Run(StepExecutor ex, FakeStepWorld world, int maxTicks = 400, double secondsPerTick = 0.5)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            var s = ex.Tick();
            if (s != StepStatus.Running) return s;
            world.Advance(secondsPerTick);
        }
        return ex.Status;
    }

    private static QuestStep Craft(uint itemId, int count) => new()
    {
        Kind = StepKind.Craft, KindName = "Craft", TerritoryId = 400, ItemId = itemId, ItemCount = count,
    };

    private static QuestStep Gather(params GatherTarget[] targets) => new()
    {
        Kind = StepKind.Gather, KindName = "Gather", TerritoryId = 400, GatherItems = [.. targets],
    };

    // ── Importer ──

    [Fact]
    public void Importer_keeps_what_a_gather_step_wants()
    {
        const string json = """
            { "QuestSequence": [ { "Sequence": 1, "Steps": [
              { "TerritoryId": 154, "InteractionType": "Gather",
                "ItemsToGather": [ { "ItemId": 2001388, "ItemCount": 15 }, { "ItemId": 5106, "ItemCount": 5 } ] },
              { "TerritoryId": 128, "InteractionType": "Craft", "ItemId": 5056, "ItemCount": 3 } ] } ] }
            """;
        var path = QuestionableImporter.Parse("1_x.json", "QuestPaths/x", json, out var unknown)!;
        Assert.Equal(0, unknown);

        var gather = path.Block(1)!.Steps[0];
        Assert.Equal(StepKind.Gather, gather.Kind);
        Assert.Equal(2, gather.GatherItems!.Count);
        Assert.Equal(15, gather.GatherItems[0].ItemCount);
        Assert.True(gather.GatherItems[0].IsEventItem);      // 2001388 — a quest-only item
        Assert.False(gather.GatherItems[1].IsEventItem);     // 5106 Copper Ore — an ordinary one

        Assert.Equal(3, path.Block(1)!.Steps[1].ItemCount);
    }

    // ── Craft ──

    [Fact]
    public void Craft_asks_Artisan_for_the_shortfall_and_finishes_when_the_bag_is_covered()
    {
        var world = new FakeStepWorld();
        world.Bag[Ingot] = 1;
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 3));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains($"Craft 2 x {Ingot}", world.Calls);
        Assert.Equal(3, world.Bag[Ingot]);
    }

    [Fact]
    public void Craft_makes_nothing_when_the_bag_already_holds_enough()
    {
        var world = new FakeStepWorld();
        world.Bag[Ingot] = 5;
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 3));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Craft "));
    }

    /// <summary>
    /// Artisan stopping short means the materials ran out. Saying which ones is the whole value of
    /// the stop — otherwise you are left staring at an idle crafting log.
    /// </summary>
    [Fact]
    public void Craft_that_stops_short_names_the_missing_ingredients()
    {
        var world = new FakeStepWorld { CraftDelivers = 1, CraftShortfallText = "4 × Copper Ore, 2 × Fire Shard" };
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 3));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("Artisan stopped", ex.FailReason);
        Assert.Contains("4 × Copper Ore, 2 × Fire Shard", ex.FailReason);
    }

    [Fact]
    public void Craft_without_Artisan_stops_and_says_so()
    {
        var world = new FakeStepWorld { CrafterReady = false };
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 1));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("Artisan is not loaded", ex.FailReason);
    }

    [Fact]
    public void Craft_of_something_with_no_recipe_stops()
    {
        var world = new FakeStepWorld { CraftJob = null };
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 1));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("no recipe", ex.FailReason);
    }

    /// <summary>Cancelling mid-craft has to switch Artisan off, or its loop outlives the run.</summary>
    [Fact]
    public void Cancelling_a_craft_stops_Artisan()
    {
        var world = new FakeStepWorld { CraftDelivers = 0, CraftKeepsRunning = true };
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 3));
        ex.Tick();
        ex.Tick();
        Assert.Contains($"Craft 3 x {Ingot}", world.Calls);
        Assert.Equal(StepStatus.Running, ex.Status);

        ex.Cancel();
        Assert.Contains("StopCraft", world.Calls);
    }

    // ── Gather ──

    [Fact]
    public void Gather_switches_GatherBuddy_on_and_off_around_the_bag_filling()
    {
        var world = new FakeStepWorld();
        world.GatherDelivers[Ore] = 5;
        var ex = new StepExecutor(world);
        ex.Begin(Gather(new GatherTarget(Ore, 5)));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("StartGather", world.Calls);
        Assert.Contains("StopGather", world.Calls);
    }

    [Fact]
    public void Gather_asks_for_nothing_when_every_target_is_already_held()
    {
        var world = new FakeStepWorld();
        world.Bag[Ore] = 9;
        var ex = new StepExecutor(world);
        ex.Begin(Gather(new GatherTarget(Ore, 5)));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain("StartGather", world.Calls);
        Assert.DoesNotContain("StopGather", world.Calls);   // never switched on, so never switched off
    }

    /// <summary>
    /// A quest-only gathering item exists inside the quest and nowhere else — not in the Item
    /// sheet, not in a bag count, not on any auto-gather list. Handing it off would spin forever.
    /// </summary>
    [Fact]
    public void A_quest_only_gathering_item_stops_immediately_rather_than_being_handed_off()
    {
        var world = new FakeStepWorld();
        var ex = new StepExecutor(world);
        ex.Begin(Gather(new GatherTarget(QuestOnly, 15)));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("quest-only gathering item", ex.FailReason);
        Assert.DoesNotContain("StartGather", world.Calls);
    }

    [Fact]
    public void Gather_that_goes_idle_stops_with_GatherBuddys_own_reason()
    {
        var world = new FakeStepWorld { GathererIdle = true, GathererStatus = "no nodes for this item" };
        var ex = new StepExecutor(world);
        ex.Begin(Gather(new GatherTarget(Ore, 5)));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("idle", ex.FailReason);
        Assert.Contains("no nodes for this item", ex.FailReason);
        Assert.Contains("StopGather", world.Calls);
    }

    [Fact]
    public void Gather_without_GatherBuddy_stops_and_says_so()
    {
        var world = new FakeStepWorld { GathererReady = false };
        var ex = new StepExecutor(world);
        ex.Begin(Gather(new GatherTarget(Ore, 5)));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("GatherBuddy is not loaded", ex.FailReason);
    }

    // ── The bill of materials ──

    private static QuestPath Chain() => new()
    {
        QuestId = 292, Name = "My First Cross-pein Hammer",
        Sequences =
        [
            new QuestSequence
            {
                Sequence = 1,
                Steps =
                [
                    new QuestStep { Kind = StepKind.PurchaseItem, ItemId = Ore, ItemCount = 30, DataId = 1000718 },
                    new QuestStep { Kind = StepKind.Craft, ItemId = Ingot, ItemCount = 3 },
                    new QuestStep { Kind = StepKind.Gather, GatherItems = [new GatherTarget(4839, 6)] },
                    new QuestStep { Kind = StepKind.Gather, GatherItems = [new GatherTarget(QuestOnly, 15)] },
                ],
            },
        ],
    };

    private static IReadOnlyList<MaterialNeed> Bill(
        Dictionary<uint, int> bag, Dictionary<uint, int>? chest = null, ChainMaterials.ExpandCraft? expand = null)
        => ChainMaterials.For([Chain()],
            id => $"item {id}",
            id => bag.GetValueOrDefault(id),
            id => (chest ?? []).GetValueOrDefault(id),
            expand);

    [Fact]
    public void The_bill_names_every_source_a_chain_draws_on()
    {
        var bill = Bill([]);
        Assert.Equal(MaterialSource.Vendor, bill.Single(n => n.ItemId == Ore).Source);
        Assert.Equal(MaterialSource.Crafted, bill.Single(n => n.ItemId == Ingot).Source);
        Assert.Equal(MaterialSource.Gathered, bill.Single(n => n.ItemId == 4839).Source);
        Assert.Equal(MaterialSource.QuestItem, bill.Single(n => n.ItemId == QuestOnly).Source);
    }

    /// <summary>A quest item cannot be counted at all, so it must not read as "you have none of 15".</summary>
    [Fact]
    public void A_quest_item_reports_an_unknown_count_rather_than_zero()
    {
        var line = Bill([]).Single(n => n.ItemId == QuestOnly);
        Assert.Equal(-1, line.Held);
        Assert.Equal(15, line.Missing);
    }

    [Fact]
    public void What_is_held_is_subtracted_and_sinks_below_what_is_missing()
    {
        var bill = Bill(new Dictionary<uint, int> { [Ore] = 30, [Ingot] = 3 });
        Assert.Equal(0, bill.Single(n => n.ItemId == Ore).Missing);
        Assert.Equal(0, bill.Single(n => n.ItemId == Ingot).Missing);
        // Missing lines sort first, so the two covered ones are at the end.
        Assert.All(bill.Take(2), n => Assert.True(n.Missing > 0));
    }

    /// <summary>The whole point of reading the chest: stop and fetch rather than craft it again.</summary>
    [Fact]
    public void A_shortfall_the_chest_covers_is_flagged()
    {
        var bill = Bill([], new Dictionary<uint, int> { [Ore] = 40 });
        var ore = bill.Single(n => n.ItemId == Ore);
        Assert.Equal(30, ore.Missing);
        Assert.True(ore.CoveredByChest);
        Assert.False(bill.Single(n => n.ItemId == Ingot).CoveredByChest);
    }

    /// <summary>Ingredients are for what still has to be made, not for what is already in the bag.</summary>
    [Fact]
    public void Crafts_expand_into_ingredients_for_the_shortfall_only()
    {
        var asked = new List<(uint, int)>();
        ChainMaterials.ExpandCraft expand = (item, count) =>
        {
            asked.Add((item, count));
            return [(9001u, "Fire Shard", count * 2)];
        };

        var bill = Bill(new Dictionary<uint, int> { [Ingot] = 1 }, chest: null, expand: expand);
        Assert.Equal([(Ingot, 2)], asked);
        var shard = bill.Single(n => n.ItemId == 9001);
        Assert.Equal(4, shard.Needed);
        Assert.Equal(MaterialSource.Ingredient, shard.Source);
    }

    /// <summary>Counts are target totals, so the same requirement in two quests is not doubled.</summary>
    [Fact]
    public void The_same_item_wanted_twice_takes_the_larger_total()
    {
        var twice = ChainMaterials.For([Chain(), Chain()], id => $"item {id}", _ => 0, _ => 0);
        Assert.Equal(30, twice.Single(n => n.ItemId == Ore).Needed);
    }

    /// <summary>
    /// One quest's list is read as instructions, so it keeps the order the steps want things in.
    /// A whole line's is read as a shopping list and puts what is missing first — the two orders
    /// are opposite on purpose.
    /// </summary>
    [Fact]
    public void A_single_quests_list_keeps_step_order_while_a_lines_leads_with_what_is_missing()
    {
        var bag = new Dictionary<uint, int> { [Ore] = 30 };   // the first step's item is covered

        var stepOrder = ChainMaterials.For([Chain()], id => $"item {id}",
            id => bag.GetValueOrDefault(id), _ => 0, expand: null, inStepOrder: true);
        Assert.Equal([Ore, Ingot, 4839u, QuestOnly], stepOrder.Select(n => n.ItemId));

        var shoppingList = ChainMaterials.For([Chain()], id => $"item {id}",
            id => bag.GetValueOrDefault(id), _ => 0);
        Assert.Equal(0, shoppingList.Last().Missing);        // the covered one sinks to the bottom
        Assert.All(shoppingList.Take(3), n => Assert.True(n.Missing > 0));
    }

    /// <summary>
    /// A path converted before a verb was named stores it as Unknown, so the step is unrunnable for
    /// a reason that has nothing to do with the feature. Saying "not implemented yet" there sends
    /// you looking for something that is already there.
    /// </summary>
    [Fact]
    public void A_step_left_Unknown_by_an_old_converter_asks_for_a_re_import_not_a_feature()
    {
        var stale = new QuestStep { Kind = StepKind.Unknown, KindName = "Craft", TerritoryId = 400 };
        Assert.Contains("re-import", StepExecutor.WhyUnsupported(stale));
        Assert.DoesNotContain("not implemented", StepExecutor.WhyUnsupported(stale));

        // A verb we genuinely cannot run still says so.
        var genuinely = new QuestStep { Kind = StepKind.Unknown, KindName = "EquipItem", TerritoryId = 400 };
        Assert.Contains("not implemented", StepExecutor.WhyUnsupported(genuinely));

        // And one nobody has ever seen keeps its name.
        var novel = new QuestStep { Kind = StepKind.Unknown, KindName = "SomethingNew", TerritoryId = 400 };
        Assert.Contains("SomethingNew", StepExecutor.WhyUnsupported(novel));
    }

    /// <summary>
    /// Only paths a re-import would actually improve. "Child Labor" (2813) is the real case: it is
    /// version 1, it was dropped from the bundle upstream so no import will ever touch it again,
    /// and it is nothing but Interact steps — flagging it forever would be a warning nobody could act on.
    /// </summary>
    [Fact]
    public void Only_a_path_a_re_import_would_improve_counts_as_outdated()
    {
        QuestPath Old(params QuestStep[] steps) => new()
        {
            FormatVersion = 1, QuestId = 1, Name = "x",
            Sequences = [new QuestSequence { Sequence = 0, Steps = [.. steps] }],
        };

        // Parses the same under every converter so far — its version is history, not a defect.
        Assert.False(Old(new QuestStep { Kind = StepKind.Interact, DataId = 5 }).NeedsReconvert);

        // A verb the old converter did not know, kept by name.
        Assert.True(Old(new QuestStep { Kind = StepKind.Unknown, KindName = "Craft" }).NeedsReconvert);

        // A kind that has since gained fields — a v2 Gather has no GatherItems.
        Assert.True(Old(new QuestStep { Kind = StepKind.Gather }).NeedsReconvert);

        // A verb nobody has ever named is not something re-converting would fix.
        Assert.False(Old(new QuestStep { Kind = StepKind.Unknown, KindName = "SomethingNew" }).NeedsReconvert);

        // And current is current.
        Assert.False(new QuestPath { QuestId = 1, Sequences = [] }.NeedsReconvert);
    }

    [Fact]
    public void Only_a_quest_that_wants_something_offers_a_list()
    {
        Assert.True(ChainMaterials.NamesItems(Chain()));
        Assert.False(ChainMaterials.NamesItems(new QuestPath
        {
            QuestId = 1, Name = "Talk to someone",
            Sequences = [new QuestSequence { Sequence = 1, Steps = [new QuestStep { Kind = StepKind.Interact, DataId = 5 }] }],
        }));
    }
}
