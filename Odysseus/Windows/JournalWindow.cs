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
    private readonly Func<IReadOnlyList<ushort>, IReadOnlyList<MaterialNeed>> _materials;
    private readonly Func<ushort, IReadOnlyList<MaterialNeed>> _questMaterials;
    private readonly Func<ushort, bool> _namesItems;
    private readonly Func<int> _outdatedPaths;

    private string _search = string.Empty;
    private bool _hideCompleted = true;
    private bool _onlyWithPaths = true;
    private string _status = string.Empty;
    private readonly Dictionary<ushort, ChainPlan?> _plans = new();
    private DateTime _plansAt;
    private static readonly TimeSpan PlanLife = TimeSpan.FromSeconds(3);

    /// <summary>The line whose bill of materials is on screen, and the bill itself.</summary>
    private string _billFor = string.Empty;
    private IReadOnlyList<MaterialNeed> _bill = [];
    private IReadOnlyList<ushort> _billQuests = [];

    /// <summary>The single quest whose own items are open, and them. 0 for none.</summary>
    private ushort _questBillFor;
    private IReadOnlyList<MaterialNeed> _questBill = [];

    /// <summary>When the open lists last re-read the bags. They go stale while you are out shopping.</summary>
    private DateTime _billAt;

    /// <param name="materials">Quest ids → what running all of them will ask you to bring, as a shopping list.</param>
    /// <param name="questMaterials">One quest id → its own items, in the order its steps want them.</param>
    /// <param name="namesItems">Whether a quest names any item at all — cheap enough to ask per row.</param>
    /// <param name="outdatedPaths">How many stored paths an older converter wrote.</param>
    public JournalWindow(QuestCatalog catalog, IQuestStateReader quests, UnlockPlanner unlock, PriorityList priority,
        Func<ushort, bool> hasPath, Func<IReadOnlyList<ushort>, IReadOnlyList<MaterialNeed>> materials,
        Func<ushort, IReadOnlyList<MaterialNeed>> questMaterials, Func<ushort, bool> namesItems,
        Func<int> outdatedPaths)
        : base("Odysseus Journal##OdysseusJournal")
    {
        _catalog = catalog;
        _quests = quests;
        _unlock = unlock;
        _priority = priority;
        _hasPath = hasPath;
        _materials = materials;
        _questMaterials = questMaterials;
        _namesItems = namesItems;
        _outdatedPaths = outdatedPaths;
        Size = new Vector2(760, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 320), MaximumSize = new Vector2(1400, 1400) };
    }

    public override void Draw()
    {
        RefreshOpenBills();

        // A path an older converter wrote degrades in silence: its Craft step is stored as Unknown,
        // so it names no item, offers no list and stops the run saying a verb is unimplemented that
        // has since been implemented. Nothing about that is guessable from the symptom.
        if (_outdatedPaths() is > 0 and var stale)
        {
            ImGui.TextColored(OdysseusTheme.StatusYellow,
                $"{stale} converted path{(stale == 1 ? "" : "s")} predate{(stale == 1 ? "s" : "")} the current converter.");
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextSecondary,
                "Re-import in Settings — until then they carry no crafts, purchases, classes or gathering.");
            ImGui.Separator();
        }

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

            // "Disciple of the Hand Quests" is one journal category holding every crafter — 126
            // quests of eight interleaved lines. Split by class when the category actually holds
            // more than one, and leave single-class categories flat rather than adding a tier that
            // says nothing. Known before the header is drawn, because the bill belongs on whichever
            // row is the line: the job's when there is one, the category's when there is not.
            var jobs = rows.Select(q => q.JobName).Where(j => j.Length > 0).Distinct().ToList();

            ImGui.SetNextItemOpen(_search.Length > 0, ImGuiCond.FirstUseEver);
            var categoryOpen = ImGui.TreeNode($"{category.Key}  ({rows.Count})##c{name}{category.Key}");
            QueueAllButton($"cat{name}{category.Key}", category.Key, rows);
            if (jobs.Count <= 1)
                MaterialsButton($"cat{name}{category.Key}", category.Key, rows);
            if (!categoryOpen) continue;

            if (jobs.Count > 1)
            {
                foreach (var job in rows.GroupBy(q => q.JobName).OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key))
                {
                    var label = job.Key.Length > 0 ? job.Key : "Other";
                    var line = job.ToList();
                    ImGui.SetNextItemOpen(_search.Length > 0, ImGuiCond.FirstUseEver);
                    var jobOpen = ImGui.TreeNode($"{label}  ({line.Count})##j{name}{category.Key}{label}");
                    QueueAllButton($"job{name}{category.Key}{label}", label, line);
                    MaterialsButton($"job{name}{category.Key}{label}", label, line);
                    if (!jobOpen) continue;
                    if (_billFor == label)
                        DrawBill(label);
                    foreach (var quest in Ordered(line))
                        DrawQuest(quest);
                    ImGui.TreePop();
                }
            }
            else
            {
                if (_billFor == category.Key)
                    DrawBill(category.Key);
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
    /// What the line will ask you to bring, worked out from its own steps. Crafter and gatherer
    /// lines are the reason it exists: the whole of a Blacksmith line is buy this, make that, and
    /// knowing the list up front is the difference between running it unattended and watching it
    /// stop at every second quest.
    /// </summary>
    private void MaterialsButton(string id, string label, IReadOnlyList<QuestListing> quests)
    {
        ImGui.SameLine();
        if (!OdysseusTheme.IconTextButton(FontAwesomeIcon.Box, $"Items##items{id}", OdysseusTheme.BgPanel,
                $"What {label} needs: everything its steps buy, craft or gather, against your bags and the FC chest.",
                new Vector2(58, 20)))
            return;

        if (_billFor == label)
        {
            _billFor = string.Empty;   // a second press closes it
            return;
        }
        _billFor = label;
        _billQuests = quests.Where(q => _hasPath(q.QuestId)).Select(q => q.QuestId).ToList();
        _bill = _materials(_billQuests);
        _billAt = DateTime.UtcNow;
        if (_bill.Count == 0)
            _status = $"{label}: nothing in its converted paths names an item.";
    }

    /// <summary>
    /// Re-read what is open. The counts are the point of the list and they move while you are
    /// looking at it — buying, crafting, or emptying the chest — so an open list that never
    /// changes is worse than no list.
    /// </summary>
    private void RefreshOpenBills()
    {
        var now = DateTime.UtcNow;
        if (now - _billAt <= PlanLife)
            return;
        _billAt = now;
        if (_billFor.Length > 0 && _billQuests.Count > 0)
            _bill = _materials(_billQuests);
        if (_questBillFor != 0)
            _questBill = _questMaterials(_questBillFor);
    }

    private void DrawBill(string label)
    {
        if (_bill.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Nothing to bring — or no converted paths to read it from.");
            return;
        }

        var missing = _bill.Count(n => n.Missing > 0);
        ImGui.TextColored(OdysseusTheme.TextSecondary,
            missing == 0
                ? $"{label}: you already have everything ({_bill.Count} items)."
                : $"{label}: {missing} of {_bill.Count} items still to find.");

        if (!ImGui.BeginTable($"##bill{label}", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
            return;
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Need");
        ImGui.TableSetupColumn("Have");
        ImGui.TableSetupColumn("FC chest");
        ImGui.TableSetupColumn("From");
        ImGui.TableHeadersRow();

        foreach (var need in _bill)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(need.Missing > 0 ? OdysseusTheme.TextPrimary : OdysseusTheme.TextDisabled, need.Name);
            ImGui.TableNextColumn();
            ImGui.Text(need.Needed.ToString());
            ImGui.TableNextColumn();
            // A quest item cannot be counted at all; "?" is honest where "0" would not be.
            ImGui.TextColored(need.Missing > 0 ? OdysseusTheme.StatusYellow : OdysseusTheme.GreenDark,
                need.Held < 0 ? "?" : need.Held.ToString());
            ImGui.TableNextColumn();
            if (need.InChest > 0)
                ImGui.TextColored(need.CoveredByChest ? OdysseusTheme.GreenDark : OdysseusTheme.TextSecondary,
                    need.InChest.ToString());
            else
                ImGui.TextColored(OdysseusTheme.TextDisabled, "–");
            ImGui.TableNextColumn();
            ImGui.TextColored(OdysseusTheme.TextSecondary, Describe(need.Source));
        }
        ImGui.EndTable();
        ImGui.TextColored(OdysseusTheme.TextDisabled,
            "The FC chest column only counts pages the game has loaded — open the chest and view each tab to fill it in.");
    }

    private static string Describe(MaterialSource source) => source switch
    {
        MaterialSource.Vendor => "vendor",
        MaterialSource.Crafted => "Artisan",
        MaterialSource.Ingredient => "ingredient",
        MaterialSource.Gathered => "GatherBuddy",
        _ => "quest only",
    };

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

        // Only quests that actually want something get a list; on a line of twenty-one, the ones
        // that need nothing are most of them and a row of dead buttons would say nothing.
        if (!_namesItems(quest.QuestId))
            return;

        ImGui.SameLine();
        if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Box, $"##items{quest.QuestId}", OdysseusTheme.BgPanel,
                $"What {quest.Name} alone needs, in the order its steps want it.",
                new Vector2(28, 20)))
        {
            var open = _questBillFor == quest.QuestId;
            _questBillFor = open ? (ushort)0 : quest.QuestId;
            _questBill = open ? [] : _questMaterials(quest.QuestId);
            _billAt = DateTime.UtcNow;
        }

        if (_questBillFor == quest.QuestId)
            DrawQuestBill(quest);
    }

    /// <summary>
    /// One quest's own items, indented under it. Deliberately not the same table as the line's:
    /// this is three or four rows read as "buy this, then make that", so it stays a list of lines
    /// rather than a grid with headers.
    /// </summary>
    private void DrawQuestBill(QuestListing quest)
    {
        ImGui.Indent(28f);
        if (_questBill.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Nothing — its path names no items.");
            ImGui.Unindent(28f);
            return;
        }

        foreach (var need in _questBill)
        {
            var have = need.Held < 0 ? "?" : need.Held.ToString();
            var chest = need.InChest > 0 ? $", {need.InChest} in the FC chest" : string.Empty;
            ImGui.TextColored(need.Missing > 0 ? OdysseusTheme.StatusYellow : OdysseusTheme.GreenDark,
                need.Missing > 0 ? $"need {need.Missing}" : "have it");
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextPrimary, $"{need.Needed} × {need.Name}");
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextDisabled, $"· {Describe(need.Source)} · have {have}{chest}");
        }
        ImGui.Unindent(28f);
    }
}
