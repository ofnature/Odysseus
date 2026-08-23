using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>
/// Quest 610's sequence 255, which is where this came from: buy three Copper Ore, craft one Copper
/// Ingot, hand it in. Each step's "skip if already held" clause asks only about its own item, so
/// arriving with the ingot already made skipped the craft and bought the ore anyway.
/// </summary>
public class PurchasePlanTests
{
    private const uint Ore = 5106;
    private const uint Ingot = 5062;
    private const uint Leather = 5275;

    /// <summary>One Copper Ingot is three Copper Ore; nothing else here has a recipe.</summary>
    private static readonly PurchasePlan.IngredientsOf Recipes =
        item => item == Ingot ? [Ore] : [];

    private static List<QuestStep> Sequence() =>
    [
        new QuestStep { Kind = StepKind.PurchaseItem, ItemId = Ore, ItemCount = 3, DataId = 1004419 },
        new QuestStep { Kind = StepKind.Craft, ItemId = Ingot, ItemCount = 1 },
        new QuestStep { Kind = StepKind.CompleteQuest, DataId = 1004093 },
    ];

    private static bool Worth(List<QuestStep> steps, Dictionary<uint, int> bag, int index = 0)
        => PurchasePlan.IsWorthBuying(steps, index, id => bag.GetValueOrDefault(id), Recipes);

    [Fact]
    public void The_ore_is_not_bought_when_the_ingot_it_makes_is_already_in_the_bag()
        => Assert.False(Worth(Sequence(), new Dictionary<uint, int> { [Ingot] = 1 }));

    [Fact]
    public void The_ore_is_bought_when_the_ingot_still_has_to_be_made()
        => Assert.True(Worth(Sequence(), []));

    /// <summary>Part-made counts for nothing: one of the three still means the craft has to run.</summary>
    [Fact]
    public void A_craft_only_partly_covered_still_needs_its_materials()
    {
        var steps = Sequence();
        steps[1].ItemCount = 3;
        Assert.True(Worth(steps, new Dictionary<uint, int> { [Ingot] = 1 }));
    }

    /// <summary>
    /// A purchase that feeds nothing is for its own sake — a turn-in, a use, bait. Nothing about
    /// crafts says anything about it, so it is always made.
    /// </summary>
    [Fact]
    public void A_purchase_that_feeds_no_craft_is_always_made()
    {
        List<QuestStep> steps =
        [
            new QuestStep { Kind = StepKind.PurchaseItem, ItemId = Leather, ItemCount = 1 },
            new QuestStep { Kind = StepKind.Craft, ItemId = Ingot, ItemCount = 1 },
        ];
        Assert.True(Worth(steps, new Dictionary<uint, int> { [Ingot] = 1 }));
    }

    /// <summary>
    /// Wanted by name later as well as being an ingredient — the craft being done says nothing
    /// about the step that uses it directly, so the purchase stands.
    /// </summary>
    [Fact]
    public void An_item_a_later_step_names_for_itself_is_still_bought()
    {
        var steps = Sequence();
        steps.Add(new QuestStep { Kind = StepKind.UseItem, ItemId = Ore });
        Assert.True(Worth(steps, new Dictionary<uint, int> { [Ingot] = 1 }));
    }

    [Fact]
    public void An_item_a_later_gather_step_wants_is_still_bought()
    {
        var steps = Sequence();
        steps.Add(new QuestStep { Kind = StepKind.Gather, GatherItems = [new GatherTarget(Ore, 5)] });
        Assert.True(Worth(steps, new Dictionary<uint, int> { [Ingot] = 1 }));
    }

    /// <summary>Only what comes after matters — a craft already behind us consumed its materials.</summary>
    [Fact]
    public void A_craft_earlier_in_the_sequence_does_not_excuse_the_purchase()
    {
        List<QuestStep> steps =
        [
            new QuestStep { Kind = StepKind.Craft, ItemId = Ingot, ItemCount = 1 },
            new QuestStep { Kind = StepKind.PurchaseItem, ItemId = Ore, ItemCount = 3 },
        ];
        Assert.True(Worth(steps, new Dictionary<uint, int> { [Ingot] = 1 }, index: 1));
    }

    /// <summary>Feeding two crafts, one of them still to do, keeps the purchase.</summary>
    [Fact]
    public void One_unsatisfied_craft_among_several_keeps_the_purchase()
    {
        PurchasePlan.IngredientsOf both = item => item is Ingot or 9999 ? [Ore] : [];
        List<QuestStep> steps =
        [
            new QuestStep { Kind = StepKind.PurchaseItem, ItemId = Ore, ItemCount = 3 },
            new QuestStep { Kind = StepKind.Craft, ItemId = Ingot, ItemCount = 1 },
            new QuestStep { Kind = StepKind.Craft, ItemId = 9999, ItemCount = 1 },
        ];
        var bag = new Dictionary<uint, int> { [Ingot] = 1 };
        Assert.True(PurchasePlan.IsWorthBuying(steps, 0, id => bag.GetValueOrDefault(id), both));
    }

    /// <summary>Anything that is not a purchase is none of this function's business.</summary>
    [Fact]
    public void Other_step_kinds_are_left_alone()
        => Assert.True(Worth(Sequence(), new Dictionary<uint, int> { [Ingot] = 1 }, index: 1));

    /// <summary>
    /// One Size Fits All: the happi sets stood crafted 3/3 while the gather step demanded the
    /// malachite they had consumed. The bundle orders the craft before its gathers, so the
    /// gather judgement scans the whole block.
    /// </summary>
    [Fact]
    public void A_gather_whose_crafts_are_all_made_is_not_worth_running()
    {
        List<QuestStep> steps =
        [
            new QuestStep { Kind = StepKind.Craft, ItemId = Ingot, ItemCount = 3 },
            new QuestStep { Kind = StepKind.Gather, KindName = "Gather", GatherItems = [new GatherTarget(Ore, 3)] },
        ];
        PurchasePlan.IngredientsOf of = item => item == Ingot ? [Ore] : [];
        Assert.False(PurchasePlan.IsWorthGathering(steps, 1, item => item == Ingot ? 3 : 0, of));

        // The craft still short: the gather stands.
        Assert.True(PurchasePlan.IsWorthGathering(steps, 1, _ => 0, of));
    }
}
