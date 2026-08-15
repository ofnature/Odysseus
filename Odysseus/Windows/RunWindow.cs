using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Odysseus.Config;
using Odysseus.Services.Ipc;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Windows;

/// <summary>
/// The main window: what the character is doing right now, what the game says about the current
/// quest, and the one button that starts or stops.
/// </summary>
public sealed class RunWindow : Window
{
    private readonly OdysseusConfig _config;
    private readonly PluginPresence _presence;
    private readonly IQuestStateReader _quests;
    private readonly QuestCatalog _catalog;
    private readonly PathStore _paths;
    private readonly QuestController _controller;
    private readonly Action _openConfig;
    private readonly Action _openFleet;
    private readonly Action<ushort> _openEditor;

    private ushort _selectedQuest;

    public RunWindow(
        OdysseusConfig config,
        PluginPresence presence,
        IQuestStateReader quests,
        QuestCatalog catalog,
        PathStore paths,
        QuestController controller,
        Action openConfig,
        Action openFleet,
        Action<ushort> openEditor)
        : base("Odysseus##OdysseusRun")
    {
        _config = config;
        _presence = presence;
        _quests = quests;
        _catalog = catalog;
        _paths = paths;
        _controller = controller;
        _openConfig = openConfig;
        _openFleet = openFleet;
        _openEditor = openEditor;

        Size = new Vector2(440, 400);
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
        var state = _controller.State;
        var missing = _presence.MissingSummary();

        if (!_config.Enabled)
            OdysseusTheme.StatusDot(false, inactiveLabel: "Disabled");
        else if (missing.Length > 0)
            ImGui.TextColored(OdysseusTheme.StatusRed, "⚠ " + missing);
        else if (state == RunState.Faulted)
            ImGui.TextColored(OdysseusTheme.StatusRed, "● Faulted");
        else
            OdysseusTheme.StatusDot(state.IsDriving(), state.ToString(), "Idle");

        ImGui.SameLine(ImGui.GetWindowWidth() - 120f);
        if (ImGui.SmallButton("Fleet"))
            _openFleet();
        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            _openConfig();
    }

    private void DrawQuestPanel()
    {
        OdysseusTheme.SectionHeader("QUEST");

        var accepted = _quests.ReadAccepted();
        if (accepted.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "No accepted quests.");
            _selectedQuest = 0;
            return;
        }

        // MSQ first, then everything else, so the line Odysseus cares about is always at the top.
        var anySelectable = false;
        foreach (var msq in new[] { true, false })
        {
            foreach (var q in accepted)
            {
                var listing = _catalog.ById(q.QuestId);
                var isMsq = listing?.IsMainScenario == true;
                if (isMsq != msq)
                    continue;
                var hasPath = _paths.Has(q.QuestId);
                if (hasPath && _selectedQuest == 0 && isMsq)
                    _selectedQuest = q.QuestId;
                anySelectable |= hasPath;
                DrawQuestRow(q, listing?.Name ?? $"Quest {q.QuestId}", isMsq, hasPath);
            }
        }
        if (!anySelectable)
            ImGui.TextColored(OdysseusTheme.TextDisabled, "No stored path for any accepted quest — import in Settings › Paths.");

        if (_controller.State != RunState.Idle)
        {
            ImGui.Spacing();
            ImGui.TextColored(OdysseusTheme.TextSecondary, _controller.StatusLine);
        }

        if (_controller.WakeNote.Length > 0)
        {
            OdysseusTheme.SectionHeader("THE WAKE", OdysseusTheme.WakeFoam);
            ImGui.TextColored(OdysseusTheme.WakeFoam, _controller.WakeNote);
        }
    }

    private void DrawQuestRow(QuestSnapshot q, string name, bool msq, bool hasPath)
    {
        var selected = _selectedQuest == q.QuestId;
        // Foam is the Wake's colour: this is the game's own record of where you are.
        ImGui.TextColored(msq ? OdysseusTheme.WakeFoam : OdysseusTheme.TextDisabled, selected ? "●" : "○");
        ImGui.SameLine(0f, 6f);
        var running = _controller.State != RunState.Idle;
        using (ImRaii.Disabled(!hasPath || running))
        {
            if (ImGui.Selectable($"{name}##{q.QuestId}", selected, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X * 0.6f, 0)))
                _selectedQuest = q.QuestId;
        }
        ImGui.SameLine();
        ImGui.TextColored(OdysseusTheme.TextSecondary,
            q.IsReadyToComplete ? "· ready to hand in" : $"· sequence {q.Sequence}");
        if (!hasPath)
        {
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextDisabled, "(no path)");
        }
    }

    private void DrawControls()
    {
        var running = _controller.State is not (RunState.Idle or RunState.Faulted);
        var canStart = _config.Enabled && _presence.CoreReady && _selectedQuest != 0 && _paths.Has(_selectedQuest);

        if (running)
        {
            if (OdysseusTheme.AccentButton("Stop"))
                _controller.Stop();
        }
        else
        {
            using (ImRaii.Disabled(!canStart))
            {
                if (OdysseusTheme.AccentButton(_controller.State == RunState.Faulted ? "Retry" : "Start quest"))
                    _controller.Start(_selectedQuest);
            }
        }

        if (_selectedQuest != 0 && _paths.Has(_selectedQuest))
        {
            ImGui.SameLine(0f, 0f);
            if (ImGui.SmallButton("Edit path"))
                _openEditor(_selectedQuest);
        }

        if (_controller.State == RunState.Faulted)
            ImGui.TextColored(OdysseusTheme.StatusRed, _controller.StatusLine);
        else if (!_config.Enabled)
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Enable Odysseus in settings to run.");
        else if (!_presence.CoreReady)
            ImGui.TextColored(OdysseusTheme.TextDisabled, _presence.MissingSummary());
        else if (!running && _selectedQuest == 0)
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Accept an MSQ quest with a stored path to start.");
    }
}
