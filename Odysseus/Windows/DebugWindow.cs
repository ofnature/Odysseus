using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Ipc;
using Odysseus.Services.Quest;

namespace Odysseus.Windows;

/// <summary>
/// Raw dump of what the quest reader sees, next to what Questionable sees.
///
/// <para>
/// This is where P0 gets field-verified. Both plugins read the same <c>QuestManager</c> memory
/// through different code; with a quest accepted, every row must agree on accepted / complete,
/// and Questionable's current quest must be one of ours. A disagreement is a bug in our reader,
/// found here in seconds instead of mid-run.
/// </para>
/// </summary>
public sealed class DebugWindow : OdysseusWindow
{
    private readonly IQuestStateReader _quests;
    private readonly QuestCatalog _catalog;
    private readonly QuestionableOracle _oracle;
    private readonly PluginPresence _presence;

    public DebugWindow(IQuestStateReader quests, QuestCatalog catalog, QuestionableOracle oracle, PluginPresence presence)
        : base("Odysseus Debug##OdysseusDebug")
    {
        _quests = quests;
        _catalog = catalog;
        _oracle = oracle;
        _presence = presence;
        Size = new Vector2(640, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawOracleLine();
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

    private void DrawOracleLine()
    {
        var available = _oracle.Available;
        OdysseusTheme.DependencyChip("Questionable oracle", available, required: false);
        if (!available)
        {
            ImGui.SameLine();
            // Two different situations, two different fixes: load the plugin, or fix our gate types.
            ImGui.TextColored(_presence.Questionable ? OdysseusTheme.StatusYellow : OdysseusTheme.TextDisabled,
                _presence.Questionable
                    ? "— plugin loaded but IPC not answering (gate signature mismatch on our side)"
                    : "— plugin not loaded in this client; differential check off");
            return;
        }

        var current = _oracle.CurrentQuestId();
        ImGui.SameLine(0f, 16f);
        ImGui.TextColored(OdysseusTheme.TextSecondary,
            current is { } id ? $"current: {id} ({_catalog.NameOf(id)})" : "current: none");
        ImGui.SameLine(0f, 16f);
        ImGui.TextColored(OdysseusTheme.TextSecondary, $"running: {(_oracle.IsRunning() == true ? "yes" : "no")}");
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

        var oracleUp = _oracle.Available;
        var columns = oracleUp ? 7 : 5;
        if (!ImGui.BeginTable("##quests", columns, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Id");
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Seq");
        ImGui.TableSetupColumn("Vars");
        ImGui.TableSetupColumn("MSQ");
        if (oracleUp)
        {
            ImGui.TableSetupColumn("Q.acc");
            ImGui.TableSetupColumn("Q.cur");
        }
        ImGui.TableHeadersRow();

        var oracleCurrent = oracleUp ? _oracle.CurrentQuestId() : null;
        foreach (var q in accepted)
        {
            var listing = _catalog.ById(q.QuestId);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text(q.QuestId.ToString());
            ImGui.TableNextColumn(); ImGui.Text(listing?.Name ?? "?");
            ImGui.TableNextColumn(); ImGui.Text(q.Sequence.ToString());
            ImGui.TableNextColumn(); ImGui.Text(string.Join(' ', q.Variables.ToArray()));
            ImGui.TableNextColumn(); ImGui.Text(listing?.IsMainScenario == true ? "●" : "");
            if (oracleUp)
            {
                // Agreement is the whole point: green when Questionable sees the same thing.
                var acc = _oracle.IsQuestAccepted(q.QuestId);
                ImGui.TableNextColumn();
                ImGui.TextColored(acc == true ? OdysseusTheme.StatusGreen : OdysseusTheme.StatusRed,
                    acc is null ? "?" : acc == true ? "✓" : "✗");
                ImGui.TableNextColumn();
                ImGui.TextColored(OdysseusTheme.WakeFoam, oracleCurrent == q.QuestId ? "●" : "");
            }
        }
        ImGui.EndTable();
    }
}
