using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Odysseus.Config;
using Odysseus.Services.Tribes;

namespace Odysseus.Windows;

/// <summary>
/// The allied societies: rank, reputation, how many dailies are left today, and a Run button per
/// tribe. Combat tribes run now; crafter and gatherer tribes are listed but wait for the
/// craft/gather handoffs. The day's allowance (12) is shown at the top.
/// </summary>
public sealed class TribesWindow : OdysseusWindow
{
    private readonly OdysseusConfig _config;
    private readonly TribeCatalog _catalog;
    private readonly ITribeState _state;
    private readonly TribeRunner _runner;
    private readonly Action<byte> _enqueue;
    private readonly Action _stopAll;

    public TribesWindow(OdysseusConfig config, TribeCatalog catalog, ITribeState state, TribeRunner runner, Action<byte> enqueue, Action stopAll)
        : base("Odysseus Tribes##OdysseusTribes")
    {
        _config = config;
        _catalog = catalog;
        _state = state;
        _runner = runner;
        _enqueue = enqueue;
        _stopAll = stopAll;
        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 260), MaximumSize = new Vector2(900, 1200) };
    }

    public override void Draw()
    {
        ImGui.TextColored(OdysseusTheme.TextSecondary, $"Daily allowance left: {_state.AllowanceLeft} / 12");
        if (_runner.State is not (TribeRunState.Idle or TribeRunState.Done or TribeRunState.Faulted))
        {
            ImGui.SameLine(0f, 16f);
            ImGui.TextColored(OdysseusTheme.WakeFoam, $"● {_runner.StatusLine}");
            ImGui.SameLine();
            if (OdysseusTheme.SolidButton("Stop##tribes", OdysseusTheme.RedDark, new Vector2(60, 22)))
                _stopAll();
        }
        else if (_runner.State == TribeRunState.Faulted)
        {
            ImGui.SameLine(0f, 16f);
            ImGui.TextColored(OdysseusTheme.StatusRed, _runner.StatusLine);
        }
        ImGui.Separator();

        if (!ImGui.BeginTable("##tribes", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Society", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableSetupColumn("Today", ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableSetupColumn("##run", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableHeadersRow();

        foreach (var tribe in _catalog.All.OrderBy(t => t.ExpansionId).ThenBy(t => t.Id))
            DrawRow(tribe);

        ImGui.EndTable();
        ImGui.Spacing();
        ImGui.TextColored(OdysseusTheme.TextDisabled, "Combat societies run now. Crafter and gatherer dailies wait for the craft/gather handoffs.");
    }

    private void DrawRow(TribeInfo tribe)
    {
        var s = _state.Read(tribe);
        var muted = s.Unlocked ? OdysseusTheme.TextSecondary : OdysseusTheme.TextDisabled;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(s.Unlocked ? OdysseusTheme.TextPrimary : OdysseusTheme.TextDisabled, tribe.Name);

        ImGui.TableNextColumn();
        ImGui.TextColored(tribe.IsRunnableKind ? muted : OdysseusTheme.StatusYellow, tribe.Kind.ToString());

        ImGui.TableNextColumn();
        if (!s.Unlocked)
            ImGui.TextColored(OdysseusTheme.TextDisabled, "locked");
        else
        {
            ImGui.TextColored(muted, $"{s.Rank}/{tribe.MaxRank}");
            if (s.ReputationNeeded > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(OdysseusTheme.TextDisabled, $"{s.Reputation}/{s.ReputationNeeded}");
            }
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(s.SlotsLeft > 0 ? muted : OdysseusTheme.TextDisabled, s.Unlocked ? $"{s.TakenToday}/3" : "—");

        ImGui.TableNextColumn();
        var canRun = _config.Enabled && tribe.IsRunnableKind && s.Unlocked && (s.SlotsLeft > 0 || s.AcceptedDailies.Count > 0)
                     && _state.AllowanceLeft > 0 && _runner.State is TribeRunState.Idle or TribeRunState.Done or TribeRunState.Faulted;
        using (ImRaii.Disabled(!canRun))
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Play, "Run", OdysseusTheme.GreenDark, RunTip(tribe, s), new Vector2(58, 22)))
                _enqueue(tribe.Id);
        }
    }

    private string RunTip(TribeInfo tribe, TribeStanding s)
    {
        if (!_config.Enabled) return "Enable Odysseus first.";
        if (!tribe.IsRunnableKind) return $"{tribe.Kind} dailies aren't automated yet.";
        if (!s.Unlocked) return "Not unlocked.";
        if (_state.AllowanceLeft <= 0) return "No daily allowance left today.";
        if (s.SlotsLeft <= 0 && s.AcceptedDailies.Count == 0) return "Nothing left for this society today.";
        return "Accept and run this society's dailies.";
    }
}
