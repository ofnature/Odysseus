using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Odysseus.Services.Ipc;
using Odysseus.Services.Run;

namespace Odysseus.Windows;

/// <summary>
/// The thread on the ground: while a run drives a step, its route is drawn in the world —
/// vnavmesh's live waypoints when a path is being followed, else a straight line to the
/// step's own mark. Background draw list, so it sits under every window and takes no input.
/// </summary>
public sealed class QuestLineOverlay
{
    private readonly QuestController _controller;
    private readonly VnavIpc _vnav;
    private readonly Func<bool> _enabled;

    public QuestLineOverlay(QuestController controller, VnavIpc vnav, Func<bool> enabled)
    {
        _controller = controller;
        _vnav = vnav;
        _enabled = enabled;
    }

    public void Draw()
    {
        if (!_enabled())
            return;
        if (_controller.State is not (RunState.Step or RunState.Travel))
            return;
        var player = Service.ObjectTable.LocalPlayer;
        if (player is null)
            return;

        var points = _vnav.ListWaypoints();
        Vector3? mark = _controller.CurrentStep is { } step
            && step.TerritoryId == Service.ClientState.TerritoryType ? step.Position : null;
        if (points.Count == 0 && mark is null)
            return;

        var draw = ImGui.GetBackgroundDrawList();
        var wine = OdysseusTheme.Current.AccentWine;
        var line = ImGui.ColorConvertFloat4ToU32(wine with { W = 0.85f });
        var glow = ImGui.ColorConvertFloat4ToU32(wine with { W = 0.30f });

        var previous = player.Position;
        var havePrevious = Service.GameGui.WorldToScreen(previous, out var previousScreen);
        void Segment(Vector3 world)
        {
            if (Service.GameGui.WorldToScreen(world, out var screen))
            {
                if (havePrevious)
                {
                    draw.AddLine(previousScreen, screen, glow, 6f);
                    draw.AddLine(previousScreen, screen, line, 2.5f);
                }
                havePrevious = true;
                previousScreen = screen;
            }
            else
                havePrevious = false;
            previous = world;
        }

        if (points.Count > 0)
            foreach (var w in points)
                Segment(w);
        else if (mark is { } m)
            Segment(m);

        // The destination ring: the last waypoint when following, else the step's mark.
        var goal = points.Count > 0 ? points[^1] : mark;
        if (goal is { } g && Service.GameGui.WorldToScreen(g, out var goalScreen))
        {
            draw.AddCircle(goalScreen, 10f, line, 24, 2.5f);
            var distance = Vector3.Distance(player.Position, g);
            draw.AddText(goalScreen + new Vector2(14, -8),
                line, $"{distance:F0}y");
        }
    }
}
