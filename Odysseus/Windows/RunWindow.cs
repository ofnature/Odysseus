using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Odysseus.Config;
using Odysseus.Services.Ipc;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Windows;

/// <summary>
/// The main window: what the character is doing right now, what the game says about the current
/// quest, and the one button that starts or stops. Framework cut — the run controller does not
/// exist yet, so state is always Idle and start is disabled; the quest panel already reads live.
/// </summary>
public sealed class RunWindow : Window
{
    private readonly OdysseusConfig _config;
    private readonly PluginPresence _presence;
    private readonly IQuestStateReader _quests;
    private readonly Func<RunState> _state;
    private readonly Action _openConfig;

    public RunWindow(
        OdysseusConfig config,
        PluginPresence presence,
        IQuestStateReader quests,
        Func<RunState> state,
        Action openConfig)
        : base("Odysseus##OdysseusRun")
    {
        _config = config;
        _presence = presence;
        _quests = quests;
        _state = state;
        _openConfig = openConfig;

        Size = new Vector2(420, 380);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 300),
            MaximumSize = new Vector2(800, 900),
        };
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        try
        {
            DrawStatusLine();
            ImGui.Separator();
            DrawQuestPanel();
            ImGui.Spacing();
            DrawControls();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private void DrawStatusLine()
    {
        var state = _state();
        var missing = _presence.MissingSummary();

        if (!_config.Enabled)
            OdysseusTheme.StatusDot(false, inactiveLabel: "Disabled");
        else if (missing.Length > 0)
            ImGui.TextColored(OdysseusTheme.StatusRed, "⚠ " + missing);
        else
            OdysseusTheme.StatusDot(state.IsDriving(), state.ToString(), "Idle");

        ImGui.SameLine(ImGui.GetWindowWidth() - 70f);
        if (ImGui.SmallButton("Settings"))
            _openConfig();
    }

    private void DrawQuestPanel()
    {
        OdysseusTheme.SectionHeader("QUEST");

        var accepted = _quests.ReadAccepted();
        if (accepted.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "No quest state readable.");
            ImGui.TextWrapped("The quest reader is not wired up yet (P0). Once it is, the accepted quests, " +
                              "their sequence and their progress variables show here, live from the game.");
            return;
        }

        foreach (var q in accepted)
        {
            // Foam is the Wake's colour: this is the game's own record of where you are.
            ImGui.TextColored(OdysseusTheme.WakeFoam, "○");
            ImGui.SameLine(0f, 6f);
            ImGui.TextColored(OdysseusTheme.TextPrimary, $"Quest {q.QuestId}");
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextSecondary,
                q.IsReadyToComplete ? "ready to hand in" : $"sequence {q.Sequence}");
        }
    }

    private void DrawControls()
    {
        var canRun = _config.Enabled && _presence.CoreReady;
        var driving = _state().IsDriving();

        // The controller lands in P1; until then the button exists so the layout is real, and
        // stays disabled so nothing pretends to work.
        using (ImRaii.Disabled(true))
        {
            OdysseusTheme.AccentButton(driving ? "Stop" : "Start MSQ");
        }

        if (!canRun)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled,
                !_config.Enabled ? "Enable Odysseus in settings to run." : _presence.MissingSummary());
        }
        else
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Run engine not built yet — framework only.");
        }
    }
}
