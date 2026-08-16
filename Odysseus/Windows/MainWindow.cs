using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Odysseus.Config;
using Odysseus.Services.Fleet;
using Odysseus.Services.Ipc;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Windows;

/// <summary>Everything the main window needs to reach, so its constructor stays readable.</summary>
public sealed record MainWindowDeps(
    OdysseusConfig Config,
    Action SaveConfig,
    PluginPresence Presence,
    IQuestStateReader Quests,
    QuestCatalog Catalog,
    PathStore Paths,
    QuestController Controller,
    StoryFrontier Frontier,
    FleetPublisher Fleet,
    Action OpenConfig,
    Action OpenFleet,
    Action OpenLog,
    Action OpenDebug,
    Action<ushort> OpenEditor,
    Func<uint?> TargetDataId,
    Func<Vector3?> TargetPosition,
    Func<Vector3> PlayerPosition,
    Func<uint> Territory,
    Func<string> JobAbbreviation);

/// <summary>
/// The main window: a compact vertical panel in the QST style — state chip in the title, quest
/// line with id and job chips, sequence · step · elapsed, step kind + progress bar, the six quest
/// variables, status, the Wake's line, an icon control row, and collapsible Fleet / Quick access
/// / Path tools / Remaining tasks. <c>CompactMode</c> keeps only the top of that.
/// </summary>
public sealed class MainWindow : OdysseusWindow
{
    private readonly MainWindowDeps _d;
    private ushort _selectedQuest;
    private QuestListing? _frontier;
    private DateTime _frontierAt;
    private static readonly TimeSpan FrontierRefresh = TimeSpan.FromSeconds(2);

    public MainWindow(MainWindowDeps deps) : base("Odysseus###OdysseusMain")
    {
        _d = deps;
        Size = new Vector2(360, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(300, 140), MaximumSize = new Vector2(700, 1200) };
    }

    private OdysseusConfig Cfg => _d.Config;
    private QuestController Ctl => _d.Controller;
    private bool Running => Ctl.State is not (RunState.Idle or RunState.Faulted);
    private bool CanStart => Cfg.Enabled && _d.Presence.CoreReady && _selectedQuest != 0 && _d.Paths.Has(_selectedQuest);

    public override void PreDraw()
    {
        base.PreDraw();
        // The title carries the state so it reads even when collapsed. ### keeps the window id stable.
        var state = !Cfg.Enabled ? "Disabled" : Ctl.State.ToString();
        WindowName = $"Odysseus v{OdysseusPlugin.PluginVersion} · {state}###OdysseusMain";
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        try
        {
            RefreshFrontier();
            DrawTopRow();
            DrawNotice();
            DrawQuestBlock();
            DrawPrimaryControls();
            if (Cfg.CompactMode)
                return;
            DrawSecondaryControls();
            DrawFleetSection();
            DrawQuickAccessSection();
            DrawPathToolsSection();
            DrawRemainingSection();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    // ── top row: state chip · job chip · compact toggle · settings ──

    private void DrawTopRow()
    {
        var state = Ctl.State;
        var label = !Cfg.Enabled ? "Disabled" : state == RunState.Faulted ? "Faulted" : state.ToString();
        if (state == RunState.Faulted && Cfg.Enabled)
            OdysseusTheme.Chip("● " + label, OdysseusTheme.RedDark, OdysseusTheme.Current.ButtonText);
        else if (Running)
            OdysseusTheme.StateChip("● " + label);
        else
            OdysseusTheme.IdChip("○ " + label);

        if (Running)
        {
            ImGui.SameLine(0f, 8f);
            ImGui.TextColored(OdysseusTheme.TextSecondary, $"{Ctl.Elapsed:hh\\:mm\\:ss}");
        }

        var right = ImGui.GetWindowContentRegionMax().X;
        var w = 26f;
        ImGui.SameLine(right - w * 2 - 6f);
        if (OdysseusTheme.IconButton("compact", Cfg.CompactMode ? FontAwesomeIcon.Expand : FontAwesomeIcon.Compress,
                Cfg.CompactMode ? "Full view" : "Compact view", new Vector2(w, 22)))
        {
            Cfg.CompactMode = !Cfg.CompactMode;
            _d.SaveConfig();
        }
        ImGui.SameLine(0f, 4f);
        if (OdysseusTheme.IconButton("settings", FontAwesomeIcon.Cog, "Settings", new Vector2(w, 22)))
            _d.OpenConfig();
    }

    private void DrawNotice()
    {
        var missing = _d.Presence.MissingSummary();
        string? notice = null;
        if (!Cfg.Enabled) notice = "Odysseus is disabled — enable it in Settings.";
        else if (missing.Length > 0) notice = missing;
        else if (Cfg.HandOffDutiesToTheseus && !_d.Presence.Theseus) notice = "Theseus not loaded — dungeons inside quests will stop and wait for you.";
        else if (Cfg.HandOffSoloDuties && !_d.Presence.BossMod) notice = "BossMod not loaded — solo duties will stop and wait for you.";
        if (notice is null)
            return;
        ImGui.TextColored(OdysseusTheme.StatusRed, "Notice");
        ImGui.TextWrapped(notice);
    }

    // ── quest block ──

    private void RefreshFrontier()
    {
        var now = DateTime.UtcNow;
        if (now - _frontierAt < FrontierRefresh) return;
        _frontierAt = now;
        _frontier = _d.Frontier.Current();
    }

    private void DrawQuestBlock()
    {
        ImGui.Spacing();
        var accepted = _d.Quests.ReadAccepted();
        var acceptedMsq = accepted.FirstOrDefault(q => _d.Catalog.ById(q.QuestId)?.IsMainScenario == true);

        // What is "the quest": the running one, else the accepted MSQ, else the frontier.
        ushort questId = Running || Ctl.State == RunState.Faulted ? Ctl.QuestId
            : acceptedMsq.IsAvailable ? acceptedMsq.QuestId
            : _frontier?.QuestId ?? 0;
        if (_selectedQuest == 0 || (!Running && _selectedQuest != questId))
            _selectedQuest = questId;

        if (questId == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "No Main Scenario quest to run — the story is finished, or nothing is unlocked.");
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Debug shows what the Scenario Guide and the chain say.");
            return;
        }

        var listing = _d.Catalog.ById(questId);
        var snap = _d.Quests.Read(questId);
        var name = listing?.Name ?? $"Quest {questId}";

        ImGui.TextColored(OdysseusTheme.TextPrimary, "Quest: ");
        ImGui.SameLine(0f, 0f);
        ImGui.TextColored(OdysseusTheme.TextPrimary, name);
        ImGui.SameLine(0f, 6f);
        OdysseusTheme.IdChip($"#{questId}");
        ImGui.SameLine(0f, 6f);
        OdysseusTheme.JobChip(_d.JobAbbreviation());
        var hasPath = _d.Paths.Has(questId);
        if (!hasPath)
        {
            ImGui.SameLine(0f, 6f);
            ImGui.TextColored(OdysseusTheme.StatusYellow, "(no path)");
        }

        // Seq · step · counts
        var seqText = snap.IsAvailable ? $"Seq {snap.Sequence}" : "Not yet accepted";
        var stepText = Running && Ctl.StepCount > 0 ? $" · Step {Math.Min(Ctl.StepIndex + 1, Ctl.StepCount)}/{Ctl.StepCount}" : "";
        var doneText = Ctl.QuestsThisRun > 0 ? $" · {Ctl.QuestsThisRun} done" : "";
        var levelText = !snap.IsAvailable && listing is not null ? $" · Lv {listing.ClassJobLevel}" : "";
        ImGui.TextColored(OdysseusTheme.TextSecondary, seqText + stepText + doneText + levelText);
        if (!snap.IsAvailable && !Running)
        {
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextDisabled, $"({_d.Frontier.LastSource})");
        }

        // Step kind + target, progress
        var step = Ctl.CurrentStep;
        if (Running && step is not null)
        {
            OdysseusTheme.KindBadge(step.KindName ?? step.Kind.ToString());
            ImGui.SameLine(0f, 6f);
            var target = (step.DataId is { } d ? d.ToString() : "") + (step.Position is { } p ? $" @({p.X:F0},{p.Y:F0},{p.Z:F0})" : "");
            ImGui.TextColored(OdysseusTheme.TextSecondary, target.Trim());
            OdysseusTheme.ProgressBar(Ctl.StepCount > 0 ? Ctl.StepIndex / (float)Ctl.StepCount : 0f);
        }

        // QW variables + the current step's mask
        if (snap.IsAvailable)
        {
            var mask = step?.CompletionQuestVariablesFlags;
            var maskText = mask is null ? "" : "  ·  mask " + string.Join(' ', mask.Select(m => m?.ToString() ?? "·"));
            ImGui.TextColored(OdysseusTheme.TextDisabled, $"QW: {string.Join(' ', snap.Variables.ToArray())}{maskText}");
        }

        // Status + Wake
        if (Ctl.StatusLine.Length > 0)
            ImGui.TextColored(Ctl.State == RunState.Faulted ? OdysseusTheme.StatusRed : OdysseusTheme.TextSecondary, Ctl.StatusLine);
        if (Ctl.WakeNote.Length > 0)
        {
            ImGui.TextColored(OdysseusTheme.WakeFoam, "Wake: " + Ctl.WakeNote);
            if (Ctl.AwaitingResumeConfirm)
            {
                if (OdysseusTheme.SolidButton("Resume from there", OdysseusTheme.WakeDim, new Vector2(140, 22)))
                    Ctl.ConfirmResume();
                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(64, 22)))
                    Ctl.Stop();
            }
        }
        if (Ctl.StopAfterQuest && Running)
            ImGui.TextColored(OdysseusTheme.StatusYellow, "Stopping after this quest");
    }

    // ── controls ──

    private void DrawPrimaryControls()
    {
        ImGui.Spacing();
        var neutral = OdysseusTheme.NeutralDark;
        var sq = new Vector2(30, 26);

        if (Running)
        {
            if (OdysseusTheme.IconButton("stop", FontAwesomeIcon.Stop, "Stop. Nothing is lost — Start resumes from the game's own quest state.", sq)) Ctl.Stop();
            ImGui.SameLine();
            var pausing = Ctl.PauseAfterStep;
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.StepForward, pausing ? "Step armed" : "Step",
                    pausing ? OdysseusTheme.YellowDark : neutral, "Finish the current step, then stop.", darkText: pausing))
                Ctl.PauseAfterStep = !pausing;
            ImGui.SameLine();
            var armed = Ctl.StopAfterQuest;
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Flag, "Stop after", armed ? OdysseusTheme.YellowDark : neutral,
                    armed ? "Armed — hands in this quest, then stops. Click to keep going." : "Hand in this quest, then stop before the next.", darkText: armed))
                Ctl.StopAfterQuest = !armed;
            ImGui.SameLine();
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Redo, "Stuck?", neutral, "Run the current step again from the top."))
                Ctl.RetryStep();
        }
        else if (Ctl.State == RunState.Faulted)
        {
            using (ImRaii.Disabled(!CanStart))
            {
                if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Play, "Retry", OdysseusTheme.GreenDark, "Re-reads the quest state and picks up from there."))
                    Ctl.Start(_selectedQuest);
            }
            ImGui.SameLine();
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Redo, "Stuck?", neutral, "Run the failed step again.")) Ctl.RetryStep();
            ImGui.SameLine();
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.FastForward, "Skip", neutral, "Skip the failed step and continue.")) Ctl.SkipStep();
            ImGui.SameLine();
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Times, "Clear", OdysseusTheme.RedDark, "Clear the fault and go idle.")) Ctl.Stop();
        }
        else
        {
            using (ImRaii.Disabled(!CanStart))
            {
                if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Play, "Start", OdysseusTheme.GreenDark, StartTooltip()))
                    Ctl.Start(_selectedQuest);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!CanStart))
            {
                // Start resets the flag, so arm it after.
                if (OdysseusTheme.IconTextButton(FontAwesomeIcon.StepForward, "Step", neutral, "Run exactly one step, then stop.") && Ctl.Start(_selectedQuest))
                    Ctl.PauseAfterStep = true;
            }
        }
    }

    private string StartTooltip()
        => CanStart ? "Start the quest. Picks up wherever the game says it is."
            : !Cfg.Enabled ? "Enable Odysseus in Settings."
            : !_d.Presence.CoreReady ? _d.Presence.MissingSummary()
            : _selectedQuest == 0 ? "No Main Scenario quest to start."
            : "No stored path for this quest — import in Settings › Paths.";

    private void DrawSecondaryControls()
    {
        var neutral = OdysseusTheme.NeutralDark;
        var sq = new Vector2(30, 24);
        using (ImRaii.Disabled(!Running))
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.FastForward, "Skip", neutral, "Skip the current step and move on.")) Ctl.SkipStep();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_selectedQuest == 0 || !_d.Paths.Has(_selectedQuest)))
        {
            if (OdysseusTheme.IconButton("edit", FontAwesomeIcon.Edit, "Edit this quest's path.", sq)) _d.OpenEditor(_selectedQuest);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!Running || Ctl.CurrentStep?.Position is null))
        {
            if (OdysseusTheme.IconButton("locate", FontAwesomeIcon.MapMarkerAlt, "Walk to the current step's position on its own.", sq) && Ctl.CurrentStep is { Position: { } p })
                _d.Controller.StepOnce(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", Position = p, TerritoryId = _d.Territory(), Comment = "walk to current step" });
        }
        ImGui.SameLine();
        if (OdysseusTheme.IconTextButton(FontAwesomeIcon.List, "Log", neutral, "Step log.")) _d.OpenLog();
        ImGui.SameLine();
        if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Users, "Fleet", neutral, "Fleet dashboard.")) _d.OpenFleet();
    }

    // ── sections ──

    private void DrawFleetSection()
    {
        var peers = _d.Fleet.Roster.Peers(DateTime.UtcNow, TimeSpan.FromSeconds(Math.Max(1f, Cfg.PeerStaleSeconds)));
        if (!ImGui.CollapsingHeader($"FLEET ({1 + peers.Count})###fleetsec", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        if (_d.Fleet.Own is { } own) FleetRow(own, OdysseusTheme.PeerOnline, "you");
        foreach (var p in peers)
        {
            var color = p.Liveness switch { PeerLiveness.Online => OdysseusTheme.PeerOnline, PeerLiveness.Stale => OdysseusTheme.PeerStale, _ => OdysseusTheme.PeerGone };
            FleetRow(p.Status, color, p.Liveness == PeerLiveness.Online ? null : $"{p.Age.TotalSeconds:F0}s");
        }
    }

    private static void FleetRow(FleetStatus s, Vector4 dot, string? tag)
    {
        ImGui.TextColored(dot, "●");
        ImGui.SameLine(0f, 6f);
        ImGui.TextColored(OdysseusTheme.TextPrimary, s.Character);
        ImGui.SameLine(0f, 8f);
        var quest = s.QuestId == 0 ? "—" : $"{s.QuestName} · {s.Sequence}";
        ImGui.TextColored(OdysseusTheme.TextSecondary, $"{quest} · {s.State}" + (tag is null ? "" : $" · {tag}"));
    }

    private void DrawQuickAccessSection()
    {
        if (!ImGui.CollapsingHeader("QUICK ACCESS###quick"))
            return;
        var h = new Vector2(0, 22);
        if (ImGui.Button("Settings##qa", h)) _d.OpenConfig();
        ImGui.SameLine();
        if (ImGui.Button("Fleet##qa", h)) _d.OpenFleet();
        ImGui.SameLine();
        if (ImGui.Button("Log##qa", h)) _d.OpenLog();
        ImGui.SameLine();
        if (ImGui.Button("Debug##qa", h)) _d.OpenDebug();
        ImGui.SameLine();
        if (ImGui.Button("Paths##qa", h)) _d.OpenEditor(_selectedQuest);

        // Other accepted quests, for reference (and selectable when idle, if they have a path).
        var others = _d.Quests.ReadAccepted().Where(q => _d.Catalog.ById(q.QuestId)?.IsMainScenario != true).ToList();
        if (others.Count > 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, $"Other quests ({others.Count})");
            foreach (var q in others)
            {
                var hasPath = _d.Paths.Has(q.QuestId);
                using (ImRaii.Disabled(!hasPath || Running))
                {
                    if (ImGui.Selectable($"  {_d.Catalog.NameOf(q.QuestId)}##oq{q.QuestId}", _selectedQuest == q.QuestId))
                        _selectedQuest = q.QuestId;
                }
                ImGui.SameLine();
                ImGui.TextColored(OdysseusTheme.TextDisabled, hasPath ? $"· seq {q.Sequence}" : "· no path");
            }
        }
    }

    private void DrawPathToolsSection()
    {
        if (!ImGui.CollapsingHeader("PATH TOOLS###pathtools"))
            return;
        ImGui.TextColored(OdysseusTheme.TextDisabled, "Tests — one synthetic step through the real engine:");
        using (ImRaii.Disabled(Running || !Cfg.Enabled))
        {
            if (ImGui.SmallButton("Walk to target")) TestStep("walk", StepKind.WalkTo, needTarget: true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Interact target")) TestStep("interact", StepKind.Interact, needTarget: true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Mount")) TestStep("mount", StepKind.WalkTo, needTarget: false);
        }
        if (Running && Ctl.Path is null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop test")) Ctl.Stop();
        }
        if (ImGui.SmallButton("Open editor / recorder")) _d.OpenEditor(_selectedQuest);
    }

    private void DrawRemainingSection()
    {
        var remaining = Ctl.RemainingSteps;
        if (!ImGui.CollapsingHeader($"REMAINING TASKS  {remaining.Count}###remaining"))
            return;
        if (remaining.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, Running ? "Waiting for the game to advance the sequence." : "—");
            return;
        }
        var i = 0;
        foreach (var s in remaining.Take(12))
        {
            ImGui.TextColored(i == 0 ? OdysseusTheme.TextPrimary : OdysseusTheme.TextDisabled, $"{(i == 0 ? ">" : "-")} {s}");
            i++;
        }
        if (remaining.Count > 12)
            ImGui.TextColored(OdysseusTheme.TextDisabled, $"... {remaining.Count - 12} more");
    }

    // ── helpers ──

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    private void TestStep(string which, StepKind kind, bool needTarget)
    {
        var target = _d.TargetPosition();
        var dataId = _d.TargetDataId();
        if (needTarget && (target is null || dataId is null))
            return;
        var step = new QuestStep { Kind = kind, KindName = kind.ToString(), TerritoryId = _d.Territory(), Comment = $"test: {which}" };
        switch (which)
        {
            case "walk": step.Position = target; step.StopDistance = 2f; break;
            case "interact": step.Position = target; step.DataId = dataId; break;
            case "mount": step.Position = _d.PlayerPosition() + new Vector3(40f, 0f, 0f); step.Mount = true; break;
        }
        Ctl.StepOnce(step);
    }
}
