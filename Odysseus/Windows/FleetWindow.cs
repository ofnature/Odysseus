using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Odysseus.Config;
using Odysseus.Services.Fleet;

namespace Odysseus.Windows;

/// <summary>
/// One row per box: who is where in the story, what state they are in, when they last spoke.
/// Read-only — there is nothing to click because there is nothing this window can make another
/// box do.
/// </summary>
public sealed class FleetWindow : Window
{
    private readonly OdysseusConfig _config;
    private readonly FleetPublisher _fleet;

    public FleetWindow(OdysseusConfig config, FleetPublisher fleet)
        : base("Odysseus Fleet##OdysseusFleet")
    {
        _config = config;
        _fleet = fleet;
        Size = new Vector2(560, 300);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 160), MaximumSize = new Vector2(1200, 900) };
    }

    public override void Draw()
    {
        var now = DateTime.UtcNow;
        var peers = _fleet.Roster.Peers(now, TimeSpan.FromSeconds(Math.Max(1f, _config.PeerStaleSeconds)));

        if (!ImGui.BeginTable("##fleet", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("##dot", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Quest", ImGuiTableColumnFlags.WidthStretch, 1.8f);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableHeadersRow();

        if (_fleet.Own is { } own)
            DrawRow(own, PeerLiveness.Online, TimeSpan.Zero, isSelf: true);
        foreach (var peer in peers)
            DrawRow(peer.Status, peer.Liveness, peer.Age, isSelf: false);

        ImGui.EndTable();

        ImGui.Spacing();
        ImGui.TextColored(OdysseusTheme.TextDisabled,
            _config.PublishFleetStatus ? "Read-only · via Daedalus relay" : "Publishing off — this box is not visible to the others");
    }

    private static void DrawRow(FleetStatus s, PeerLiveness liveness, TimeSpan age, bool isSelf)
    {
        var dot = liveness switch
        {
            PeerLiveness.Online => OdysseusTheme.PeerOnline,
            PeerLiveness.Stale => OdysseusTheme.PeerStale,
            _ => OdysseusTheme.PeerGone,
        };
        var text = liveness == PeerLiveness.Gone ? OdysseusTheme.TextDisabled : OdysseusTheme.TextPrimary;
        var muted = liveness == PeerLiveness.Gone ? OdysseusTheme.TextDisabled : OdysseusTheme.TextSecondary;

        ImGui.TableNextRow();
        ImGui.TableNextColumn(); ImGui.TextColored(dot, "●");
        ImGui.TableNextColumn(); ImGui.TextColored(text, s.Character + (isSelf ? " (you)" : ""));
        ImGui.TableNextColumn();
        if (s.QuestId == 0)
            ImGui.TextColored(muted, "—");
        else
        {
            ImGui.TextColored(text, s.QuestName);
            ImGui.SameLine();
            ImGui.TextColored(muted, $"· {s.Sequence}");
        }
        ImGui.TableNextColumn();
        var stateColor = s.State == "Faulted" ? OdysseusTheme.StatusRed : s.State == "Idle" ? muted : text;
        ImGui.TextColored(stateColor, s.State);
        if (s.State == "Faulted" && s.StatusLine.Length > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(s.StatusLine);
        ImGui.TableNextColumn();
        ImGui.TextColored(liveness == PeerLiveness.Online ? muted : dot, isSelf ? "now" : Fmt(age));
    }

    private static string Fmt(TimeSpan age)
        => age.TotalSeconds < 60 ? $"{age.TotalSeconds:F0}s" : age.TotalMinutes < 60 ? $"{age.TotalMinutes:F0}m" : $"{age.TotalHours:F0}h";
}
