using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Quest;

namespace Odysseus.Windows;

/// <summary>
/// The journal, grouped the way the game groups it, so a quest line can be found and queued.
///
/// <para>
/// This adds no engine: <see cref="QuestChain"/> already resolves prerequisites for any quest and
/// <see cref="UnlockPlanner"/> already puts a chain on the priority list — that is how allied
/// societies, delivery clients and aether-current quests are unlocked. What was missing was a way
/// to <i>find</i> a chain. Trial, raid and hard-mode unlocks live under "Chronicles of a New Era",
/// which is a journal section like any other, so grouping by section is the whole feature.
/// </para>
///
/// <para>
/// Queueing the last quest in a line is enough — the chain resolver walks backwards and queues
/// whatever has to come first, so there is no need to pick out the starting quest yourself.
/// </para>
/// </summary>
public sealed class JournalWindow : OdysseusWindow
{
    /// <summary>Sections offered first, because they are what people come here for.</summary>
    private static readonly string[] Featured = ["Chronicles of a New Era", "Sidequests", "Class & Job Quests"];

    private readonly QuestCatalog _catalog;
    private readonly IQuestStateReader _quests;
    private readonly UnlockPlanner _unlock;
    private readonly PriorityList _priority;
    private readonly Func<ushort, bool> _hasPath;

    private string _search = string.Empty;
    private bool _hideCompleted = true;
    private bool _onlyWithPaths = true;
    private string _status = string.Empty;
    private readonly Dictionary<ushort, ChainPlan?> _plans = new();
    private DateTime _plansAt;
    private static readonly TimeSpan PlanLife = TimeSpan.FromSeconds(3);

    public JournalWindow(QuestCatalog catalog, IQuestStateReader quests, UnlockPlanner unlock, PriorityList priority,
        Func<ushort, bool> hasPath)
        : base("Odysseus Journal##OdysseusJournal")
    {
        _catalog = catalog;
        _quests = quests;
        _unlock = unlock;
        _priority = priority;
        _hasPath = hasPath;
        Size = new Vector2(760, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 320), MaximumSize = new Vector2(1400, 1400) };
    }

    public override void Draw()
    {
        ImGui.SetNextItemWidth(280f);
        ImGui.InputTextWithHint("##search", "Search quests and categories", ref _search, 64);
        ImGui.SameLine(0f, 10f);
        ImGui.Checkbox("Hide completed", ref _hideCompleted);
        ImGui.SameLine(0f, 10f);
        ImGui.Checkbox("Only with paths", ref _onlyWithPaths);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A quest with no converted path cannot be run, only queued and waited on.");
        ImGui.Separator();

        var sections = _catalog.All
            .Where(Matches)
            .GroupBy(q => q.Section.Length > 0 ? q.Section : "Uncategorised")
            .OrderBy(g => Array.IndexOf(Featured, g.Key) is var i && i >= 0 ? i : Featured.Length)
            .ThenBy(g => g.Key)
            .ToList();

        if (sections.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Nothing matches.");
            return;
        }

        foreach (var section in sections)
            DrawSection(section.Key, section.ToList());

        ImGui.Spacing();
        if (_status.Length > 0)
            OdysseusTheme.TextWrappedColored(OdysseusTheme.TextSecondary, _status);
        OdysseusTheme.TextWrappedColored(OdysseusTheme.TextDisabled,
            "Queue puts a quest and everything it needs first onto the priority list, in order. " +
            "Queueing the last quest of a line is enough — the chain is resolved backwards for you.");
    }

    private bool Matches(QuestListing quest)
    {
        if (quest.Name.Length == 0) return false;
        if (_hideCompleted && _quests.IsComplete(quest.QuestId)) return false;
        if (_onlyWithPaths && !_hasPath(quest.QuestId)) return false;
        if (_search.Length == 0) return true;
        return quest.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
               || quest.Category.Contains(_search, StringComparison.OrdinalIgnoreCase)
               || quest.Section.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawSection(string name, List<QuestListing> quests)
    {
        // Open the featured sections and anything the search narrowed to; leave the rest folded.
        var open = _search.Length > 0 || Array.IndexOf(Featured, name) == 0;
        ImGui.SetNextItemOpen(open, ImGuiCond.FirstUseEver);
        if (!ImGui.CollapsingHeader($"{name}  ({quests.Count})##s{name}"))
            return;

        ImGui.Indent(12f);
        foreach (var category in quests
                     .GroupBy(q => q.Category.Length > 0 ? q.Category : name)
                     .OrderBy(g => g.Key))
        {
            var rows = category.ToList();
            ImGui.SetNextItemOpen(_search.Length > 0, ImGuiCond.FirstUseEver);
            var categoryOpen = ImGui.TreeNode($"{category.Key}  ({rows.Count})##c{name}{category.Key}");
            QueueAllButton($"cat{name}{category.Key}", category.Key, rows);
            if (!categoryOpen) continue;

            // "Disciple of the Hand Quests" is one journal category holding every crafter — 126
            // quests of eight interleaved lines. Split by class when the category actually holds
            // more than one, and leave single-class categories flat rather than adding a tier that
            // says nothing.
            var jobs = rows.Select(q => q.JobName).Where(j => j.Length > 0).Distinct().ToList();
            if (jobs.Count > 1)
            {
                foreach (var job in rows.GroupBy(q => q.JobName).OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key))
                {
                    var label = job.Key.Length > 0 ? job.Key : "Other";
                    var line = job.ToList();
                    ImGui.SetNextItemOpen(_search.Length > 0, ImGuiCond.FirstUseEver);
                    var jobOpen = ImGui.TreeNode($"{label}  ({line.Count})##j{name}{category.Key}{label}");
                    QueueAllButton($"job{name}{category.Key}{label}", label, line);
                    if (!jobOpen) continue;
                    foreach (var quest in Ordered(line))
                        DrawQuest(quest);
                    ImGui.TreePop();
                }
            }
            else
            {
                foreach (var quest in Ordered(rows))
                    DrawQuest(quest);
            }
            ImGui.TreePop();
        }
        ImGui.Unindent(12f);
    }

    /// <summary>Level then id — the order a line is actually done in.</summary>
    private static IEnumerable<QuestListing> Ordered(IEnumerable<QuestListing> quests)
        => quests.OrderBy(q => q.ClassJobLevel).ThenBy(q => q.QuestId);

    /// <summary>
    /// Queue a whole line, on the group's own header row.
    ///
    /// <para>
    /// Quests go in the order the line is done, and each one still runs through the chain resolver,
    /// so a prerequisite outside this group is pulled in with it. The priority list refuses
    /// duplicates, which is what makes queueing twenty-one overlapping chains safe.
    /// </para>
    ///
    /// <para>Only what is on screen is queued — the completed and no-path filters still apply.</para>
    /// </summary>
    private void QueueAllButton(string id, string label, IReadOnlyList<QuestListing> quests)
    {
        if (quests.Count == 0) return;
        ImGui.SameLine();

        // Deliberately not pre-checking which are runnable: that is a chain resolution each, and a
        // category like Sidequests holds over a thousand. The filtering happens on click instead.
        if (!OdysseusTheme.IconTextButton(FontAwesomeIcon.Plus, $"All##all{id}", OdysseusTheme.GreenDark,
                $"Queue all {quests.Count} of {label}, in order, with anything they need first.\n" +
                "Anything already queued or not yet reachable is skipped.",
                new Vector2(52, 20)))
            return;

        var runnable = quests.Where(q => _unlock.Plan(q.QuestId) is { IsRunnable: true }).ToList();
        if (runnable.Count == 0)
        {
            _status = $"{label}: nothing here can be queued yet.";
            return;
        }

        var before = _priority.Count;
        foreach (var quest in Ordered(runnable))
            _unlock.Queue(quest.QuestId, quest.Name);
        var added = _priority.Count - before;

        _status = added == 0
            ? $"{label}: everything was already on the priority list."
            : $"{label}: queued {added} quest(s)" +
              (added > runnable.Count ? $" — {added - runnable.Count} pulled in as prerequisites." : ".");
        _plans.Clear();
    }

    /// <summary>
    /// Chain plans, memoised for a moment.
    ///
    /// <para>
    /// Every visible row wants one, and resolving a chain is a graph walk — doing that per row per
    /// frame is what makes an expanded category crawl. Plans only move when a quest completes or
    /// something is queued, so a short-lived cache costs nothing in accuracy.
    /// </para>
    /// </summary>
    private ChainPlan? PlanFor(ushort questId)
    {
        var now = DateTime.UtcNow;
        if (now - _plansAt > PlanLife)
        {
            _plans.Clear();
            _plansAt = now;
        }
        if (_plans.TryGetValue(questId, out var cached)) return cached;
        return _plans[questId] = _unlock.Plan(questId);
    }

    private void DrawQuest(QuestListing quest)
    {
        var plan = PlanFor(quest.QuestId);
        var runnable = plan is { IsRunnable: true };

        using (ImRaii.Disabled(!runnable))
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Plus, $"Queue##q{quest.QuestId}",
                    OdysseusTheme.GreenDark,
                    plan is null ? "Nothing to queue." :
                    runnable ? $"Queue {plan.Summary}." : $"Cannot queue: {plan.Summary}.",
                    new Vector2(84, 20)))
            {
                var queued = _unlock.Queue(quest.QuestId, quest.Name);
                _status = $"Queued {queued.Steps.Count} quest(s) for {quest.Name}" +
                          (queued.Steps.Count > 1 ? $" — {string.Join(" → ", queued.Steps.Select(s => s.Name))}" : ".");
                _plans.Clear();
            }
        }

        ImGui.SameLine();
        ImGui.TextColored(_quests.IsComplete(quest.QuestId) ? OdysseusTheme.TextDisabled : OdysseusTheme.TextPrimary,
            quest.Name);
        ImGui.SameLine();
        ImGui.TextColored(OdysseusTheme.TextDisabled,
            $"#{quest.QuestId} · Lv {quest.ClassJobLevel}" +
            (_hasPath(quest.QuestId) ? string.Empty : " · no path") +
            (plan is { Steps.Count: > 1 } ? $" · {plan.Steps.Count} in chain" : string.Empty));
    }
}
