using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Odysseus.Config;
using Odysseus.Services.Gathering;

namespace Odysseus.Windows;

/// <summary>
/// The gather lists in a window of their own: several named lists, each item with a bag-count
/// target, run zone by zone through the own gatherer. Toggled from the main window.
/// </summary>
public sealed class GatherListsWindow : OdysseusWindow
{
    private readonly OdysseusConfig _config;
    private readonly Action _save;
    private readonly GatherListRunner _runner;
    private readonly GatherableIndex _gatherables;
    private readonly IOwnGatherer _gatherer;
    private readonly Func<uint, int> _itemCount;
    private readonly Func<bool> _runActive;
    private int _gatherListIndex;
    private string _gatherFilter = string.Empty;

    public GatherListsWindow(OdysseusConfig config, Action save, GatherListRunner runner, GatherableIndex gatherables,
        IOwnGatherer gatherer, Func<uint, int> itemCount, Func<bool> runActive)
        : base("Odysseus Gather lists##OdysseusGather")
    {
        _config = config;
        _save = save;
        _runner = runner;
        _gatherables = gatherables;
        _gatherer = gatherer;
        _itemCount = itemCount;
        _runActive = runActive;
        Size = new Vector2(560, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 220), MaximumSize = new Vector2(1000, 1200) };
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        try { DrawGatherLists(); }
        finally { ImGui.PopStyleVar(); }
    }

    private void DrawGatherLists()
    {
        var lists = _config.GatherLists;

        if (lists.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "No lists yet.");
            ImGui.SameLine();
            if (ImGui.SmallButton("+ New list")) { lists.Add(new GatherList()); _save(); }
            return;
        }

        if (_gatherListIndex >= lists.Count) _gatherListIndex = lists.Count - 1;
        var list = lists[_gatherListIndex];

        ImGui.SetNextItemWidth(150f);
        if (ImGui.BeginCombo("##gatherlist", list.Name))
        {
            for (var i = 0; i < lists.Count; i++)
                if (ImGui.Selectable($"{lists[i].Name}##gl{i}", i == _gatherListIndex)) _gatherListIndex = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ New")) { lists.Add(new GatherList()); _gatherListIndex = lists.Count - 1; _save(); return; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete")) { lists.RemoveAt(_gatherListIndex); _save(); return; }

        var name = list.Name;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputText("##glname", ref name, 48)) list.Name = name;
        if (ImGui.IsItemDeactivatedAfterEdit()) _save();
        ImGui.SameLine();
        var enabled = list.Enabled;
        if (ImGui.Checkbox("Enabled##gl", ref enabled)) { list.Enabled = enabled; _save(); }
        ImGui.SameLine();
        var prune = list.RemoveCompleted;
        if (ImGui.Checkbox("Remove completed##gl", ref prune)) { list.RemoveCompleted = prune; _save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drop items that have reached their target when a run ends.");

        OdysseusTheme.BeginCard("gathercard");
        if (ImGui.BeginTable("##gatheritems", 4, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("count", ImGuiTableColumnFlags.WidthFixed, 118f);
            ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("rm", ImGuiTableColumnFlags.WidthFixed, 26f);
            var current = _runner.CurrentItem;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var item = list.Items[i];
                using var rowId = Dalamud.Interface.Utility.Raii.ImRaii.PushId(i);
                var held = _itemCount(item.ItemId);
                var done = held >= item.TargetCount;
                ImGui.TableNextRow();
                if (current == item.ItemId)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(OdysseusTheme.WakeWash));

                ImGui.TableNextColumn();
                ImGui.TextColored(done ? OdysseusTheme.StatusGreen : OdysseusTheme.TextDisabled, "●");
                ImGui.SameLine(0f, 6f);
                ImGui.TextColored(done ? OdysseusTheme.TextDisabled : OdysseusTheme.TextPrimary, _gatherables.NameOf(item.ItemId));

                ImGui.TableNextColumn();
                ImGui.TextColored(done ? OdysseusTheme.StatusGreen : OdysseusTheme.TextPrimary, $"{held} /");
                ImGui.SameLine(0f, 4f);
                var target = item.TargetCount;
                ImGui.SetNextItemWidth(64f);
                if (ImGui.InputInt("##target", ref target, 0)) item.TargetCount = Math.Max(1, target);
                if (ImGui.IsItemDeactivatedAfterEdit()) _save();

                ImGui.TableNextColumn();
                ImGui.TextColored(OdysseusTheme.TextDisabled, _gatherer.Where(item.ItemId));

                ImGui.TableNextColumn();
                if (OdysseusTheme.IconButton("rm", FontAwesomeIcon.Trash, "Remove from the list", new Vector2(22, 20)))
                {
                    list.Items.RemoveAt(i);
                    _save();
                    break;
                }
            }
            ImGui.EndTable();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##gatheradd", "Add an item… (part of its name)", ref _gatherFilter, 64);
        if (_gatherFilter.Length >= 2)
        {
            foreach (var (id, itemName) in _gatherables.Search(_gatherFilter, 8))
            {
                if (list.Items.Any(x => x.ItemId == id))
                    continue;
                if (ImGui.Selectable($"{itemName}##add{id}"))
                {
                    list.Items.Add(new GatherListItem { ItemId = id });
                    _gatherFilter = string.Empty;
                    _save();
                    break;
                }
                ImGui.SameLine();
                ImGui.TextColored(OdysseusTheme.TextDisabled, _gatherer.Where(id));
            }
        }
        OdysseusTheme.EndCard();

        ImGui.SetNextItemWidth(190f);
        if (ImGui.BeginCombo("Afterwards", GatherHomeCommands.Label(_config.GatherHome)))
        {
            foreach (var home in Enum.GetValues<GatherHome>())
                if (ImGui.Selectable(GatherHomeCommands.Label(home), home == _config.GatherHome))
                {
                    _config.GatherHome = home;
                    _save();
                }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Where a finished run takes you. Lifestream handles the inn and the estates; Return is the action, on its own cooldown.");

        var runner = _runner;
        if (runner.State == GatherListRunState.Running)
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Stop, "Stop", OdysseusTheme.RedDark, "Stop gathering; the gathering window is closed first."))
                runner.Stop();
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.WakeFoam, runner.Status);
        }
        else
        {
            var canRun = _config.Enabled && _config.OwnGathering && !_runActive() && lists.Any(l => l.Enabled && l.Items.Count > 0);
            using (ImRaii.Disabled(!canRun))
            {
                if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Play, "Gather", OdysseusTheme.GreenDark, "Gather everything short on the enabled lists, zone by zone.") && canRun)
                    runner.Begin(lists);
            }
            if (ImGui.IsItemHovered() && !canRun)
                ImGui.SetTooltip(!_config.OwnGathering ? "Own gathering is off in Settings." : _runActive() ? "A run is active." : "Nothing on an enabled list.");
            if (runner.Status.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(OdysseusTheme.TextDisabled, runner.Status);
            }
        }
    }
}
