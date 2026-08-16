using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Odysseus.Windows;

/// <summary>Which of the two palettes is live.</summary>
public enum ThemeMode
{
    /// <summary>Light blue body, ink text — the QST-style compact panel look (mockup A).</summary>
    Day,
    /// <summary>Deep slate-blue body, light text (mockup B).</summary>
    Dusk,
}

/// <summary>
/// Odysseus palette + shared draw helpers.
///
/// <para>
/// Two palettes, one vocabulary. Every window reads colours through the static properties below,
/// which forward to the live <see cref="Palette"/>; <see cref="Mode"/> swaps it. Windows derive
/// from <see cref="OdysseusWindow"/>, which pushes the palette into ImGui's own window/frame/
/// button colours around each draw — that is what makes a light window actually light instead of
/// light text on Dalamud's dark chrome.
/// </para>
///
/// <para>
/// <b>Accent: wine-dark.</b> Homer's sea; sits nowhere near Daedalus's gold, Theseus's verdigris,
/// the tank-role blue, or the red / amber / green status ramp.
/// <b>Foam/teal is reserved for the Wake</b> — the read-back quest state and the resume line — never
/// ordinary chrome. Theseus reserves crimson for the same job.
/// </para>
/// </summary>
internal static class OdysseusTheme
{
    /// <summary>One complete palette. Immutable; swap the whole thing.</summary>
    public sealed record Palette(
        Vector4 BgDeep, Vector4 BgPanel, Vector4 BgRow, Vector4 TitleBg, Vector4 Border,
        Vector4 AccentWine, Vector4 AccentDim, Vector4 AccentWash,
        Vector4 WakeFoam, Vector4 WakeDim, Vector4 WakeWash,
        Vector4 StatusGreen, Vector4 StatusYellow, Vector4 StatusRed, Vector4 StatusGrey,
        Vector4 TextPrimary, Vector4 TextSecondary, Vector4 TextDisabled,
        Vector4 GreenDark, Vector4 RedDark, Vector4 YellowDark, Vector4 NeutralDark, Vector4 ButtonText,
        Vector4 ChipBg, Vector4 ChipFg, Vector4 StateChipBg, Vector4 StateChipFg, Vector4 JobChipBg, Vector4 JobChipFg,
        Vector4 BarBg, Vector4 OnAccent);

    /// <summary>Mockup A: light blue day.</summary>
    public static readonly Palette Day = new(
        BgDeep: C(0xE9F1F8), BgPanel: C(0xF5F9FC), BgRow: C(0xDCE8F2), TitleBg: C(0xD6E4F0), Border: C(0xB9CCDD),
        AccentWine: C(0x7A4EA6), AccentDim: C(0x5E3B82), AccentWash: C(0x7A4EA6, 0.12f),
        WakeFoam: C(0x1F6E8C), WakeDim: C(0x3F8FAF), WakeWash: C(0x1F6E8C, 0.10f),
        StatusGreen: C(0x2E8B57), StatusYellow: C(0xB8860B), StatusRed: C(0xC0392B), StatusGrey: C(0x8299AC),
        TextPrimary: C(0x1D2A36), TextSecondary: C(0x4E6478), TextDisabled: C(0x8299AC),
        GreenDark: C(0x2E8B57), RedDark: C(0xC0392B), YellowDark: C(0xE3B94D), NeutralDark: C(0xF5F9FC), ButtonText: C(0xFFFFFF),
        ChipBg: C(0xCFE0EE), ChipFg: C(0x2B4157), StateChipBg: C(0xCDEBD8), StateChipFg: C(0x1E5A34), JobChipBg: C(0xF0DCE0), JobChipFg: C(0x7A2E3D),
        BarBg: C(0xC9D9E6), OnAccent: C(0xFFFFFF));

    /// <summary>Mockup B: dusk slate-blue.</summary>
    public static readonly Palette Dusk = new(
        BgDeep: C(0x1B2633), BgPanel: C(0x243444), BgRow: C(0x26364A), TitleBg: C(0x15202B), Border: C(0x2E4052),
        AccentWine: C(0x9E6BC7), AccentDim: C(0x5D3F78), AccentWash: C(0x9E6BC7, 0.12f),
        WakeFoam: C(0xBFE3F2), WakeDim: C(0x7FB3C8), WakeWash: C(0xBFE3F2, 0.08f),
        StatusGreen: C(0x3FCB73), StatusYellow: C(0xE0B84A), StatusRed: C(0xE0604F), StatusGrey: C(0x6A8098),
        TextPrimary: C(0xE6EEF6), TextSecondary: C(0x9DB2C6), TextDisabled: C(0x6A8098),
        GreenDark: C(0x2E8B57), RedDark: C(0xB03A2E), YellowDark: C(0xC99A2E), NeutralDark: C(0x2E4052), ButtonText: C(0xE6EEF6),
        ChipBg: C(0x26364A), ChipFg: C(0xBFD3E6), StateChipBg: C(0x1F4A34), StateChipFg: C(0x8FE0B0), JobChipBg: C(0x4A2A34), JobChipFg: C(0xF0B7C4),
        BarBg: C(0x26364A), OnAccent: C(0xFFFFFF));

    private static Palette _p = Day;

    public static ThemeMode Mode { get; private set; } = ThemeMode.Day;

    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        _p = mode == ThemeMode.Dusk ? Dusk : Day;
    }

    public static Palette Current => _p;

    // ── forwarding properties: the vocabulary every window uses ──
    public static Vector4 BgDeep => _p.BgDeep;
    public static Vector4 BgPanel => _p.BgPanel;
    public static Vector4 BgRow => _p.BgRow;
    public static Vector4 Border => _p.Border;
    public static Vector4 AccentWine => _p.AccentWine;
    public static Vector4 AccentDim => _p.AccentDim;
    public static Vector4 AccentWash => _p.AccentWash;
    public static Vector4 WakeFoam => _p.WakeFoam;
    public static Vector4 WakeDim => _p.WakeDim;
    public static Vector4 WakeWash => _p.WakeWash;
    public static Vector4 StatusGreen => _p.StatusGreen;
    public static Vector4 StatusYellow => _p.StatusYellow;
    public static Vector4 StatusRed => _p.StatusRed;
    public static Vector4 StatusGrey => _p.StatusGrey;
    public static Vector4 TextPrimary => _p.TextPrimary;
    public static Vector4 TextSecondary => _p.TextSecondary;
    public static Vector4 TextDisabled => _p.TextDisabled;
    public static Vector4 PeerOnline => _p.StatusGreen;
    public static Vector4 PeerStale => _p.StatusYellow;
    public static Vector4 PeerGone => _p.StatusGrey;
    public static Vector4 GreenDark => _p.GreenDark;
    public static Vector4 RedDark => _p.RedDark;
    public static Vector4 YellowDark => _p.YellowDark;
    public static Vector4 NeutralDark => _p.NeutralDark;

    private static Vector4 C(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    private static Vector4 Scale(Vector4 c, float f)
        => new(System.Math.Clamp(c.X * f, 0f, 1f), System.Math.Clamp(c.Y * f, 0f, 1f), System.Math.Clamp(c.Z * f, 0f, 1f), c.W);

    // ── window chrome ──

    /// <summary>Push the palette into ImGui's window/frame/button colours. Returns the count for <see cref="ImGui.PopStyleColor(int)"/>.</summary>
    public static int PushWindowColors()
    {
        var p = _p;
        var n = 0;
        void Push(ImGuiCol col, Vector4 v) { ImGui.PushStyleColor(col, v); n++; }
        Push(ImGuiCol.WindowBg, p.BgDeep);
        Push(ImGuiCol.ChildBg, p.BgPanel);
        Push(ImGuiCol.PopupBg, p.BgPanel);
        Push(ImGuiCol.Border, p.Border);
        Push(ImGuiCol.TitleBg, p.TitleBg);
        Push(ImGuiCol.TitleBgActive, p.TitleBg);
        Push(ImGuiCol.TitleBgCollapsed, p.TitleBg);
        Push(ImGuiCol.MenuBarBg, p.TitleBg);
        Push(ImGuiCol.Text, p.TextPrimary);
        Push(ImGuiCol.TextDisabled, p.TextDisabled);
        Push(ImGuiCol.FrameBg, p.BgRow);
        Push(ImGuiCol.FrameBgHovered, Scale(p.BgRow, Mode == ThemeMode.Day ? 0.95f : 1.15f));
        Push(ImGuiCol.FrameBgActive, Scale(p.BgRow, Mode == ThemeMode.Day ? 0.90f : 1.25f));
        Push(ImGuiCol.Button, p.NeutralDark);
        Push(ImGuiCol.ButtonHovered, Scale(p.NeutralDark, Mode == ThemeMode.Day ? 0.94f : 1.2f));
        Push(ImGuiCol.ButtonActive, Scale(p.NeutralDark, Mode == ThemeMode.Day ? 0.88f : 1.35f));
        Push(ImGuiCol.Header, p.AccentWash);
        Push(ImGuiCol.HeaderHovered, p.AccentWash);
        Push(ImGuiCol.HeaderActive, p.AccentWash);
        Push(ImGuiCol.Separator, p.Border);
        Push(ImGuiCol.CheckMark, p.AccentWine);
        Push(ImGuiCol.SliderGrab, p.AccentWine);
        Push(ImGuiCol.SliderGrabActive, p.AccentDim);
        Push(ImGuiCol.ScrollbarBg, p.BgDeep);
        Push(ImGuiCol.ScrollbarGrab, p.Border);
        Push(ImGuiCol.TableHeaderBg, p.BgRow);
        Push(ImGuiCol.TableRowBg, p.BgDeep);
        Push(ImGuiCol.TableRowBgAlt, p.BgPanel);
        Push(ImGuiCol.TableBorderLight, p.Border);
        Push(ImGuiCol.TableBorderStrong, p.Border);
        Push(ImGuiCol.ResizeGrip, p.Border);
        Push(ImGuiCol.Tab, p.BgRow);
        Push(ImGuiCol.TabHovered, p.AccentWash);
        Push(ImGuiCol.TabActive, p.AccentWash);
        return n;
    }

    // ── shared widgets ──

    /// <summary>
    /// Accent-coloured section header. This Dalamud ImGui binding has no SeparatorText, so it is
    /// hand-drawn: coloured label with a hairline continuing to the right edge (family pattern).
    /// </summary>
    public static void SectionHeader(string label) => SectionHeader(label, AccentWine);

    /// <summary>Section header in an explicit colour — pass <see cref="WakeFoam"/> for Wake panels.</summary>
    public static void SectionHeader(string label, Vector4 color)
    {
        ImGui.Spacing();
        ImGui.TextColored(color, label);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineY = (min.Y + max.Y) / 2f;
        var lineStart = new Vector2(max.X + 8f, lineY);
        var lineEnd = new Vector2(
            ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X, lineY);
        if (lineEnd.X > lineStart.X)
            ImGui.GetWindowDrawList().AddLine(lineStart, lineEnd, ImGui.ColorConvertFloat4ToU32(Border), 1f);
        ImGui.Spacing();
    }

    /// <summary>Coloured status dot + label.</summary>
    public static void StatusDot(bool active, string activeLabel = "Active", string inactiveLabel = "Idle")
    {
        ImGui.TextColored(active ? StatusGreen : StatusGrey, "●");
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(active ? StatusGreen : TextSecondary, active ? activeLabel : inactiveLabel);
    }

    /// <summary>Dependency chip — green when the plugin is present, red (hard) or grey (soft) when missing.</summary>
    public static void DependencyChip(string label, bool available, bool required = true)
    {
        var missingColor = required ? StatusRed : StatusGrey;
        ImGui.TextColored(available ? StatusGreen : missingColor, available ? "●" : "○");
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(available ? TextPrimary : missingColor, label);
    }

    /// <summary>A small rounded pill of text — quest id, job, state.</summary>
    public static void Chip(string text, Vector4 bg, Vector4 fg)
    {
        var pad = new Vector2(7f, 1f);
        var size = ImGui.CalcTextSize(text) + pad * 2f;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(bg), size.Y / 2f);
        ImGui.SetCursorScreenPos(pos + pad);
        ImGui.TextColored(fg, text);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
        ImGui.Dummy(new Vector2(0f, size.Y));
        ImGui.SameLine(0f, 0f);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
    }

    public static void IdChip(string text) => Chip(text, _p.ChipBg, _p.ChipFg);
    public static void StateChip(string text) => Chip(text, _p.StateChipBg, _p.StateChipFg);
    public static void JobChip(string text) => Chip(text, _p.JobChipBg, _p.JobChipFg);

    /// <summary>Filled badge for the step kind.</summary>
    public static void KindBadge(string text)
    {
        var pad = new Vector2(6f, 1f);
        var size = ImGui.CalcTextSize(text) + pad * 2f;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(AccentWine), 3f);
        ImGui.SetCursorScreenPos(pos + pad);
        ImGui.TextColored(_p.OnAccent, text);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
        ImGui.Dummy(new Vector2(0f, size.Y));
        ImGui.SameLine(0f, 0f);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
    }

    /// <summary>Thin progress bar in the accent.</summary>
    public static void ProgressBar(float fraction, float height = 4f)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(_p.BarBg), height / 2f);
        var f = System.Math.Clamp(fraction, 0f, 1f);
        if (f > 0f)
            draw.AddRectFilled(pos, pos + new Vector2(width * f, height), ImGui.ColorConvertFloat4ToU32(AccentWine), height / 2f);
        ImGui.Dummy(new Vector2(width, height + 4f));
    }

    /// <summary>Solid-fill button in a base colour, brighter on hover, darker when pressed.</summary>
    public static bool SolidButton(string label, Vector4 baseColor, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Scale(baseColor, 1.15f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Scale(baseColor, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Text, _p.ButtonText);
        try
        {
            return ImGui.Button(label, size);
        }
        finally
        {
            ImGui.PopStyleColor(4);
        }
    }

    public static bool StartButton(string label, Vector2 size) => SolidButton(label, GreenDark, size);

    public static bool StopButton(string label, Vector2 size) => SolidButton(label, RedDark, size);

    /// <summary>A toggle that is yellow while armed and neutral otherwise ("stop after this quest").</summary>
    public static bool ArmedButton(string label, bool armed, Vector2 size)
    {
        if (!armed)
            return ImGui.Button(label, size);
        ImGui.PushStyleColor(ImGuiCol.Button, YellowDark);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Scale(YellowDark, 1.1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Scale(YellowDark, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.Text, C(0x2B2200));
        try
        {
            return ImGui.Button(label, size);
        }
        finally
        {
            ImGui.PopStyleColor(4);
        }
    }

    /// <summary>
    /// Full-width call-to-action button in the accent.
    /// </summary>
    public static bool AccentButton(string label, float height = 30f)
        => SolidButton(label, AccentDim, new Vector2(-1f, height));

    /// <summary>Hover "(?)" tooltip for a non-obvious control.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}

/// <summary>Minimal scoped-disable helper (Dalamud's ImRaii is not exposed by this binding).</summary>
internal static class ImRaii
{
    public static DisabledScope Disabled(bool disabled) => new(disabled);

    internal readonly struct DisabledScope : System.IDisposable
    {
        private readonly bool _active;

        public DisabledScope(bool active)
        {
            _active = active;
            if (_active)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (_active)
                ImGui.EndDisabled();
        }
    }
}

/// <summary>Base for every Odysseus window: the palette goes into ImGui's own colours around each draw.</summary>
public abstract class OdysseusWindow : Dalamud.Interface.Windowing.Window
{
    private int _pushed;

    protected OdysseusWindow(string name) : base(name) { }

    public override void PreDraw() => _pushed = OdysseusTheme.PushWindowColors();

    public override void PostDraw()
    {
        if (_pushed > 0)
            ImGui.PopStyleColor(_pushed);
        _pushed = 0;
    }
}
