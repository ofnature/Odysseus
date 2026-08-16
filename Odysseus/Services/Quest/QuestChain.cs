using System;
using System.Collections.Generic;
using System.Linq;

namespace Odysseus.Services.Quest;

/// <summary>One quest in an unlock chain, in the order it must be done.</summary>
public sealed record ChainStep(ushort QuestId, string Name, ushort Level, bool HasPath);

/// <summary>
/// What it takes to unlock something: the quests to run, prerequisites first, plus whatever
/// stands in the way.
/// </summary>
/// <param name="Steps">Incomplete quests in run order — the target is last.</param>
/// <param name="AlreadyDone">The target is already complete; <see cref="Steps"/> is empty.</param>
/// <param name="MissingPaths">Quests in the chain with no stored path — they will stop the run.</param>
/// <param name="Unobtainable">Quests the character can never take (wrong city, alternative chosen).</param>
public sealed record ChainPlan(
    IReadOnlyList<ChainStep> Steps,
    bool AlreadyDone,
    IReadOnlyList<ChainStep> MissingPaths,
    IReadOnlyList<ushort> Unobtainable)
{
    public bool IsRunnable => !AlreadyDone && Steps.Count > 0 && MissingPaths.Count == 0 && Unobtainable.Count == 0;

    public string Summary => AlreadyDone ? "already done"
        : Steps.Count == 0 ? "nothing to run"
        : Unobtainable.Count > 0 ? $"{Unobtainable.Count} quest(s) unobtainable on this character"
        : MissingPaths.Count > 0 ? $"{Steps.Count} quests, {MissingPaths.Count} without a path"
        : Steps.Count == 1 ? "1 quest" : $"{Steps.Count} quests";
}

/// <summary>
/// Works out the quest chain that leads to a target quest.
///
/// <para>
/// Walks <c>PreviousQuest</c> backwards from the target, collecting everything not yet complete,
/// and returns them prerequisite-first (post-order, so a quest never appears before something it
/// needs). <c>PreviousQuestJoin</c> is honoured: <b>all</b> prerequisites are pulled in for join 1;
/// for join 2 ("at least one") a single branch is chosen — one already complete if there is one,
/// otherwise the cheapest by chain length.
/// </para>
///
/// <para>
/// This is what the Unlock buttons use: an allied society's first story quest, or a custom
/// delivery client's <c>QuestRequired</c>, resolved into an ordered list that goes straight onto
/// the priority list.
/// </para>
/// </summary>
public static class QuestChain
{
    /// <summary>Depth guard — the longest real MSQ prerequisite chain is a few hundred; beyond that something is cyclic.</summary>
    private const int MaxSteps = 400;

    public static ChainPlan Resolve(ushort target, QuestCatalog catalog, Func<ushort, bool> isComplete, Func<ushort, bool> hasPath, CharacterFacts facts = default)
    {
        if (target == 0)
            return new ChainPlan([], false, [], []);
        if (isComplete(target))
            return new ChainPlan([], true, [], []);

        var ordered = new List<ushort>();
        var seen = new HashSet<ushort>();
        var visiting = new HashSet<ushort>();
        var unobtainable = new List<ushort>();

        void Visit(ushort id)
        {
            if (ordered.Count >= MaxSteps || isComplete(id) || !seen.Add(id) || !visiting.Add(id))
                return;
            try
            {
                if (catalog.ById(id) is { } listing)
                {
                    if (!QuestCatalog.IsObtainable(listing, isComplete, facts))
                    {
                        unobtainable.Add(id);
                        return;
                    }
                    foreach (var prev in ChooseParents(listing, catalog, isComplete))
                        Visit(prev);
                }
                ordered.Add(id); // post-order: prerequisites are already in
            }
            finally
            {
                visiting.Remove(id);
            }
        }

        Visit(target);

        var steps = ordered
            .Select(id => catalog.ById(id) is { } l
                ? new ChainStep(id, l.Name, l.ClassJobLevel, hasPath(id))
                : new ChainStep(id, $"Quest {id}", 0, hasPath(id)))
            .ToList();

        return new ChainPlan(steps, false, steps.Where(s => !s.HasPath).ToList(), unobtainable);
    }

    /// <summary>Which prerequisites to pull in: all of them, or one branch for an "at least one" join.</summary>
    private static IEnumerable<ushort> ChooseParents(QuestListing listing, QuestCatalog catalog, Func<ushort, bool> isComplete)
    {
        if (listing.Previous.Length == 0)
            return [];
        if (listing.PreviousJoin != 2)
            return listing.Previous;

        // Any-of: nothing to do if a branch is already done; otherwise take the shallowest.
        if (listing.Previous.Any(isComplete))
            return [];
        return [listing.Previous.OrderBy(p => Depth(p, catalog, isComplete, 0)).First()];
    }

    private static int Depth(ushort id, QuestCatalog catalog, Func<ushort, bool> isComplete, int depth)
    {
        if (depth > 32 || isComplete(id) || catalog.ById(id) is not { } l || l.Previous.Length == 0)
            return depth;
        return l.Previous.Max(p => Depth(p, catalog, isComplete, depth + 1));
    }
}
