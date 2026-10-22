using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace Odysseus.Windows;

/// <summary>
/// Quest target indicators: a marker on every object the game is showing a quest icon over, and a line
/// from the character to each — what a player had TextAdvance's indicators for.
///
/// <para>
/// "Has a quest icon" is the same test the run uses (<c>NamePlateIconId</c> in 71000–71999). Main
/// scenario and side quests are not told apart: the ranges inside that block have not been checked in
/// game, and a colour that guesses is worse than one colour.
/// </para>
///
/// <para>
/// A full-screen window that takes no input, so it never eats a click. The line is sampled in world
/// space and only the stretches in front of the camera are drawn — one screen-space line is nonsense
/// once one end is behind the camera (Charon's <c>EspOverlayWindow.DrawWorldLine</c>, the same author).
/// Hidden in cutscenes and loading screens.
/// </para>
/// </summary>
public sealed unsafe class QuestTargetOverlay : Window
{
    private const uint QuestMarkerFirst = 71000, QuestMarkerLast = 71999;
    private const int LineSegments = 32;

    private readonly IObjectTable _objects;
    private readonly IGameGui _gameGui;
    private readonly Func<bool> _shown;

    /// <summary>How many markers the last frame drew, for the settings line.</summary>
    public int Drawn { get; private set; }

    public QuestTargetOverlay(IObjectTable objects, IGameGui gameGui, Func<bool> shown)
        : base("##OdysseusQuestTargets")
    {
        _objects = objects;
        _gameGui = gameGui;
        _shown = shown;
        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
                | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoBringToFrontOnFocus;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void PreOpenCheck()
    {
        IsOpen = _shown();
        if (!IsOpen)
            Drawn = 0;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        Position = viewport.Pos;
        Size = viewport.Size;
        PositionCondition = ImGuiCond.Always;
        SizeCondition = ImGuiCond.Always;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw() => ImGui.PopStyleVar();

    public override void Draw()
    {
        var me = _objects.LocalPlayer;
        if (me is null)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(new Vector4(1f, 0.82f, 0.25f, 0.95f));   // the game's quest-icon gold
        var shadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.65f));
        var count = 0;

        foreach (var obj in _objects)
        {
            if (obj.Address == me.Address)
                continue;
            var icon = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)->NamePlateIconId;
            if (icon < QuestMarkerFirst || icon > QuestMarkerLast)
                continue;

            count++;
            DrawWorldLine(drawList, me.Position, obj.Position, colour);

            if (!_gameGui.WorldToScreen(obj.Position + new Vector3(0f, 1.2f, 0f), out var at))
                continue;
            drawList.AddCircleFilled(at, 9f, shadow);
            drawList.AddCircle(at, 9f, colour, 0, 2.5f);
            var bang = ImGui.CalcTextSize("!");
            drawList.AddText(at - bang / 2f, colour, "!");

            var label = $"{obj.Name.TextValue}  {Vector3.Distance(me.Position, obj.Position):0}y";
            var size = ImGui.CalcTextSize(label);
            var textAt = at + new Vector2(-size.X / 2f, 13f);
            drawList.AddText(textAt + Vector2.One, shadow, label);
            drawList.AddText(textAt, colour, label);
        }

        Drawn = count;
    }

    /// <summary>The line sampled in world space; only the stretches in front of the camera are stroked.</summary>
    private void DrawWorldLine(ImDrawListPtr drawList, Vector3 from, Vector3 to, uint colour)
    {
        var drawing = false;
        for (var i = 0; i <= LineSegments; i++)
        {
            var point = Vector3.Lerp(from, to, (float)i / LineSegments);
            if (_gameGui.WorldToScreen(point, out var screen, out _))
            {
                drawList.PathLineTo(screen);
                drawing = true;
            }
            else if (drawing)
            {
                drawList.PathStroke(colour, ImDrawFlags.None, 2f);
                drawing = false;
            }
        }
        if (drawing)
            drawList.PathStroke(colour, ImDrawFlags.None, 2f);
        drawList.PathClear();
    }
}
