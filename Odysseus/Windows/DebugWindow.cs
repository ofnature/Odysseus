using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Quest;

namespace Odysseus.Windows;

/// <summary>
/// Raw dump of what the quest reader sees. This is where P0 gets field-verified: the numbers here
/// are compared against Questionable's own <c>GetCurrentQuestId</c> / <c>GetCurrentStepData</c>
/// while both plugins are loaded, and any disagreement is a bug in our reader.
/// </summary>
public sealed class DebugWindow : Window
{
    private readonly IQuestStateReader _quests;

    public DebugWindow(IQuestStateReader quests)
        : base("Odysseus Debug##OdysseusDebug")
    {
        _quests = quests;
        Size = new Vector2(480, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        OdysseusTheme.SectionHeader("QUEST STATE (LIVE)");
        var accepted = _quests.ReadAccepted();
        if (accepted.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Reader returned nothing.");
            return;
        }

        if (ImGui.BeginTable("##quests", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Quest", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Seq", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("Variables");
            ImGui.TableHeadersRow();
            foreach (var q in accepted)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(q.QuestId.ToString());
                ImGui.TableNextColumn(); ImGui.Text(q.Sequence.ToString());
                ImGui.TableNextColumn(); ImGui.Text(string.Join(' ', q.Variables.ToArray()));
            }
            ImGui.EndTable();
        }
    }
}
