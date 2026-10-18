using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Odysseus.Windows.Dock;

/// <summary>
/// The edge tab the main window minimizes to: a tab pinned to a screen edge with a sideways label,
/// popping the main window's body out beside it while pointed at (click to pin, right-click to
/// restore the window). Ported from Daedalus's <c>EdgeDockWindow</c> (GPLv3, the same author), in
/// Odysseus's colours, and stacked with Daedalus's and Charon's docks through the shared layout.
/// <para>
/// A tab's panel is the BODY of the window it stands for — that window's own <c>Draw()</c>, called inside
/// the pop-out — so the tab and the window cannot drift apart.
/// </para>
/// <para>
/// The sideways label is ordinary text whose vertices are turned after it is drawn (<see cref="DockMath"/>).
/// It is drawn under a full-screen clip rect: ImGui culls glyphs against the clip rect BEFORE they are
/// turned, and the unturned text runs far outside a 34-pixel tab.
/// </para>
/// </summary>
public sealed unsafe class EdgeDockWindow : Window
{
    /// <param name="Id">Stable id; also the ImGui id suffix.</param>
    /// <param name="Label">What the sideways tab reads.</param>
    /// <param name="Applies">Whether the tab is shown right now.</param>
    /// <param name="DrawPanel">The panel body — normally the target window's own <c>Draw()</c>.</param>
    /// <param name="PanelWidth">A fixed width with the height left to the content; null sizes both ways.</param>
    /// <param name="Restore">Right-click on the tab: back to the ordinary window.</param>
    public sealed record Tab(string Id, string Label, Func<bool> Applies, Action DrawPanel, float? PanelWidth = null, Action? Restore = null);

    private const float TabWidth = 34f;
    private const float TabPadding = 14f;
    private const float TabGap = 4f;
    private const float PanelGap = 6f;
    private const float LabelScale = 1.25f;
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(450);

    private readonly IReadOnlyList<Tab> _tabs;
    private readonly Func<bool> _rightSide;
    private readonly Func<bool> _shown;
    private readonly DockLayoutShare? _layout;
    private readonly DockHover _hover = new();
    private List<Tab> _visible = new();
    private float _panelHeight = 240f;

    /// <param name="shown">Whether the dock is up at all — the main window minimized to it, and logged in.</param>
    /// <param name="layout">The layout shared with other plugins' docks; null centres this one on its own.</param>
    public EdgeDockWindow(IReadOnlyList<Tab> tabs, Func<bool> rightSide, Func<bool> shown, DockLayoutShare? layout = null)
        : base("##OdysseusEdgeDock")
    {
        _tabs = tabs;
        _rightSide = rightSide;
        _shown = shown;
        _layout = layout;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar;
        RespectCloseHotkey = false;
        PositionCondition = ImGuiCond.Always;
        IsOpen = false;
    }

    /// <summary>Asked every frame, open or not: up while minimized to it and some tab applies.</summary>
    public override void PreOpenCheck()
    {
        _visible = _shown() ? _tabs.Where(t => t.Applies()).ToList() : [];
        IsOpen = _visible.Count > 0;
    }

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        var viewport = ImGui.GetMainViewport();
        var total = _visible.Sum(t => TabHeight(t.Label)) + TabGap * Math.Max(0, _visible.Count - 1);
        var right = _rightSide();
        var x = right ? viewport.WorkPos.X + viewport.WorkSize.X - TabWidth : viewport.WorkPos.X;
        var y = _layout?.Place(right, total, viewport.WorkPos.Y, viewport.WorkSize.Y)
                ?? viewport.WorkPos.Y + (viewport.WorkSize.Y - total) / 2f;
        Position = new Vector2(x, y);
    }

    public override void PostDraw() => ImGui.PopStyleVar();

    public override void Draw()
    {
        var drawList = ImGui.GetWindowDrawList();
        var right = _rightSide();
        string? hovered = null;
        Tab? openTab = null;
        var openMin = Vector2.Zero;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, TabGap));
        foreach (var tab in _visible)
        {
            var size = new Vector2(TabWidth, TabHeight(tab.Label));
            var min = ImGui.GetCursorScreenPos();

            if (ImGui.InvisibleButton($"##dock{tab.Id}", size))
                _hover.Click(tab.Id);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && tab.Restore is { } restore)
                restore();
            // No tooltip: it sat over the top of the panel that pops out at the same moment.
            if (ImGui.IsItemHovered())
                hovered = tab.Id;

            var isOpen = _hover.Open == tab.Id;
            var pinned = _hover.Pinned == tab.Id;
            DrawTab(drawList, min, size, tab.Label, isOpen || hovered == tab.Id, pinned, right);

            if (isOpen)
            {
                openTab = tab;
                openMin = min;
            }
        }

        ImGui.PopStyleVar();

        var panelBusy = openTab != null && DrawPanel(openTab, openMin, right);
        _hover.Update(hovered, panelBusy, DateTime.UtcNow, CloseDelay, _visible.Select(t => t.Id).ToList());
    }

    private static float TabHeight(string label) =>
        MathF.Max(90f, ImGui.CalcTextSize(label).X * LabelScale + TabPadding * 2f);

    private static void DrawTab(ImDrawListPtr drawList, Vector2 min, Vector2 size, string label, bool lit, bool pinned, bool right)
    {
        var max = min + size;
        var fill = lit ? OdysseusTheme.BgRow : OdysseusTheme.Current.TitleBg;
        fill.W = lit ? 0.97f : 0.90f;
        // Rounded on the side facing the screen, square against the edge it is pinned to.
        var corners = right ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersRight;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 6f, corners);
        drawList.AddRect(min, max, ImGui.GetColorU32(OdysseusTheme.Border), 6f, corners);

        if (pinned)
        {
            // Wine on the tab's outer edge marks a pinned panel.
            var barMin = right ? new Vector2(min.X, min.Y) : new Vector2(max.X - 2f, min.Y);
            drawList.AddRectFilled(barMin, barMin + new Vector2(2f, size.Y), ImGui.GetColorU32(OdysseusTheme.AccentWine));
        }

        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * LabelScale;
        var textSize = ImGui.CalcTextSize(label) * LabelScale;

        // Turned, the label is textSize.Y wide and textSize.X tall — centred in the tab.
        var topLeft = min + new Vector2((size.X - textSize.Y) / 2f, (size.Y - textSize.X) / 2f);
        var pivot = DockMath.PivotFor(topLeft, textSize);
        var color = ImGui.GetColorU32(lit ? OdysseusTheme.AccentWine : OdysseusTheme.TextSecondary);

        // Drawn first near the top-left of the screen, where the whole unturned line fits, then turned and
        // moved into the tab.
        var drawnAt = ImGui.GetMainViewport().WorkPos + new Vector2(8f, 8f);
        drawList.PushClipRectFullScreen();
        var first = drawList.VtxBuffer.Size;
        drawList.AddText(font, fontSize, drawnAt, color, label);
        var last = drawList.VtxBuffer.Size;
        var vertices = drawList.VtxBuffer.Data;
        for (var i = first; i < last; i++)
            vertices[i].Pos = DockMath.TurnUpAndPlace(vertices[i].Pos, drawnAt, pivot);
        drawList.PopClipRect();
    }

    /// <summary>Draws the open tab's panel beside it. True while the pointer is on it or it is mid-interaction.</summary>
    private bool DrawPanel(Tab tab, Vector2 tabMin, bool right)
    {
        var viewport = ImGui.GetMainViewport();

        var x = right ? tabMin.X - PanelGap : tabMin.X + TabWidth + PanelGap;
        // Level with the tab, pulled up if last frame's height would run it off the bottom of the screen.
        var bottom = viewport.WorkPos.Y + viewport.WorkSize.Y - _panelHeight - 4f;
        var y = MathF.Max(viewport.WorkPos.Y + 4f, MathF.Min(tabMin.Y, bottom));

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always, new Vector2(right ? 1f : 0f, 0f));
        if (tab.PanelWidth is { } width)
            ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, float.MaxValue));

        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize;

        var busy = false;
        // The panel is the main window's body: it wears the same palette the window would.
        var pushed = OdysseusTheme.PushWindowColors();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
        if (ImGui.Begin($"##OdysseusEdgeDockPanel{tab.Id}", flags))
        {
            try
            {
                tab.DrawPanel();
            }
            catch (Exception ex)
            {
                // Caught HERE: an exception leaving between Begin and End would skip the End and corrupt
                // ImGui's window stack for every plugin drawing after this one.
                ImGui.TextColored(OdysseusTheme.StatusRed, $"This panel could not be drawn: {ex.Message}");
            }

            _panelHeight = ImGui.GetWindowSize().Y;

            // The panel also counts as "in use" while one of its own pop-ups is open or a control is held:
            // a combo's list is a separate window, and losing the panel under it closes the combo.
            busy = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                         | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                         | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                   || ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
                   || ImGui.IsAnyItemActive();
        }

        ImGui.End();
        ImGui.PopStyleVar();
        if (pushed > 0)
            ImGui.PopStyleColor(pushed);
        return busy;
    }
}
