using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Odysseus.Windows;

/// <summary>
/// Odysseus palette + shared draw helpers.
///
/// <para>
/// Same dark Greek-pantheon chassis as Daedalus / Charon / Theseus — identical background layers,
/// text ramp and status colours, so the family reads as one suite — but Odysseus owns its own
/// accent so you can tell at a glance which plugin's window you are looking at.
/// </para>
///
/// <para>
/// <b>Accent: wine-dark.</b> Homer's sea. A muted violet that sits nowhere near Daedalus's gold,
/// Theseus's verdigris, the tank-role blue, or the red / amber / green status ramp — so error,
/// warning and good keep meaning exactly what they mean everywhere else.
/// </para>
///
/// <para>
/// <b>Foam is reserved.</b> <see cref="WakeFoam"/> is the ship's wake and is used for exactly one
/// thing: the resume system — the read-back quest state, the step it picked up at, the trail
/// behind. Never ordinary chrome, so wherever foam appears it means "this is what the game says
/// about where you were". Theseus reserves crimson for the same job; each plugin gets its own.
/// </para>
/// </summary>
internal static class OdysseusTheme
{
    // ── Background layers (shared with the family) ──
    public static readonly Vector4 BgDeep = new(0.08f, 0.08f, 0.10f, 1.00f);
    public static readonly Vector4 BgPanel = new(0.12f, 0.12f, 0.15f, 1.00f);
    public static readonly Vector4 BgRow = new(0.15f, 0.15f, 0.18f, 0.60f);

    // ── Accent — wine-dark (Odysseus identity) ──
    public static readonly Vector4 AccentWine = new(0.62f, 0.42f, 0.78f, 1.00f);
    public static readonly Vector4 AccentDim = new(0.38f, 0.26f, 0.48f, 1.00f);
    public static readonly Vector4 AccentWash = new(0.62f, 0.42f, 0.78f, 0.10f);

    // ── The Wake (resume system only — never ordinary chrome) ──
    public static readonly Vector4 WakeFoam = new(0.82f, 0.88f, 0.92f, 1.00f);
    public static readonly Vector4 WakeDim = new(0.50f, 0.55f, 0.58f, 1.00f);
    public static readonly Vector4 WakeWash = new(0.82f, 0.88f, 0.92f, 0.08f);

    // ── Status (shared with the family — semantics stay put) ──
    public static readonly Vector4 StatusGreen = new(0.20f, 0.75f, 0.35f, 1.00f);
    public static readonly Vector4 StatusYellow = new(0.85f, 0.75f, 0.10f, 1.00f);
    public static readonly Vector4 StatusRed = new(0.85f, 0.25f, 0.20f, 1.00f);
    public static readonly Vector4 StatusGrey = new(0.45f, 0.45f, 0.50f, 1.00f);

    // ── Text ramp (shared with the family) ──
    public static readonly Vector4 TextPrimary = new(0.92f, 0.90f, 0.85f, 1.00f);
    public static readonly Vector4 TextSecondary = new(0.60f, 0.58f, 0.55f, 1.00f);
    public static readonly Vector4 TextDisabled = new(0.35f, 0.35f, 0.38f, 1.00f);

    // ── Fleet rows ──
    public static readonly Vector4 PeerOnline = new(0.20f, 0.75f, 0.35f, 1.00f);
    public static readonly Vector4 PeerStale = new(0.85f, 0.75f, 0.10f, 1.00f);
    public static readonly Vector4 PeerGone = new(0.45f, 0.45f, 0.50f, 1.00f);

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
            ImGui.GetWindowDrawList().AddLine(lineStart, lineEnd,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.20f, 0.24f, 1f)), 1f);
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

    /// <summary>
    /// Full-width call-to-action button in the accent. The one thing on the panel you press to make
    /// something happen gets the identity colour; everything else stays quiet around it.
    /// </summary>
    public static bool AccentButton(string label, float height = 30f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, AccentDim);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentWine);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentWine);
        ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
        try
        {
            return ImGui.Button(label, new Vector2(-1f, height));
        }
        finally
        {
            ImGui.PopStyleColor(4);
        }
    }

    // ── Control buttons (SealBreaker convention: solid green go, solid red stop, yellow armed) ──
    public static readonly Vector4 GreenDark = new(0.16f, 0.42f, 0.22f, 1.00f);
    public static readonly Vector4 RedDark = new(0.50f, 0.16f, 0.14f, 1.00f);
    public static readonly Vector4 YellowDark = new(0.42f, 0.34f, 0.12f, 1.00f);
    public static readonly Vector4 NeutralDark = new(0.22f, 0.22f, 0.26f, 1.00f);

    /// <summary>Solid-fill button in a base colour, brighter on hover, darker when pressed.</summary>
    public static bool SolidButton(string label, Vector4 baseColor, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Scale(baseColor, 1.25f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Scale(baseColor, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
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
    public static bool ArmedButton(string label, bool armed, Vector2 size) => SolidButton(label, armed ? YellowDark : NeutralDark, size);

    private static Vector4 Scale(Vector4 c, float f)
        => new(System.Math.Clamp(c.X * f, 0f, 1f), System.Math.Clamp(c.Y * f, 0f, 1f), System.Math.Clamp(c.Z * f, 0f, 1f), c.W);

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
