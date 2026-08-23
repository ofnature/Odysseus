using System;
using System.Collections.Generic;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Run;

/// <summary>
/// Whether a <c>PurchaseItem</c> step still has a purpose by the time the run reaches it.
///
/// <para>
/// The path data is step-local: a sequence buys three Copper Ore, crafts one Copper Ingot from
/// them, and hands the ingot in — but each step's "skip if already held" clause asks only about
/// its <i>own</i> item. Arrive holding the ingot and the craft correctly skips while the purchase
/// still runs, buying materials for something that will never be made.
/// </para>
///
/// <para>
/// So the question asked here is the one the data does not: is anything left in this sequence that
/// will actually consume what this step is about to buy? A purchase is kept unless every later
/// craft it feeds is already satisfied — and kept outright if anything else in the sequence names
/// the item, because then it is wanted for its own sake rather than as an ingredient.
/// </para>
/// </summary>
public static class PurchasePlan
{
    /// <summary>What crafting one of an item consumes. Empty for anything with no recipe.</summary>
    public delegate IReadOnlyList<uint> IngredientsOf(uint itemId);

    /// <summary>
    /// True when the purchase is still worth making.
    /// </summary>
    /// <param name="steps">The sequence the step belongs to.</param>
    /// <param name="index">Which step is about to run.</param>
    /// <param name="held">How many of an item are in the bags.</param>
    /// <param name="ingredientsOf">Item → the items its recipe consumes.</param>
    public static bool IsWorthBuying(
        IReadOnlyList<QuestStep> steps, int index, Func<uint, int> held, IngredientsOf ingredientsOf)
    {
        if (index < 0 || index >= steps.Count)
            return true;
        var step = steps[index];
        if (step.Kind != StepKind.PurchaseItem || step.ItemId is not { } buying)
            return true;

        var feedsSomething = false;
        var everyCraftSatisfied = true;

        for (var i = index + 1; i < steps.Count; i++)
        {
            var later = steps[i];

            if (later.Kind == StepKind.Craft && later.ItemId is { } making)
            {
                var consumes = false;
                foreach (var ingredient in ingredientsOf(making))
                    if (ingredient == buying) { consumes = true; break; }
                if (!consumes)
                    continue;

                feedsSomething = true;
                if (held(making) < Math.Max(1, later.ItemCount ?? 1))
                    everyCraftSatisfied = false;
                continue;
            }

            // Wanted by name for its own sake — used, equipped, gathered up to a count, bought
            // again. Nothing about a satisfied craft says anything about that.
            if (later.ItemId == buying)
                return true;
            if (later.GatherItems is { } targets)
                foreach (var target in targets)
                    if (target.ItemId == buying)
                        return true;
        }

        return !feedsSomething || !everyCraftSatisfied;
    }

    /// <summary>
    /// The same judgement for a Gather step: skip it only when every target it is still short of
    /// exists solely to feed crafts that are already made. One Size Fits All's happi sets stood
    /// crafted 3/3 while the gather step demanded the malachite they had consumed — the bundle
    /// orders the craft before its gathers, which is also why this scans the whole block.
    /// </summary>
    public static bool IsWorthGathering(
        IReadOnlyList<QuestStep> steps, int index, Func<uint, int> held, IngredientsOf ingredientsOf)
    {
        if (index < 0 || index >= steps.Count)
            return true;
        var step = steps[index];
        if (step.Kind != StepKind.Gather || step.GatherItems is not { Count: > 0 } targets)
            return true;

        // The block's crafting, as a whole: gathers exist to serve it, and One Size Fits All's
        // raws are not even in the happi's recipe — the crate item is — so "feeds nothing the
        // sheets know" cannot mean "must be gathered" once every craft here stands satisfied.
        var anyCraft = false;
        var allCraftsSatisfied = true;
        foreach (var other in steps)
        {
            if (other.Kind != StepKind.Craft || other.ItemId is not { } making)
                continue;
            anyCraft = true;
            if (held(making) < Math.Max(1, other.ItemCount ?? 1))
                allCraftsSatisfied = false;
        }

        foreach (var target in targets)
        {
            if (held(target.ItemId) >= target.ItemCount)
                continue; // covered either way; says nothing about the rest
            if (NamedByAnotherStep(steps, index, target.ItemId))
                return true;
            if (FeedsAnyCraft(steps, target.ItemId, held, ingredientsOf, out var unsatisfied))
            {
                if (unsatisfied)
                    return true;
                continue; // feeds only crafts that are already made
            }
            if (!(anyCraft && allCraftsSatisfied))
                return true; // feeds nothing the sheets know, and the block still has work — gather it
        }
        return false;
    }

    private static bool NamedByAnotherStep(IReadOnlyList<QuestStep> steps, int index, uint item)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (i == index)
                continue;
            var other = steps[i];
            if (other.Kind != StepKind.Craft && other.ItemId == item)
                return true;
            if (other.Kind != StepKind.Gather)
                continue;
            if (other.GatherItems is { } wanted)
                foreach (var t in wanted)
                    if (t.ItemId == item)
                        return true;
        }
        return false;
    }

    private static bool FeedsAnyCraft(
        IReadOnlyList<QuestStep> steps, uint item, Func<uint, int> held, IngredientsOf ingredientsOf, out bool unsatisfied)
    {
        var feeds = false;
        unsatisfied = false;
        foreach (var other in steps)
        {
            if (other.Kind != StepKind.Craft || other.ItemId is not { } making)
                continue;
            var consumes = false;
            foreach (var ingredient in ingredientsOf(making))
                if (ingredient == item) { consumes = true; break; }
            if (!consumes)
                continue;
            feeds = true;
            if (held(making) < Math.Max(1, other.ItemCount ?? 1))
                unsatisfied = true;
        }
        return feeds;
    }

}
