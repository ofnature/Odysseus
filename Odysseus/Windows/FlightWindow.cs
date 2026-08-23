using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Flight;
using Odysseus.Services.Quest;

namespace Odysseus.Windows;

/// <summary>
/// Flight, zone by zone: how many aether currents you have, which are missing, and which of the two
/// ways each one is obtained.
///
/// <para>
/// The split is the point. Currents handed out by quests go on the priority list and the quest
/// engine runs them — most of them are side quests, which is why running the MSQ alone leaves zones
/// unflyable. Currents lying in the world are walked to directly. Neither half needs to know about
/// the other.
/// </para>
/// </summary>
public sealed class FlightWindow : OdysseusWindow
{
    private readonly AetherCurrentCatalog _catalog;
    private readonly IFlightState _state;
    private readonly CurrentCollector _collector;
    private readonly PriorityList _priority;
    private readonly QuestCatalog _quests;
    private readonly UnlockPlanner _unlock;
    private readonly Func<uint> _territory;

    private string _status = string.Empty;
    private bool _hideFlyable = true;

    public FlightWindow(AetherCurrentCatalog catalog, IFlightState state, CurrentCollector collector,
        PriorityList priority, QuestCatalog quests, UnlockPlanner unlock, Func<uint> territory)
        : base("Odysseus Flight##OdysseusFlight")
    {
        _catalog = catalog;
        _state = state;
        _collector = collector;
        _priority = priority;
        _quests = quests;
        _unlock = unlock;
        _territory = territory;
        Size = new Vector2(720, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 300), MaximumSize = new Vector2(1400, 1400) };
    }

    public override void Draw()
    {
        var zones = _catalog.Progress(_state.IsUnlocked);
        var here = _territory();
        var flyable = zones.Count(z => z.CanFly);

        OdysseusTheme.IdChip($"Flying in {flyable}/{zones.Count} zones");
        ImGui.SameLine(0f, 8f);
        ImGui.Checkbox("Hide finished", ref _hideFlyable);
        if (!_collector.IsFinished)
        {
            ImGui.SameLine(0f, 8f);
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Stop, "Stop", OdysseusTheme.NeutralDark,
                    "Stop collecting.", new Vector2(90, 22)))
                _collector.Stop();
            ImGui.SameLine(0f, 8f);
            ImGui.TextColored(OdysseusTheme.TextSecondary,
                $"[{_collector.Collected}/{_collector.Target}] {_collector.StatusLine}");
        }
        ImGui.Separator();

        if (!ImGui.BeginTable("##zones", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Coerthas Western Highlands__").X);
        ImGui.TableSetupColumn("Currents", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Currents").X + 12f);
        ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("00 quest · 00 ground").X);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        foreach (var zone in zones.OrderByDescending(z => z.TerritoryId == here).ThenBy(z => z.Name))
        {
            if (_hideFlyable && zone.CanFly) continue;
            DrawZone(zone, here);
        }
        ImGui.EndTable();

        ImGui.Spacing();
        if (_status.Length > 0)
            OdysseusTheme.TextWrappedColored(OdysseusTheme.TextSecondary, _status);
        OdysseusTheme.TextWrappedColored(OdysseusTheme.TextDisabled,
            "Flight needs every current in a zone. The quest half is mostly side quests, so the MSQ alone " +
            "leaves zones grounded — Queue puts those chains on the priority list. Collect walks to the loose " +
            "ones, and only works in the zone you are standing in. Positions come from the converted paths, so " +
            "a current no path ever visited is counted but cannot be walked to.");
    }

    private void DrawZone(ZoneFlight zone, uint here)
    {
        // Per-row id scope; identical buttons on sibling rows are dead without it.
        using var rowId = Dalamud.Interface.Utility.Raii.ImRaii.PushId((int)zone.TerritoryId);

        var missing = zone.Missing(_state.IsUnlocked).ToList();
        var fromQuests = missing.Where(c => c.FromQuest).ToList();
        var loose = missing.Where(c => !c.FromQuest).ToList();
        var reachable = loose.Count(c => c.Position is not null);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        if (zone.TerritoryId == here)
        {
            OdysseusTheme.Chip("here", OdysseusTheme.AccentDim, OdysseusTheme.TextPrimary);
            ImGui.SameLine(0f, 4f);
        }
        ImGui.TextColored(zone.CanFly ? OdysseusTheme.TextDisabled : OdysseusTheme.TextPrimary, zone.Name);

        ImGui.TableNextColumn();
        ImGui.TextColored(zone.CanFly ? OdysseusTheme.StatusGreen : OdysseusTheme.TextSecondary,
            $"{zone.Unlocked}/{zone.Total}");

        ImGui.TableNextColumn();
        if (zone.CanFly)
            ImGui.TextColored(OdysseusTheme.StatusGreen, "flying");
        else
        {
            ImGui.TextColored(OdysseusTheme.TextSecondary,
                $"{fromQuests.Count} quest · {loose.Count} ground");
            if (loose.Count > reachable && ImGui.IsItemHovered())
                ImGui.SetTooltip($"{loose.Count - reachable} of the ground currents have no recorded position,\n" +
                                 "so they have to be found by hand.");
        }

        ImGui.TableNextColumn();
        if (zone.CanFly) return;

        if (fromQuests.Count > 0)
        {
            var known = fromQuests.Where(c => _quests.ById(c.QuestId) is not null).ToList();
            using (ImRaii.Disabled(known.Count == 0))
            {
                if (OdysseusTheme.IconTextButton(FontAwesomeIcon.ListUl, $"Queue {known.Count}##q{zone.TerritoryId}",
                        OdysseusTheme.GreenDark,
                        known.Count == 0
                            ? "None of these quests are in the catalog."
                            : "Put these quests, and anything they need first, on the priority list:\n"
                              + string.Join("\n", known.Select(c => "  " + (_quests.ById(c.QuestId)?.Name ?? $"#{c.QuestId}"))),
                        new Vector2(96, 22)))
                {
                    var queued = 0;
                    foreach (var current in known)
                        queued += _unlock.Queue(current.QuestId, zone.Name).Steps.Count;
                    _status = $"{zone.Name}: queued {queued} quest(s) for {known.Count} aether current(s).";
                }
            }
            ImGui.SameLine();
        }

        using (ImRaii.Disabled(reachable == 0 || !_collector.IsFinished))
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Feather, $"Collect {reachable}##c{zone.TerritoryId}",
                    zone.TerritoryId == here ? OdysseusTheme.AccentDim : OdysseusTheme.NeutralDark,
                    zone.TerritoryId == here
                        ? $"Walk to the {reachable} loose current(s) here and attune them."
                        : "Only works in the zone you are standing in.",
                    new Vector2(104, 22)))
            {
                if (!_collector.Start(zone))
                    _status = _collector.StatusLine;
            }
        }
    }
}
