using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Windows;

/// <summary>
/// Fix one step. Pick a quest, pick a step, correct what is wrong — a position from where you are
/// standing, a data id from what you have targeted, a stop distance — run just that step to see it
/// work, save. This is how a broken path gets repaired without waiting on anyone; the saved file
/// is ours and never touches the bundle it came from.
/// </summary>
public sealed class PathEditorWindow : OdysseusWindow
{
    private static readonly string[] KindNames = Enum.GetNames<StepKind>();

    private readonly PathStore _store;
    private readonly QuestCatalog _catalog;
    private readonly QuestController _controller;
    private readonly PathRecorder _recorder;
    private readonly Func<uint> _territory;
    private readonly Func<Vector3> _playerPosition;
    private readonly Func<uint?> _targetDataId;

    private ushort _questId;
    private QuestPath? _path;
    private int _selectedSeq = -1;
    private int _selectedStep = -1;
    private bool _dirty;
    private string _status = string.Empty;
    private int _questInput;
    /// <summary>A finished recording waiting on the overwrite decision.</summary>
    private QuestPath? _recordedPending;

    public PathEditorWindow(
        PathStore store, QuestCatalog catalog, QuestController controller, PathRecorder recorder,
        Func<uint> territory, Func<Vector3> playerPosition, Func<uint?> targetDataId)
        : base("Odysseus Path Editor##OdysseusPaths")
    {
        _store = store;
        _catalog = catalog;
        _controller = controller;
        _recorder = recorder;
        _territory = territory;
        _playerPosition = playerPosition;
        _targetDataId = targetDataId;
        _recorder.Note += n => _status = $"Recording: {n}";
        _recorder.StepRecorded += s => _status = $"Recorded: {s}";
        Size = new Vector2(760, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(600, 380), MaximumSize = new Vector2(1400, 1000) };
    }

    /// <summary>Open on a specific quest (from the run window).</summary>
    public void Open(ushort questId)
    {
        Load(questId);
        IsOpen = true;
    }

    private void Load(ushort questId)
    {
        _questId = questId;
        _questInput = questId;
        _path = _store.ForQuest(questId);
        _selectedSeq = _path?.Sequences.Count > 0 ? 0 : -1;
        _selectedStep = _path?.Sequences.FirstOrDefault()?.Steps.Count > 0 ? 0 : -1;
        _dirty = false;
        _status = _path is null ? $"No stored path for quest {questId}." : string.Empty;
    }

    /// <summary>The recorder's quest, so the plugin knows what to observe.</summary>
    public ushort RecordingQuestId { get; private set; }

    public override void Draw()
    {
        DrawHeader();
        DrawRecorderBar();
        ImGui.Separator();
        var avail = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("##steps", new Vector2(avail.X * 0.42f, avail.Y - 28f), true);
        DrawStepList();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##edit", new Vector2(0, avail.Y - 28f), true);
        DrawStepEditor();
        ImGui.EndChild();
        DrawFooter();
    }

    private void DrawHeader()
    {
        ImGui.SetNextItemWidth(90f);
        ImGui.InputInt("Quest id", ref _questInput, 0, 0);
        ImGui.SameLine();
        if (ImGui.SmallButton("Load") && _questInput is > 0 and <= ushort.MaxValue)
            Load((ushort)_questInput);
        if (_controller.QuestId != 0 && _controller.QuestId != _questId)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Current run ({_controller.QuestId})"))
                Load(_controller.QuestId);
        }
        if (_path is { } p)
        {
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextPrimary, $"{p.Name}");
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextSecondary, $"· {p.Category} · {p.Sequences.Count} seq / {p.StepCount} steps" +
                                                             (p.LastChecked is { } lc ? $" · upstream checked {lc}" : ""));
        }
    }

    // ── recorder ──

    private void DrawRecorderBar()
    {
        if (_recordedPending is { } pending)
        {
            ImGui.TextColored(OdysseusTheme.StatusYellow,
                $"Recorded {pending.StepCount} steps for {pending.Name}. A stored path already exists for quest {pending.QuestId}.");
            if (OdysseusTheme.SolidButton("Overwrite stored path", OdysseusTheme.RedDark, new Vector2(170, 24)))
            {
                _store.Save(pending);
                _recordedPending = null;
                Load(pending.QuestId);
                _status = "Recording saved over the stored path.";
            }
            ImGui.SameLine();
            if (ImGui.Button("Discard recording", new Vector2(150, 24)))
            {
                _recordedPending = null;
                _status = "Recording discarded.";
            }
            return;
        }

        if (_recorder.IsRecording)
        {
            ImGui.TextColored(OdysseusTheme.StatusRed, "● REC");
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextSecondary, $"{_recorder.Path!.Name} · {_recorder.StepCount} steps");
            ImGui.SameLine(0f, 12f);
            if (ImGui.Button("Walk-to here", new Vector2(100, 22)))
                _recorder.AddWalkToHere();
            ImGui.SameLine();
            if (OdysseusTheme.SolidButton("Stop & keep", OdysseusTheme.GreenDark, new Vector2(100, 22)))
                StopRecording(keep: true);
            ImGui.SameLine();
            if (OdysseusTheme.SolidButton("Stop & discard", OdysseusTheme.RedDark, new Vector2(110, 22)))
                StopRecording(keep: false);
            // While recording the list mirrors the recorder's path.
            _path = _recorder.Path;
            _questId = _recorder.Path.QuestId;
            return;
        }

        var canRecord = _questInput is > 0 and <= ushort.MaxValue && _controller.State is RunState.Idle or RunState.Faulted;
        using (ImRaii.Disabled(!canRecord))
        {
            if (OdysseusTheme.SolidButton("● Record", OdysseusTheme.RedDark, new Vector2(90, 22)))
            {
                var id = (ushort)_questInput;
                var listing = _catalog.ById(id);
                var category = listing?.IsMainScenario == true ? "Recorded/MSQ" : "Recorded";
                _recorder.Begin(id, listing?.Name ?? $"Quest {id}", category);
                RecordingQuestId = id;
                _selectedSeq = -1;
                _selectedStep = -1;
                _dirty = false;
                _status = $"Recording {listing?.Name ?? id.ToString()} — play the quest; talk, fight, teleport, and it writes the steps.";
            }
        }
        OdysseusTheme.HelpMarker(
            "Records a path from play for the quest id above: each NPC you talk to, each fight, each teleport and zone " +
            "line, each instance, plus the quest-variable landmarks the Wake resumes on. Add waypoints with \"Walk-to here\". " +
            "Stop & keep saves it as this quest's path.");
    }

    private void StopRecording(bool keep)
    {
        var path = _recorder.Finish();
        RecordingQuestId = 0;
        if (path is null || !keep)
        {
            _status = "Recording discarded.";
            Load(_questId);
            return;
        }
        if (_store.Has(path.QuestId))
        {
            _recordedPending = path;
            return;
        }
        _store.Save(path);
        Load(path.QuestId);
        _status = $"Recording saved: {path.StepCount} steps.";
    }

    /// <summary>
    /// The step the run is on right now, so the list reads as a live view rather than a document.
    /// Only when the loaded path is the quest actually running — the editor is often open on
    /// something else entirely.
    /// </summary>
    private bool IsRunning(int sequence, int stepIndex)
        => _path is not null
           && _controller.State is not (RunState.Idle or RunState.Faulted)
           && _controller.QuestId == _path.QuestId
           && _controller.Sequence == sequence
           && _controller.StepIndex == stepIndex;

    private void DrawStepList()
    {
        if (_path is null)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Load a quest.");
            return;
        }
        for (var s = 0; s < _path.Sequences.Count; s++)
        {
            var block = _path.Sequences[s];
            var open = ImGui.CollapsingHeader($"Sequence {block.Sequence} ({block.Steps.Count})##seq{s}",
                s == _selectedSeq ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
            if (!open) continue;
            for (var i = 0; i < block.Steps.Count; i++)
            {
                var step = block.Steps[i];
                var selected = s == _selectedSeq && i == _selectedStep;
                var running = IsRunning(block.Sequence, i);
                var color = running ? OdysseusTheme.WakeFoam
                    : StepExecutor.IsSupported(step.Kind) ? OdysseusTheme.TextPrimary
                    : OdysseusTheme.StatusYellow;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                if (ImGui.Selectable($"{(running ? "▶" : " ")} {i + 1}. {step}##s{s}i{i}", selected))
                {
                    _selectedSeq = s;
                    _selectedStep = i;
                }
                ImGui.PopStyleColor();
                if (running && ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Running now — {_controller.StatusLine}");
            }
            if (block.Steps.Count == 0)
                ImGui.TextColored(OdysseusTheme.TextDisabled, "  (game advances this one)");
        }
    }

    private QuestStep? Selected
        => _path is not null && _selectedSeq >= 0 && _selectedSeq < _path.Sequences.Count
           && _selectedStep >= 0 && _selectedStep < _path.Sequences[_selectedSeq].Steps.Count
            ? _path.Sequences[_selectedSeq].Steps[_selectedStep]
            : null;

    private void DrawStepEditor()
    {
        var step = Selected;
        if (step is null)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Select a step.");
            if (_path is not null && _selectedSeq >= 0 && ImGui.Button("Add WalkTo here"))
                InsertWalkToHere();
            return;
        }
        var running = _controller.State is not (RunState.Idle or RunState.Faulted);

        OdysseusTheme.SectionHeader($"SEQ {_path!.Sequences[_selectedSeq].Sequence} · STEP {_selectedStep + 1}");

        var kindIndex = Array.IndexOf(KindNames, step.Kind.ToString());
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Kind", ref kindIndex, KindNames, KindNames.Length))
        {
            step.Kind = Enum.Parse<StepKind>(KindNames[kindIndex]);
            step.KindName = step.Kind.ToString();
            _dirty = true;
        }
        if (!StepExecutor.IsSupported(step.Kind))
        {
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.StatusYellow, "not runnable yet");
        }

        // DataId
        var dataId = (int)(step.DataId ?? 0);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("DataId", ref dataId, 0, 0)) { step.DataId = dataId > 0 ? (uint)dataId : null; _dirty = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("From target"))
        {
            if (_targetDataId() is { } t) { step.DataId = t; _dirty = true; }
            else _status = "Nothing targeted.";
        }

        // Position
        var pos = step.Position ?? Vector3.Zero;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.InputFloat3("Position", ref pos)) { step.Position = pos; _dirty = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Here")) { step.Position = _playerPosition(); step.TerritoryId = _territory(); _dirty = true; }
        ImGui.SameLine();
        if (step.Position is not null && ImGui.SmallButton("Clear##pos")) { step.Position = null; _dirty = true; }

        var terr = (int)step.TerritoryId;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Territory", ref terr, 0, 0)) { step.TerritoryId = (uint)Math.Max(0, terr); _dirty = true; }
        ImGui.SameLine();
        ImGui.TextColored(OdysseusTheme.TextDisabled, $"(you: {_territory()})");

        var stop = step.StopDistance ?? 0f;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputFloat("Stop distance", ref stop, 0, 0, "%.1f")) { step.StopDistance = stop > 0 ? stop : null; _dirty = true; }
        OdysseusTheme.HelpMarker("0 = default (3y for interactions, 0.5y for WalkTo).");

        var fly = step.Fly;
        if (ImGui.Checkbox("Fly", ref fly)) { step.Fly = fly; _dirty = true; }
        ImGui.SameLine();
        var mount = step.Mount != false;
        if (ImGui.Checkbox("Allow mount", ref mount)) { step.Mount = mount ? null : false; _dirty = true; }
        ImGui.SameLine();
        var noNav = step.DisableNavmesh;
        if (ImGui.Checkbox("No navmesh", ref noNav)) { step.DisableNavmesh = noNav; _dirty = true; }
        ImGui.SameLine();
        var dismount = step.Dismount;
        if (ImGui.Checkbox("Dismount", ref dismount)) { step.Dismount = dismount; _dirty = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join('\u000A',
                "Get off the mount before this step and stay off for it.",
                "For a walk that has to thread somewhere a chocobo will not,",
                "or something the game refuses from the saddle."));

        var aetheryte = step.AetheryteShortcut ?? string.Empty;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.InputText("Aetheryte", ref aetheryte, 80)) { step.AetheryteShortcut = aetheryte.Length > 0 ? aetheryte : null; _dirty = true; }

        var cfc = (int)(step.ContentFinderConditionId ?? 0);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Duty (CFC id)", ref cfc, 0, 0)) { step.ContentFinderConditionId = cfc > 0 ? (uint)cfc : null; _dirty = true; }

        var item = (int)(step.ItemId ?? 0);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Item id", ref item, 0, 0)) { step.ItemId = item > 0 ? (uint)item : null; _dirty = true; }

        var emote = step.Emote ?? string.Empty;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.InputText("Emote", ref emote, 40)) { step.Emote = emote.Length > 0 ? emote : null; _dirty = true; }

        var comment = step.Comment ?? string.Empty;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("Note", ref comment, 200)) { step.Comment = comment.Length > 0 ? comment : null; _dirty = true; }

        if (step.CompletionQuestVariablesFlags is { } flags)
            ImGui.TextColored(OdysseusTheme.WakeFoam, "Completion mask: " + string.Join(' ', flags.Select(f => f?.ToString() ?? "·")));

        ImGui.Spacing();
        using (ImRaii.Disabled(running))
        {
            if (ImGui.Button("Run this step"))
            {
                if (!_controller.StepOnce(step)) _status = "Cannot run: a quest is in progress.";
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete step"))
            {
                _path.Sequences[_selectedSeq].Steps.RemoveAt(_selectedStep);
                _selectedStep = Math.Min(_selectedStep, _path.Sequences[_selectedSeq].Steps.Count - 1);
                _dirty = true;
            }

            if (ImGui.Button("Insert WalkTo before"))
                InsertWalkToHere(before: true);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join('\u000A', "A waypoint on the way to this step, at your feet.", "This is the one that unsticks a step that will not path."));
            ImGui.SameLine();
            if (ImGui.Button("Insert WalkTo after"))
                InsertWalkToHere(before: false);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A waypoint for once this step is done.");
        }
        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                _controller.Stop();
        }
    }

    /// <summary>
    /// Where an inserted step lands. "Before" takes the selected step's own place and pushes it
    /// down; "after" goes one past it. Both are clamped, because nothing is selected when the
    /// sequence is empty and <c>_selectedStep</c> is -1 then.
    /// </summary>
    internal static int InsertIndex(int selectedStep, int stepCount, bool before)
        => before
            ? Math.Clamp(selectedStep, 0, stepCount)
            : Math.Clamp(selectedStep + 1, 0, stepCount);

    /// <summary>
    /// Put a waypoint at the player's feet, before or after the selected step.
    ///
    /// <para>
    /// Before is the one that gets used: a step that will not path — an NPC in a cave, a route the
    /// author expected you to fly — needs the waypoint on the way <i>to</i> it, and a sequence of
    /// one step has no "after" worth having.
    /// </para>
    /// </summary>
    private void InsertWalkToHere(bool before = false)
    {
        if (_path is null || _selectedSeq < 0) return;
        var steps = _path.Sequences[_selectedSeq].Steps;
        var step = new QuestStep
        {
            Kind = StepKind.WalkTo, KindName = "WalkTo", Position = _playerPosition(), TerritoryId = _territory(),
            Comment = "added in editor",
        };
        var at = InsertIndex(_selectedStep, steps.Count, before);
        steps.Insert(at, step);
        _selectedStep = at;
        _dirty = true;
    }

    private void DrawFooter()
    {
        using (ImRaii.Disabled(!_dirty || _path is null))
        {
            if (OdysseusTheme.AccentButton(_dirty ? "Save path" : "Saved", 24f) && _path is not null)
            {
                _store.Save(_path);
                _dirty = false;
                _status = $"Saved {_path.Name} to {_store.Directory}.";
            }
        }
        if (_status.Length > 0 || _controller.StatusLine.Length > 0)
            ImGui.TextColored(OdysseusTheme.TextSecondary, _status.Length > 0 ? _status : _controller.StatusLine);
    }
}
