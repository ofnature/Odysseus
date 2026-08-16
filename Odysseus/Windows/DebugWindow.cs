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

    public DebugWindow(IQuestStateReader quests, QuestCatalog catalog)
        : base("Odysseus Debug##OdysseusDebug")
    {
        _quests = quests;
        _catalog = catalog;
        Size = new Vector2(640, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        OdysseusTheme.SectionHeader("STORY FRONTIER");
        DrawFrontier();
        OdysseusTheme.SectionHeader("QUEST STATE (LIVE, FROM QUESTMANAGER)");
        DrawQuestTable();
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
