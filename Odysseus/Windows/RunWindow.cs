using System;
using System.Linq;
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
/// quest, and the controls. Same control conventions as SealBreaker — solid green Start, solid red
/// Stop, a yellow "stop after this quest" that arms rather than acts, compact Start/Stop pinned in
/// the header, an elapsed timer, and a row of subsystem tests that exercise the real engine on
/// one synthetic step.
/// </summary>
public sealed class RunWindow : Window
{
    private readonly OdysseusConfig _config;
    private readonly PluginPresence _presence;
    private readonly IQuestStateReader _quests;
    private readonly QuestCatalog _catalog;
    private readonly PathStore _paths;
    private readonly QuestController _controller;
    private readonly StoryFrontier _storyFrontier;
    private readonly Action _openConfig;
    private readonly Action _openFleet;
    private readonly Action _openLog;
    private readonly Action<ushort> _openEditor;
    private readonly Func<uint?> _targetDataId;
    private readonly Func<Vector3?> _targetPosition;
    private readonly Func<Vector3> _playerPosition;
    private readonly Func<uint> _territory;

    private ushort _selectedQuest;

    // The story frontier is 1,000+ completion-bit reads; refresh it on a timer, not every frame.
    private QuestListing? _frontier;
    private DateTime _frontierAt;
    private static readonly TimeSpan FrontierRefresh = TimeSpan.FromSeconds(2);

    public RunWindow(
        OdysseusConfig config,
        PluginPresence presence,
        IQuestStateReader quests,
        QuestCatalog catalog,
        PathStore paths,
        QuestController controller,
        StoryFrontier storyFrontier,
        Action openConfig,
        Action openFleet,
        Action openLog,
        Action<ushort> openEditor,
        Func<uint?> targetDataId,
        Func<Vector3?> targetPosition,
        Func<Vector3> playerPosition,
        Func<uint> territory)
        : base("Odysseus##OdysseusRun")
    {
        _playerPosition = playerPosition;
        _config = config;
        _presence = presence;
        _quests = quests;
        _catalog = catalog;
        _paths = paths;
        _controller = controller;
        _storyFrontier = storyFrontier;
        _openConfig = openConfig;
        _openFleet = openFleet;
        _openLog = openLog;
        _openEditor = openEditor;
        _targetDataId = targetDataId;
        _targetPosition = targetPosition;
        _territory = territory;

        Size = new Vector2(460, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 320),
            MaximumSize = new Vector2(800, 900),
        };
    }

    private bool Running => _controller.State is not (RunState.Idle or RunState.Faulted);

    private bool CanStart => _config.Enabled && _presence.CoreReady && _selectedQuest != 0 && _paths.Has(_selectedQuest);

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        try
        {
            DrawHeader();
            ImGui.Separator();
            DrawQuestPanel();
            ImGui.Spacing();
            DrawControls();
            ImGui.Spacing();
            DrawTests();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    // ── header: state · elapsed · compact start/stop · settings ──

    private void DrawHeader()
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

        if (Running)
        {
            ImGui.SameLine(0f, 12f);
            ImGui.TextColored(OdysseusTheme.AccentWine, $"{_controller.Elapsed:hh\\:mm\\:ss}");
            if (_controller.QuestsThisRun > 0)
            {
                ImGui.SameLine(0f, 8f);
                ImGui.TextColored(OdysseusTheme.TextSecondary, $"· {_controller.QuestsThisRun} done");
            }
        }

        // Compact Start/Stop pinned right, then the window buttons. Widths come from the labels
        // and the anchor from the content region, so nothing clips at the window edge.
        var style = ImGui.GetStyle();
        const float buttonWidth = 64f;
        const float gap = 6f;
        var fleetWidth = ImGui.CalcTextSize("Fleet").X + style.FramePadding.X * 2f + 8f;
        var logWidth = ImGui.CalcTextSize("Log").X + style.FramePadding.X * 2f + 8f;
        var settingsWidth = ImGui.CalcTextSize("Settings").X + style.FramePadding.X * 2f + 8f;
        var total = buttonWidth + fleetWidth + logWidth + settingsWidth + gap * 3f;
        var anchor = ImGui.GetWindowContentRegionMax().X - total;
        if (anchor > ImGui.GetCursorPosX() + 8f)
            ImGui.SameLine(anchor);
        else
            ImGui.SameLine(0f, 12f);

        if (Running)
        {
            if (OdysseusTheme.StopButton("Stop##hdr", new Vector2(buttonWidth, 22)))
                _controller.Stop();
        }
        else
        {
            using (ImRaii.Disabled(!CanStart))
            {
                if (OdysseusTheme.StartButton(_controller.State == RunState.Faulted ? "Retry##hdr" : "Start##hdr", new Vector2(buttonWidth, 22)))
                    _controller.Start(_selectedQuest);
            }
        }
        ImGui.SameLine(0f, gap);
        if (ImGui.Button("Fleet", new Vector2(fleetWidth, 22)))
            _openFleet();
        ImGui.SameLine(0f, gap);
        if (ImGui.Button("Log", new Vector2(logWidth, 22)))
            _openLog();
        ImGui.SameLine(0f, gap);
        if (ImGui.Button("Settings", new Vector2(settingsWidth, 22)))
            _openConfig();

        if (_controller.StopAfterQuest && Running)
        {
            ImGui.TextColored(OdysseusTheme.StatusYellow, "⚑ Stopping after this quest");
        }
    }

    // ── quest panel ──

    private void DrawQuestPanel()
    {
        OdysseusTheme.SectionHeader("MAIN SCENARIO");

        var accepted = _quests.ReadAccepted();
        var now = DateTime.UtcNow;
        if (now - _frontierAt > FrontierRefresh)
        {
            _frontierAt = now;
            _frontier = _storyFrontier.Current();
        }

        // An accepted MSQ quest is the current one; otherwise the story frontier, not yet accepted.
        var anyMsqAccepted = false;
        foreach (var q in accepted)
        {
            var listing = _catalog.ById(q.QuestId);
            if (listing?.IsMainScenario != true)
                continue;
            anyMsqAccepted = true;
            var hasPath = _paths.Has(q.QuestId);
            if (hasPath && _selectedQuest == 0)
                _selectedQuest = q.QuestId;
            DrawQuestRow(q, listing.Name, msq: true, hasPath);
        }
        if (!anyMsqAccepted)
        {
            if (_frontier is { } next)
            {
                var hasPath = _paths.Has(next.QuestId);
                if (hasPath && (_selectedQuest == 0 || !accepted.Any(a => a.QuestId == _selectedQuest)))
                    _selectedQuest = next.QuestId;
                var selected = _selectedQuest == next.QuestId;
                ImGui.TextColored(OdysseusTheme.WakeFoam, selected ? "●" : "○");
                ImGui.SameLine(0f, 6f);
                ImGui.TextColored(OdysseusTheme.TextPrimary, next.Name);
                ImGui.SameLine();
                ImGui.TextColored(OdysseusTheme.TextSecondary, $"· Lv {next.ClassJobLevel} · not yet accepted");
                ImGui.SameLine();
                ImGui.TextColored(OdysseusTheme.TextDisabled, $"({_storyFrontier.LastSource})");
                if (!hasPath)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(OdysseusTheme.TextDisabled, "(no path)");
                }
                ImGui.TextColored(OdysseusTheme.TextDisabled, "Start walks to the quest giver and accepts it.");
            }
            else
            {
                ImGui.TextColored(OdysseusTheme.TextDisabled, "No Main Scenario quest is available — story finished, or nothing unlocked.");
            }
        }

        // Everything else, folded away — Odysseus does not run these, but seeing them helps.
        var others = accepted.Where(q => _catalog.ById(q.QuestId)?.IsMainScenario != true).ToList();
        if (others.Count > 0 && ImGui.CollapsingHeader($"Other quests ({others.Count})##others"))
        {
            foreach (var q in others)
                DrawQuestRow(q, _catalog.NameOf(q.QuestId), msq: false, _paths.Has(q.QuestId));
        }

        if (_controller.State != RunState.Idle)
        {
            ImGui.Spacing();
            ImGui.TextColored(_controller.State == RunState.Faulted ? OdysseusTheme.StatusRed : OdysseusTheme.TextSecondary,
                _controller.StatusLine);
        }
        else if (_controller.StatusLine.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(OdysseusTheme.TextSecondary, _controller.StatusLine);
        }

        if (_controller.WakeNote.Length > 0)
        {
            OdysseusTheme.SectionHeader("THE WAKE", OdysseusTheme.WakeFoam);
            ImGui.TextColored(OdysseusTheme.WakeFoam, _controller.WakeNote);
            if (_controller.AwaitingResumeConfirm)
            {
                if (OdysseusTheme.SolidButton("Resume from there", OdysseusTheme.WakeDim, new Vector2(150, 24)))
                    _controller.ConfirmResume();
                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(80, 24)))
                    _controller.Stop();
            }
        }
    }

    private void DrawQuestRow(QuestSnapshot q, string name, bool msq, bool hasPath)
    {
        var selected = _selectedQuest == q.QuestId;
        var active = Running && _controller.QuestId == q.QuestId;
        // Foam is the Wake's colour: this is the game's own record of where you are.
        ImGui.TextColored(msq ? OdysseusTheme.WakeFoam : OdysseusTheme.TextDisabled, selected || active ? "●" : "○");
        ImGui.SameLine(0f, 6f);
        using (ImRaii.Disabled(!hasPath || Running))
        {
            if (ImGui.Selectable($"{name}##{q.QuestId}", selected, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X * 0.55f, 0)))
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
        else if (selected && !Running)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Edit##{q.QuestId}"))
                _openEditor(q.QuestId);
        }
    }

    // ── controls ──

    private void DrawControls()
    {
        var full = new Vector2(ImGui.GetContentRegionAvail().X, 34);
        var half = new Vector2((full.X - ImGui.GetStyle().ItemSpacing.X) / 2f, 34);

        if (Running)
        {
            if (OdysseusTheme.StopButton("Stop##main", half))
                _controller.Stop();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stops now. Nothing is lost — Start again resumes from the game's own quest state.");

            ImGui.SameLine();
            var armed = _controller.StopAfterQuest;
            if (OdysseusTheme.ArmedButton(armed ? "Cancel stop after quest##main" : "Stop after this quest##main", armed, half))
                _controller.StopAfterQuest = !armed;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(armed
                    ? "Armed — finishes the current quest and stops instead of rolling into the next. Click to keep going."
                    : "Graceful stop: hands in the current quest, then stops before accepting the next one.");
        }
        else if (_controller.State == RunState.Faulted)
        {
            using (ImRaii.Disabled(!CanStart))
            {
                if (OdysseusTheme.StartButton("Retry##main", half))
                    _controller.Start(_selectedQuest);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Re-reads the quest state and picks up from there.");
            ImGui.SameLine();
            if (OdysseusTheme.SolidButton("Clear fault##main", OdysseusTheme.RedDark, half))
                _controller.Stop();
        }
        else
        {
            using (ImRaii.Disabled(!CanStart))
            {
                if (OdysseusTheme.StartButton("Start##main", full))
                    _controller.Start(_selectedQuest);
            }
            if (!CanStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!_config.Enabled ? "Enable Odysseus in Settings."
                    : !_presence.CoreReady ? _presence.MissingSummary()
                    : "Accept an MSQ quest with a stored path to start.");
        }
    }

    // ── tests: one synthetic step through the real executor ──

    private void DrawTests()
    {
        ImGui.TextColored(OdysseusTheme.StatusGrey, "Tests");
        ImGui.SameLine();
        using (ImRaii.Disabled(Running || !_config.Enabled))
        {
            if (ImGui.SmallButton("Walk to target"))
                TestStep("walk", StepKind.WalkTo, needTarget: true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Interact target"))
                TestStep("interact", StepKind.Interact, needTarget: true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Mount"))
                TestStep("mount", StepKind.WalkTo, needTarget: false);
        }
        if (Running && _controller.Path is null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop test"))
                _controller.Stop();
        }
        ImGui.SameLine();
        OdysseusTheme.HelpMarker(
            "Each runs exactly one synthetic step through the real engine — vnavmesh, TextAdvance, the target claim — " +
            "so a subsystem can be checked without a quest. Walk/Interact use your current target; Mount walks 40y away " +
            "and back on a mount.");
    }

    private void TestStep(string which, StepKind kind, bool needTarget)
    {
        var target = _targetPosition();
        var dataId = _targetDataId();
        if (needTarget && (target is null || dataId is null))
        {
            _controller.Stop();
            return;
        }
        var step = new QuestStep
        {
            Kind = kind, KindName = kind.ToString(), TerritoryId = _territory(),
            Comment = $"test: {which}",
        };
        switch (which)
        {
            case "walk":
                step.Position = target;
                step.StopDistance = 2f;
                break;
            case "interact":
                step.Position = target;
                step.DataId = dataId;
                break;
            case "mount":
                // Far enough to trigger the mount, close enough to be harmless.
                step.Position = _playerPosition() + new Vector3(40f, 0f, 0f);
                step.Mount = true;
                break;
        }
        _controller.StepOnce(step);
    }
}
