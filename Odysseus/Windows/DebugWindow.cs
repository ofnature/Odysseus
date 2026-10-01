using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Quest;

namespace Odysseus.Windows;

/// <summary>
/// Raw dump of what the quest reader sees: the story frontier's two sources and every accepted
/// quest with its sequence and variables.
///
/// <para>
/// History: this window once carried a differential check against Questionable's own IPC — an
/// independent reading of the same <c>QuestManager</c> memory — which is how the P0 reader was
/// field-verified (every row agreed, 2026-08-15). The gate passed and the oracle was removed;
/// Odysseus has no dependency on that plugin.
/// </para>
/// </summary>
public sealed class DebugWindow : OdysseusWindow
{
    private readonly IQuestStateReader _quests;
    private readonly QuestCatalog _catalog;

    private readonly Func<IReadOnlyList<(uint ItemId, int Missing)>, string> _grabFromChest;
    private readonly Func<string> _chestStatus;
    private string _grabSaid = string.Empty;

    public DebugWindow(IQuestStateReader quests, QuestCatalog catalog,
        Func<IReadOnlyList<(uint ItemId, int Missing)>, string> grabFromChest, Func<string> chestStatus)
        : base("Odysseus Debug##OdysseusDebug")
    {
        _quests = quests;
        _catalog = catalog;
        _grabFromChest = grabFromChest;
        _chestStatus = chestStatus;
        Size = new Vector2(640, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        OdysseusTheme.SectionHeader("STORY FRONTIER");
        DrawFrontier();
#if DEBUG
        // A dev-build test of the chest split; not something a release should offer.
        OdysseusTheme.SectionHeader("FC CHEST (SPLIT TEST)");
        DrawChestTest();
#endif
        OdysseusTheme.SectionHeader("QUEST STATE (LIVE, FROM QUESTMANAGER)");
        DrawQuestTable();
    }

    /// <summary>A partial withdrawal on demand: 3 out of whatever stack holds Volcanic Rock Salt.</summary>
    private void DrawChestTest()
    {
        const uint VolcanicRockSalt = 6152;
        if (ImGui.Button("Withdraw 3 Volcanic Rock Salt"))
            _grabSaid = _grabFromChest([(VolcanicRockSalt, 3)]);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the FC chest on the page holding it first. Takes exactly 3 — a bigger stack is split through the game's \"how many?\" prompt.");
        ImGui.SameLine();
        ImGui.TextColored(OdysseusTheme.TextSecondary, _grabSaid.Length > 0 ? $"{_grabSaid}  ·  now: {_chestStatus()}" : _chestStatus());
    }

    private void DrawFrontier()
    {
        var agent = _quests.CurrentScenarioQuest();
        var facts = _quests.Character();
        ImGui.TextColored(OdysseusTheme.TextSecondary, "Scenario Guide pointer: ");
        ImGui.SameLine(0f, 0f);
        ImGui.TextColored(OdysseusTheme.TextPrimary, agent is { } a ? $"{a} ({_catalog.NameOf(a)}){(_quests.IsComplete(a) ? " — complete" : "")}" : "none");
        var chain = _catalog.CurrentMainScenario(_quests.IsComplete, facts);
        ImGui.TextColored(OdysseusTheme.TextSecondary, "Chain walk: ");
        ImGui.SameLine(0f, 0f);
        ImGui.TextColored(OdysseusTheme.TextPrimary, chain is { } c ? $"{c.QuestId} ({c.Name}, Lv {c.ClassJobLevel})" : "none — finished or nothing unlocked");
        ImGui.TextColored(OdysseusTheme.TextDisabled,
            $"character: start town {facts.StartTown} · first class {facts.FirstClass} · grand company {facts.GrandCompany}");
    }

    private void DrawQuestTable()
    {
        var accepted = _quests.ReadAccepted();
        ImGui.TextColored(OdysseusTheme.TextSecondary, $"{accepted.Count} accepted · catalog {_catalog.Count} quests");
        if (accepted.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Reader returned nothing.");
            return;
        }

        if (!ImGui.BeginTable("##quests", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Id");
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Seq");
        ImGui.TableSetupColumn("Vars");
        ImGui.TableSetupColumn("MSQ");
        ImGui.TableHeadersRow();

        foreach (var q in accepted)
        {
            var listing = _catalog.ById(q.QuestId);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text(q.QuestId.ToString());
            ImGui.TableNextColumn(); ImGui.Text(listing?.Name ?? "?");
            ImGui.TableNextColumn(); ImGui.Text(q.Sequence.ToString());
            ImGui.TableNextColumn(); ImGui.Text(string.Join(' ', q.Variables.ToArray()));
            ImGui.TableNextColumn(); ImGui.Text(listing?.IsMainScenario == true ? "●" : "");
        }
        ImGui.EndTable();
    }
}
