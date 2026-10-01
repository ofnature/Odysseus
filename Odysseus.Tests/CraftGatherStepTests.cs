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

    // ── A Craft step that names no item ──

    /// <summary>
    /// My First Saw (205) and nine others: upstream's Craft step names no item. The quest's own
    /// hand-in items say what to make — every one of them not already in the bag, in turn.
    /// </summary>
    [Fact]
    public void A_craft_step_naming_no_item_makes_what_the_quest_hands_in()
    {
        var world = new FakeStepWorld();
        world.HandIns[2045] = [(12849, 1, false), (12862, 1, false), (12855, 1, false)];   // Kaiser Roll, Beet Soup, Grilled Sweetfish
        world.Craftable.UnionWith([12849u, 12862u, 12855u]);
        world.Bag[12862] = 1;                           // one already made
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Craft, KindName = "Craft", TerritoryId = 400 }, questId: 2045);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Craft 1 x 12849", world.Calls);
        Assert.Contains("Craft 1 x 12855", world.Calls);
        Assert.DoesNotContain("Craft 1 x 12862", world.Calls);
    }

    /// <summary>To Be the Wood (139) takes three shields: making one left the turn-in at 2/3.</summary>
    [Fact]
    public void A_craft_step_naming_no_item_makes_as_many_as_the_quest_takes()
    {
        var world = new FakeStepWorld();
        world.HandIns[139] = [(2219, 3, false)];
        world.Craftable.Add(2219);
        world.Bag[2219] = 2;
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Craft, KindName = "Craft", TerritoryId = 400 }, questId: 139);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Craft 1 x 2219", world.Calls);
        Assert.Equal(3, world.Bag[2219]);
    }

    [Theory]
    [InlineData("Crafted Item:\n 3x Square Maple Shield\nAutocraft requires:\n 3x Maple Lumber,\n 6x Bronze Rivets", "Square Maple Shield", 3)]
    [InlineData("Crafted Item:\n 12x Ash Lumber\nAutocraft requires:\n 36x Ash Log", "Ash Lumber", 12)]
    [InlineData("Crafted Item:\n 1x Walnut Lumber HQ\nAutocraft requires:\n 3x Walnut Log", "Walnut Lumber", 1)]
    [InlineData("Crafted Item:\n1x Beet Soup HQ\n1x Kaiser Roll HQ\n1x Grilled Sweetfish HQ\nAutocrafting requires:\n1x Abalathian Rock Salt", "Kaiser Roll", 1)]
    [InlineData("!!Requires Manual Melding!!\nCrafts: 1x Iron Lance\nRequires: 1x * Materia I (not II+)", "Iron Lance", 1)]
    public void The_path_note_says_how_many_the_quest_takes(string note, string item, int count)
    {
        var counts = CraftNote.Read(note);
        Assert.Equal(count, counts[item].Count);
        Assert.DoesNotContain("Maple Lumber", counts.Keys);   // materials are not what the quest takes
        Assert.DoesNotContain("Ash Log", counts.Keys);
    }

    [Theory]
    [InlineData("Crafted Item:\n 1x Walnut Lumber HQ\nAutocraft requires:\n 3x Walnut Log", "Walnut Lumber", true)]
    [InlineData("Crafted Item:\n1x Sohm Al tart\n1x Ishgardian tea HQ\nAutocrafting requires:", "Sohm Al tart", false)]
    [InlineData("Crafted Item:\n1x Sohm Al tart\n1x Ishgardian tea HQ\nAutocrafting requires:", "Ishgardian tea", true)]
    [InlineData("Crafted Item:\n 3x Hi-Potion of Strength HQ// Autocraft requires:\n 1x Rock Salt,", "Hi-Potion of Strength", true)]
    [InlineData("Crafted Item:\n 3x Square Maple Shield\nAutocraft requires:\n 3x Maple Lumber", "Square Maple Shield", false)]
    public void The_path_note_says_which_items_must_be_HQ(string note, string item, bool hq)
        => Assert.Equal(hq, CraftNote.Read(note)[item].HighQuality);

    /// <summary>
    /// A Crisis of Confidence takes an HQ Walnut Lumber. A normal-quality result does not count: the
    /// crafter is asked again, and the step ends only once an HQ one is in the bag.
    /// </summary>
    [Fact]
    public void An_HQ_craft_asks_again_when_one_comes_out_normal_quality()
    {
        var world = new FakeStepWorld();
        world.Craftable.Add(5371);
        world.NoteHq.Add(5371);
        world.CraftsHq.Enqueue(false);
        world.CraftsHq.Enqueue(true);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(5371, 1), questId: 168);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Equal(2, world.Calls.Count(c => c.StartsWith("Craft 1 x 5371")));
        Assert.Equal(1, world.ItemCountHq(5371));
        Assert.Contains(world.Calls, c => c.StartsWith("Log") && c.Contains("normal quality"));
    }

    [Fact]
    public void An_HQ_craft_that_keeps_coming_out_normal_quality_stops_rather_than_burn_materials()
    {
        var world = new FakeStepWorld();
        world.Craftable.Add(5371);
        world.NoteHq.Add(5371);
        world.CraftsHq.Enqueue(false);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(5371, 1), questId: 168);

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("takes only HQ", ex.FailReason);
        Assert.Equal(MaxHqAsks, world.Calls.Count(c => c.StartsWith("Craft 1 x 5371")));
    }

    private const int MaxHqAsks = 2;

    [Fact]
    public void A_craft_the_quest_takes_at_any_quality_is_not_held_to_HQ()
    {
        var world = new FakeStepWorld();
        world.Craftable.Add(5361);
        world.CraftsHq.Enqueue(false);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(5361, 1), questId: 205);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Single(world.Calls, c => c.StartsWith("Craft 1 x 5361"));
    }

    [Theory]
    [InlineData("!!Requires Manual Melding!!\nCrafted Item:\n 1x Crab Bow HQ with 1x Savage Aim Materia III,\n 1x Rosewood Lumber HQ\nAutocraft requires:", "Crab Bow", true, "Savage Aim Materia III", null)]
    [InlineData("!!Requires Manual Melding!!\nCrafted Item:\n 1x Staghorn Staff with any Materia\nAutocraft requires:", "Staghorn Staff", false, null, null)]
    [InlineData("!!Requires Manual Melding!!\nCrafts: 1x Iron Lance\nRequires: 1x * Materia I (not II+)\nAutocraft requires:\n 1x Elm Lumber,", "Iron Lance", false, null, 1)]
    [InlineData("!!Requires Manual Melding!!\nCrafted Item:\n 1x Goatskin Leggings with any Materia// Autocraft requires:\n 2x Aldgoat Leather,", "Goatskin Leggings", false, null, null)]
    [InlineData("!!Requires Manual Melding!! Crafted Item:\n 1x Crab Bow HQ with 1x Savage Aim Materia III,\n 1x Rosewood Lumber HQ\nAutocraft requires:\n 1x Oak Composite Bow,", "Crab Bow", true, "Savage Aim Materia III", null)]
    public void The_path_note_says_what_materia_the_quest_wants_melded(string note, string item, bool hq, string? materia, int? grade)
    {
        var entry = CraftNote.Read(note)[item];
        Assert.Equal(hq, entry.HighQuality);
        Assert.NotNull(entry.Melded);
        Assert.Equal(materia, entry.Melded!.Materia);
        Assert.Equal(grade, entry.Melded.Grade);
        Assert.Equal(1, entry.Melded.Count);
    }

    [Fact]
    public void A_second_item_in_the_same_note_carries_no_materia_of_the_first()
    {
        var entries = CraftNote.Read("Crafted Item:\n 1x Crab Bow HQ with 1x Savage Aim Materia III,\n 1x Rosewood Lumber HQ\nAutocraft requires:");
        Assert.Null(entries["Rosewood Lumber"].Melded);
        Assert.True(entries["Rosewood Lumber"].HighQuality);
    }

    /// <summary>
    /// The Lance's Lesson: the lance is made, but the quest takes it only with a grade I materia in
    /// it. The meld is done the way the recorded clicks do it: open, the lance, a grade I materia —
    /// not the Savage Might III sitting first in the list — then Meld once the confirmation agrees.
    /// </summary>
    [Fact]
    public void A_craft_the_quest_takes_melded_is_melded_with_a_materia_that_fits()
    {
        var world = new FakeStepWorld();
        world.Craftable.Add(1827);
        world.NoteMeld[1827] = new CraftNote.Meld(null, 1, 1);
        world.MeldItems.AddRange([6112u, 1827u]);                       // Rainbow Cap, Iron Lance
        world.MeldMaterias.AddRange(["Savage Might Materia III", "Savage Aim Materia I"]);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(1827, 1), questId: 142);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("OpenMelding", world.Calls);
        Assert.Contains("MeldItem 1", world.Calls);
        Assert.Contains("MeldMateria 1", world.Calls);                  // the grade I, not the III
        Assert.Single(world.Calls, c => c == "ConfirmMeld");
        Assert.Contains(1827u, world.Melded);
        Assert.False(world.MeldingOpen);                                 // closed after
    }

    [Fact]
    public void With_no_materia_that_fits_it_says_which_to_get_and_melds_nothing()
    {
        var world = new FakeStepWorld();
        world.Craftable.Add(4543);
        world.NoteMeld[4543] = new CraftNote.Meld("Savage Aim Materia III", null, 1);
        world.MeldItems.Add(4543);
        world.MeldMaterias.Add("Savage Aim Materia I");
        var ex = new StepExecutor(world);
        ex.Begin(Craft(4543, 1), questId: 539);

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("no Savage Aim Materia III in the bags", ex.FailReason);
        Assert.DoesNotContain("ConfirmMeld", world.Calls);
    }

    [Fact]
    public void Melding_not_yet_learned_is_said_rather_than_waited_on()
    {
        var world = new FakeStepWorld { MeldingUnlocked = false };
        world.Craftable.Add(1827);
        world.NoteMeld[1827] = new CraftNote.Meld(null, 1, 1);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(1827, 1), questId: 142);

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("Materia Melding is not learned", ex.FailReason);
    }

    /// <summary>"Any Materia" takes the cheapest on offer, not whatever the window lists first.</summary>
    [Fact]
    public void Any_materia_melds_the_lowest_grade_on_offer()
    {
        string[] offered = ["Savage Might Materia III", "Piety Materia II", "Savage Aim Materia I", "Heavens' Eye Materia I"];
        Assert.Equal(2, CraftNote.Pick(offered, new CraftNote.Meld(null, null, 1)));
        Assert.Equal(0, CraftNote.Pick(offered, new CraftNote.Meld("Savage Might Materia III", null, 1)));
        Assert.Equal(1, CraftNote.Pick(offered, new CraftNote.Meld(null, 2, 1)));
        Assert.Equal(-1, CraftNote.Pick(offered, new CraftNote.Meld(null, 4, 1)));
    }

    [Theory]
    [InlineData("Savage Aim Materia I", null, 1, true)]
    [InlineData("Savage Might Materia III", null, 1, false)]
    [InlineData("Savage Might Materia III", "Savage Might Materia III", null, true)]
    [InlineData("Savage Aim Materia III", "Savage Might Materia III", null, false)]
    [InlineData("Piety Materia II", null, null, true)]
    public void A_materia_fits_the_requirement_by_name_or_grade(string materia, string? wanted, int? grade, bool fits)
        => Assert.Equal(fits, CraftNote.Fits(materia, new CraftNote.Meld(wanted, grade, 1)));

    [Fact]
    public void The_converter_hands_the_path_note_to_a_craft_step_that_names_no_item()
    {
        const string json = """
            { "Comment": "Crafted Item:\n 3x Square Maple Shield", "QuestSequence": [ { "Sequence": 255, "Steps": [
              { "TerritoryId": 132, "InteractionType": "Craft" },
              { "TerritoryId": 132, "InteractionType": "Craft", "ItemId": 5056, "$": "The recipe makes 3 items." } ] } ] }
            """;
        var path = QuestionableImporter.Parse("139_x.json", "QuestPaths/x", json, out _)!;
        Assert.Contains("3x Square Maple Shield", path.Block(255)!.Steps[0].Comment);
        Assert.Contains("3x Square Maple Shield", path.Block(255)!.Steps[1].Comment);   // a named one still needs it for the quality
        Assert.Contains("The recipe makes 3 items.", path.Block(255)!.Steps[1].Comment); // beside its own
    }

    [Fact]
    public void A_craft_step_naming_no_item_with_everything_in_the_bag_is_done()
    {
        var world = new FakeStepWorld();
        world.HandIns[205] = [(5361, 1, false)];
        world.Bag[5361] = 1;
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Craft, KindName = "Craft", TerritoryId = 400 }, questId: 205);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Craft "));
    }

    [Fact]
    public void A_craft_step_naming_no_item_for_a_quest_that_hands_in_nothing_craftable_stops()
    {
        var world = new FakeStepWorld();
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Craft, KindName = "Craft", TerritoryId = 400 }, questId: 9);

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("hands in nothing that can be crafted", ex.FailReason);
    }

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
        world.Craftable.Add(Ingot);
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
    /// Artisan producing nothing means the materials ran out. Saying which ones is the whole value
    /// of the stop — otherwise you are left staring at an idle crafting log.
    /// </summary>
    [Fact]
    public void Craft_that_stops_short_names_the_missing_ingredients()
    {
        var world = new FakeStepWorld { CraftDelivers = 0 };
        world.Shortfall.Add(new MaterialShortfall(Ore, "Copper Ore", 4));
        world.Craftable.Add(Ingot);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 3));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("Artisan stopped", ex.FailReason);
        Assert.Contains("4 × Copper Ore", ex.FailReason);
    }

    /// <summary>
    /// Artisan making some but not all is progress, not failure: the loop re-reads the bag and
    /// asks for what is left, which converges. Only a round that delivers nothing is the end.
    /// </summary>
    [Fact]
    public void A_craft_that_arrives_in_pieces_keeps_going_until_it_is_covered()
    {
        var world = new FakeStepWorld { CraftDelivers = 1 };
        world.Craftable.Add(Ingot);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 3));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Equal(3, world.Bag[Ingot]);
        Assert.Equal(3, world.Calls.Count(c => c.StartsWith("Craft ")));
    }

    [Fact]
    public void Craft_without_Artisan_stops_and_says_so()
    {
        var world = new FakeStepWorld { CrafterReady = false };
        world.Craftable.Add(Ingot);
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
        world.Craftable.Add(Ingot);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 3));
        ex.Tick();
        ex.Tick();
        Assert.Contains($"Craft 3 x {Ingot}", world.Calls);
        Assert.Equal(StepStatus.Running, ex.Status);

        ex.Cancel();
        Assert.Contains("StopCraft", world.Calls);
    }

    // ── Sub-component crafting ──
    //
    // Quest 613's shape: twelve Copper Rings, each needing a Copper Ingot, with no ingots in the
    // bag. Artisan crafts one recipe — asked for the rings it makes nothing — and its crafting
    // lists, which do resolve sub-crafts, can only be started by id if you built one by hand.

    private const uint Rings = 5086;

    [Fact]
    public void A_craft_makes_its_missing_sub_component_first()
    {
        var world = new FakeStepWorld();
        world.MadeFrom[Rings] = Ingot;      // one ring per ingot
        world.Craftable.Add(Ingot);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Rings, 12));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        var ingots = world.Calls.IndexOf($"Craft 12 x {Ingot}");
        var rings = world.Calls.IndexOf($"Craft 12 x {Rings}");
        Assert.True(ingots >= 0, "the ingots were never crafted");
        Assert.True(rings > ingots, "the rings were crafted before the ingots they are made from");
        Assert.Equal(12, world.Bag[Rings]);
    }

    /// <summary>Ore → ingot → rings: the deepest missing thing is made first, and only then up.</summary>
    [Fact]
    public void A_two_deep_tree_is_walked_from_the_bottom()
    {
        var world = new FakeStepWorld();
        world.MadeFrom[Rings] = Ingot;
        world.MadeFrom[Ingot] = Ore;
        world.Craftable.Add(Ore);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Rings, 2));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        var order = world.Calls.Where(c => c.StartsWith("Craft ")).ToList();
        Assert.Equal(3, order.Count);
        Assert.StartsWith($"Craft 2 x {Ore}", order[0]);
        Assert.StartsWith($"Craft 2 x {Ingot}", order[1]);
        Assert.StartsWith($"Craft 2 x {Rings}", order[2]);
    }

    /// <summary>What is already in the bag is not remade on the way down.</summary>
    [Fact]
    public void A_sub_component_already_held_is_not_crafted_again()
    {
        var world = new FakeStepWorld();
        world.MadeFrom[Rings] = Ingot;
        world.Craftable.Add(Ingot);
        world.Bag[Ingot] = 12;
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Rings, 12));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain($"Craft 12 x {Ingot}", world.Calls);
        Assert.Contains($"Craft 12 x {Rings}", world.Calls);
    }

    /// <summary>
    /// A sub-component that cannot be crafted — ore comes out of the ground or a vendor — is where
    /// the walk stops, and the stop says what to go and get.
    /// </summary>
    [Fact]
    public void A_sub_component_that_can_only_be_bought_stops_with_a_reason()
    {
        var world = new FakeStepWorld();
        world.Shortfall.Add(new MaterialShortfall(Ingot, "Copper Ingot", 12));
        world.MadeFrom[Rings] = Ingot;      // ingot is not in Craftable — a leaf
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Rings, 12));

        // This is the message from the live run: Artisan was asked, made nothing, and the stop
        // names the ingredient that has to come from somewhere else.
        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("Artisan stopped", ex.FailReason);
        Assert.Contains("12 × Copper Ingot", ex.FailReason);
    }

    // ── Buying what the craft turned out to need ──
    //
    // The path data assumes you own the materials. A character running the class for the first
    // time owns none of them, and every crafting guild keeps its material vendor beside the
    // guildmaster — so the shop is a few paces away and the run can just go and buy.

    private const uint GuildVendor = 1004419;

    [Fact]
    public void A_craft_short_of_a_base_material_buys_it_from_a_vendor_standing_here()
    {
        var world = new FakeStepWorld();
        world.Craftable.Add(Ingot);
        world.MadeFrom[Ingot] = Ore;
        world.PerCraft = 3;
        world.Shortfall.Add(new MaterialShortfall(Ore, "Copper Ore", 3));
        world.Vendors[Ore] = new VendorOffer(GuildVendor, 262176, "Alaric", 9);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 1));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains($"OpenShop {GuildVendor}/262176", world.Calls);
        Assert.Contains($"Buy 3 x {Ore} from 262176", world.Calls);
        Assert.Contains("CloseShop", world.Calls);
        Assert.Equal(1, world.Bag[Ingot]);   // and it went back and made the thing
    }

    /// <summary>
    /// Being in the object table is not being in reach. A merchant across the guild hall is
    /// visible and still too far to talk to — the first attempt stood still and tried to open a
    /// shop from thirty yalms.
    /// </summary>
    [Fact]
    public void It_walks_to_a_merchant_who_is_visible_but_not_beside_you()
    {
        var world = new FakeStepWorld { ArriveOnMove = true };
        world.Craftable.Add(Ingot);
        world.MadeFrom[Ingot] = Ore;
        world.PerCraft = 3;
        world.Shortfall.Add(new MaterialShortfall(Ore, "Copper Ore", 3));
        world.Vendors[Ore] = new VendorOffer(GuildVendor, 262176, "Alaric", 9);
        world.Spawned.Add(GuildVendor);
        world.Positions[GuildVendor] = new System.Numerics.Vector3(30, 0, 0);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 1));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains(world.Calls, c => c.StartsWith("Move 30,0,0"));
        Assert.Contains($"OpenShop {GuildVendor}/262176", world.Calls);
        Assert.Equal(1, world.Bag[Ingot]);
    }

    /// <summary>Standing beside them already, there is nothing to walk.</summary>
    [Fact]
    public void It_does_not_walk_when_the_merchant_is_already_in_reach()
    {
        var world = new FakeStepWorld();
        world.Craftable.Add(Ingot);
        world.MadeFrom[Ingot] = Ore;
        world.PerCraft = 3;
        world.Shortfall.Add(new MaterialShortfall(Ore, "Copper Ore", 3));
        world.Vendors[Ore] = new VendorOffer(GuildVendor, 262176, "Alaric", 9);
        world.Spawned.Add(GuildVendor);   // no Positions entry — sits on the player
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 1));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Move "));
    }

    /// <summary>Nobody here sells it — then it is still a stop, and still says what to go and get.</summary>
    [Fact]
    public void With_no_vendor_in_reach_it_stops_naming_the_material()
    {
        var world = new FakeStepWorld();
        world.Craftable.Add(Ingot);
        world.MadeFrom[Ingot] = Ore;
        world.PerCraft = 3;
        world.Shortfall.Add(new MaterialShortfall(Ore, "Copper Ore", 3));
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 1));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("3 × Copper Ore", ex.FailReason);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("OpenShop"));
    }

    /// <summary>
    /// Each material is bought once. A craft that still fails after the shopping has something
    /// else wrong with it, and bouncing between the vendor and the crafting log would hide that.
    /// </summary>
    [Fact]
    public void A_material_is_bought_once_and_then_the_step_gives_up()
    {
        var world = new FakeStepWorld { BuyDelivers = 0 };   // the shop takes the order and delivers nothing
        world.Craftable.Add(Ingot);
        world.MadeFrom[Ingot] = Ore;
        world.PerCraft = 3;
        world.Shortfall.Add(new MaterialShortfall(Ore, "Copper Ore", 3));
        world.Vendors[Ore] = new VendorOffer(GuildVendor, 262176, "Alaric", 9);
        var ex = new StepExecutor(world);
        ex.Begin(Craft(Ingot, 1));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Equal(1, world.Calls.Count(c => c.StartsWith("OpenShop")));
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

    private sealed class FakeOwnGatherer : Odysseus.Services.Gathering.IOwnGatherer
    {
        public bool Enabled { get; set; } = true;
        public bool CanGatherAnswer { get; set; } = true;
        public bool CanGather(uint itemId, uint territoryHint = 0) => Enabled && CanGatherAnswer;
        public string WhyNot(uint itemId, uint territoryHint = 0) => "test says no";
        public uint? ZoneOf(uint itemId) => null;
        public string Where(uint itemId) => "";
        public List<(uint Item, int Count)> Starts { get; } = [];
        public int TicksSeen { get; private set; }
        public int TicksUntilDone { get; set; } = 3;
        public Action? OnDone { get; set; }
        public bool Start(uint itemId, int count, int collectability, uint territoryHint = 0) { Starts.Add((itemId, count)); Busy = true; return Enabled; }
        public void Tick()
        {
            TicksSeen++;
            if (Busy && TicksSeen >= TicksUntilDone) { Busy = false; OnDone?.Invoke(); }
        }
        public bool Busy { get; private set; }
        public bool Faulted { get; set; }
        public string Status { get; set; } = "";
        public bool DryRun { get; set; }
        public bool ProbeOnly { get; set; }
        public int Stopped { get; private set; }
        public void Stop() { Stopped++; Busy = false; }
    }

    [Fact]
    public void A_wired_own_gatherer_takes_the_quest_gather_instead_of_GatherBuddy()
    {
        // The Stewards of Note's gather ended in "check it is on one of its auto-gather lists";
        // with our own gathering wired in and on, GatherBuddy is never asked.
        var world = new FakeStepWorld { GathererReady = true };
        var own = new FakeOwnGatherer();
        own.OnDone = () => world.Bag[Ore] = 5;
        var ex = new StepExecutor(world) { OwnGatherer = own };
        ex.Begin(Gather(new GatherTarget(Ore, 5)));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Equal((Ore, 5), Assert.Single(own.Starts));
        Assert.DoesNotContain("StartGather", world.Calls);
    }

    [Fact]
    public void Bench_modes_and_off_leave_the_quest_gather_to_GatherBuddy()
    {
        foreach (var own in new[]
        {
            new FakeOwnGatherer { Enabled = false },
            new FakeOwnGatherer { ProbeOnly = true },
            new FakeOwnGatherer { DryRun = true },
            new FakeOwnGatherer { CanGatherAnswer = false },
        })
        {
            var world = new FakeStepWorld { GathererReady = true };
            var ex = new StepExecutor(world) { OwnGatherer = own };
            ex.Begin(Gather(new GatherTarget(Ore, 5)));
            for (var i = 0; i < 4 && !world.Calls.Contains("StartGather"); i++) { ex.Tick(); world.Advance(0.5); }
            Assert.Empty(own.Starts);
            Assert.Contains("StartGather", world.Calls);
        }
    }

    [Fact]
    public void An_own_gather_that_gives_up_faults_with_its_reason()
    {
        var world = new FakeStepWorld { GathererReady = true };
        var own = new FakeOwnGatherer { Status = "no live node reachable" };
        own.OnDone = () => own.Faulted = true;
        var ex = new StepExecutor(world) { OwnGatherer = own };
        ex.Begin(Gather(new GatherTarget(Ore, 5)));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("no live node reachable", ex.FailReason);
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

    /// <summary>
    /// The finished item in the FC chest is fetched, not made: its ingredients leave the bill, so
    /// the Grab button stops taking both. Only what the chest cannot cover is still expanded.
    /// </summary>
    [Fact]
    public void A_craft_the_chest_holds_is_fetched_and_its_ingredients_are_not_wanted()
    {
        var asked = new List<(uint, int)>();
        ChainMaterials.ExpandCraft expand = (item, count) =>
        {
            asked.Add((item, count));
            return [(9001u, "Fire Shard", count * 2)];
        };

        var covered = Bill([], chest: new Dictionary<uint, int> { [Ingot] = 3 }, expand: expand);
        Assert.True(covered.Single(n => n.ItemId == Ingot).CoveredByChest);
        Assert.DoesNotContain(covered, n => n.ItemId == 9001);
        Assert.Empty(asked);

        var partly = Bill([], chest: new Dictionary<uint, int> { [Ingot] = 1 }, expand: expand);
        Assert.Equal([(Ingot, 2)], asked);                     // the two the chest cannot give
        Assert.Equal(4, partly.Single(n => n.ItemId == 9001).Needed);
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
        var genuinely = new QuestStep { Kind = StepKind.Unknown, KindName = "UnlockTaxiStand", TerritoryId = 400 };
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
