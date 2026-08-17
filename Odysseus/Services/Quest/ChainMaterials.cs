using System;
using System.Collections.Generic;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Quest;

/// <summary>Where one item on a chain's bill is meant to come from.</summary>
public enum MaterialSource
{
    /// <summary>A PurchaseItem step buys it from a named vendor.</summary>
    Vendor,
    /// <summary>A Craft step makes it; its own ingredients are listed separately.</summary>
    Crafted,
    /// <summary>An ingredient of something crafted.</summary>
    Ingredient,
    /// <summary>A Gather or Fish step brings it back.</summary>
    Gathered,
    /// <summary>A quest-only gathering item. Nothing can fetch it and nothing can count it.</summary>
    QuestItem,
}

/// <summary>
/// One line of a chain's bill of materials.
/// </summary>
/// <param name="Held">In the bags. <see cref="MaterialSource.QuestItem"/> lines cannot be counted and report -1.</param>
/// <param name="InChest">In the Free Company chest, as far as the loaded pages can say.</param>
public sealed record MaterialNeed(
    uint ItemId, string Name, int Needed, int Held, int InChest, MaterialSource Source, string Where)
{
    /// <summary>How many are still to find. Unknowable for a quest item, which reports the whole amount.</summary>
    public int Missing => Held < 0 ? Needed : Math.Max(0, Needed - Held);

    /// <summary>The shortfall is sitting in the chest — go and take it rather than making more.</summary>
    public bool CoveredByChest => Missing > 0 && InChest >= Missing;
}

/// <summary>
/// What a queued line of quests will ask you to bring.
///
/// <para>
/// Pure: it is handed the paths, a way to read the bags and the chest, and a way to expand a craft
/// into its ingredients. The three step kinds that name materials — <c>PurchaseItem</c>,
/// <c>Craft</c> and <c>Gather</c>/<c>Fish</c> — are all the bill needs, and every count in the data
/// is a <i>target total</i>, so the same step appearing in two quests wants the larger of the two
/// rather than the sum.
/// </para>
///
/// <para>
/// The point of it is the choice it gives you: gather the list yourself, let Artisan and
/// GatherBuddy work through it, or notice that half of it is already in the FC chest.
/// </para>
/// </summary>
public static class ChainMaterials
{
    /// <summary>Expands one craft into the ingredients it consumes.</summary>
    /// <param name="itemId">What is being made.</param>
    /// <param name="count">How many of it.</param>
    public delegate IReadOnlyList<(uint ItemId, string Name, int Needed)> ExpandCraft(uint itemId, int count);

    /// <param name="name">Item id → display name.</param>
    /// <param name="held">Item id → how many are in the bags.</param>
    /// <param name="inChest">Item id → how many are in the FC chest.</param>
    /// <param name="expand">Craft → ingredients; null leaves crafts unexpanded.</param>
    /// <param name="inStepOrder">
    /// Keep the order the steps meet the items in — buy, then craft, then gather — instead of
    /// putting what is missing first. One quest's list is short and reads as a set of instructions;
    /// a whole line's is long and reads as a shopping list, and they want opposite orders.
    /// </param>
    public static IReadOnlyList<MaterialNeed> For(
        IEnumerable<QuestPath> paths,
        Func<uint, string> name,
        Func<uint, int> held,
        Func<uint, int> inChest,
        ExpandCraft? expand = null,
        bool inStepOrder = false)
    {
        // Target totals, so the same requirement met twice is not counted twice. The key order is
        // kept separately rather than leaned on: a dictionary's enumeration order is not a promise.
        var wanted = new Dictionary<uint, (int Needed, MaterialSource Source, string Where)>();
        var order = new List<uint>();
        var crafts = new List<(uint ItemId, int Count)>();

        void Want(uint itemId, int count, MaterialSource source, string where)
        {
            if (itemId == 0 || count <= 0) return;
            if (wanted.TryGetValue(itemId, out var have))
            {
                // A thing both bought and crafted is reported by the way the path meets it first.
                wanted[itemId] = (Math.Max(have.Needed, count), have.Source, have.Where);
                return;
            }
            wanted[itemId] = (count, source, where);
            order.Add(itemId);
        }

        foreach (var path in paths)
        foreach (var sequence in path.Sequences)
        foreach (var step in sequence.Steps)
        {
            switch (step.Kind)
            {
                case StepKind.PurchaseItem when step.ItemId is { } bought:
                    Want(bought, Math.Max(1, step.ItemCount ?? 1), MaterialSource.Vendor,
                        step.DataId is { } vendor ? $"vendor {vendor}" : "a vendor");
                    break;

                case StepKind.Craft when step.ItemId is { } made:
                    var howMany = Math.Max(1, step.ItemCount ?? 1);
                    Want(made, howMany, MaterialSource.Crafted, path.Name);
                    crafts.Add((made, howMany));
                    break;

                case StepKind.Gather or StepKind.Fish when step.GatherItems is { } targets:
                    foreach (var t in targets)
                        Want(t.ItemId, t.ItemCount,
                            t.IsEventItem ? MaterialSource.QuestItem : MaterialSource.Gathered, path.Name);
                    break;
            }
        }

        // Ingredients of everything crafted, on top of what the steps named directly. Only the
        // shortfall is expanded: ingredients for something already in the bag are not needed.
        if (expand is not null)
        {
            foreach (var (itemId, count) in crafts)
            {
                var stillToMake = count - held(itemId);
                if (stillToMake <= 0) continue;
                foreach (var (ingredient, ingredientName, needed) in expand(itemId, stillToMake))
                {
                    if (wanted.ContainsKey(ingredient)) continue;
                    wanted[ingredient] = (needed, MaterialSource.Ingredient, ingredientName);
                    order.Add(ingredient);
                }
            }
        }

        var bill = new List<MaterialNeed>(order.Count);
        foreach (var itemId in order)
        {
            var (needed, source, where) = wanted[itemId];
            var isQuestItem = source == MaterialSource.QuestItem;
            bill.Add(new MaterialNeed(
                itemId,
                isQuestItem ? $"quest item {itemId}" : name(itemId),
                needed,
                isQuestItem ? -1 : held(itemId),
                isQuestItem ? 0 : inChest(itemId),
                source,
                where));
        }

        if (inStepOrder)
            return bill;

        // Everything still missing first, then by name — the list is a shopping list, so what you
        // have already should not be what you read first.
        bill.Sort((a, b) =>
        {
            var byMissing = (b.Missing > 0).CompareTo(a.Missing > 0);
            return byMissing != 0 ? byMissing : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return bill;
    }

    /// <summary>
    /// Whether a quest's steps name anything at all. Cheap — no sheet reads and no recipe
    /// expansion — so it can be asked of every row on screen to decide which ones get a list.
    /// </summary>
    public static bool NamesItems(QuestPath path)
    {
        foreach (var sequence in path.Sequences)
        foreach (var step in sequence.Steps)
        {
            var names = step.Kind switch
            {
                StepKind.PurchaseItem or StepKind.Craft => step.ItemId is > 0,
                StepKind.Gather or StepKind.Fish => step.GatherItems is { Count: > 0 },
                _ => false,
            };
            if (names) return true;
        }
        return false;
    }
}
