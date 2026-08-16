using System;
using System.Linq;

namespace Odysseus.Services.Quest;

/// <summary>
/// Turns "unlock this" into work the runner already knows how to do: resolve the quest chain to
/// the target and put it on the priority list, prerequisites first. Shared by the Tribes and
/// Deliveries windows.
/// </summary>
public sealed class UnlockPlanner
{
    private readonly QuestCatalog _catalog;
    private readonly IQuestStateReader _quests;
    private readonly PriorityList _priority;
    private readonly Func<ushort, bool> _hasPath;
    private readonly Action<string> _log;

    public UnlockPlanner(QuestCatalog catalog, IQuestStateReader quests, PriorityList priority, Func<ushort, bool> hasPath, Action<string> log)
    {
        _catalog = catalog;
        _quests = quests;
        _priority = priority;
        _hasPath = hasPath;
        _log = log;
    }

    /// <summary>What it would take, without changing anything.</summary>
    public ChainPlan Plan(ushort targetQuestId)
        => QuestChain.Resolve(targetQuestId, _catalog, _quests.IsComplete, _hasPath, _quests.Character());

    /// <summary>
    /// Queue the chain: every step that is not already listed goes onto the priority list in
    /// order, so the runner takes them at the next quest boundary. Returns what was queued.
    /// </summary>
    public ChainPlan Queue(ushort targetQuestId, string what)
    {
        var plan = Plan(targetQuestId);
        if (plan.AlreadyDone || plan.Steps.Count == 0)
            return plan;

        var added = 0;
        foreach (var step in plan.Steps)
            if (_priority.Add(step.QuestId))
                added++;

        _log($"Unlock {what}: queued {added} of {plan.Steps.Count} quest(s) — " +
             string.Join(" → ", plan.Steps.Select(s => s.Name)));
        return plan;
    }
}
