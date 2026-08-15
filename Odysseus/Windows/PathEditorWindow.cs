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
public sealed class PathEditorWindow : Window
{
    private static readonly string[] KindNames = Enum.GetNames<StepKind>();

    private readonly PathStore _store;
    private readonly QuestCatalog _catalog;
    private readonly QuestController _controller;
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

    public PathEditorWindow(
        PathStore store, QuestCatalog catalog, QuestController controller,
        Func<uint> territory, Func<Vector3> playerPosition, Func<uint?> targetDataId)
        : base("Odysseus Path Editor##OdysseusPaths")
    {
        _store = store;
        _catalog = catalog;
        _controller = controller;
        _territory = territory;
        _playerPosition = playerPosition;
        _targetDataId = targetDataId;
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

    public override void Draw()
    {
        DrawHeader();
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
                var color = StepExecutor.IsSupported(step.Kind) ? OdysseusTheme.TextPrimary : OdysseusTheme.StatusYellow;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                if (ImGui.Selectable($"  {i + 1}. {step}##s{s}i{i}", selected))
                {
                    _selectedSeq = s;
                    _selectedStep = i;
                }
                ImGui.PopStyleColor();
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
            if (ImGui.Button("Insert WalkTo here after"))
                InsertWalkToHere();
            ImGui.SameLine();
            if (ImGui.Button("Delete step"))
            {
                _path.Sequences[_selectedSeq].Steps.RemoveAt(_selectedStep);
                _selectedStep = Math.Min(_selectedStep, _path.Sequences[_selectedSeq].Steps.Count - 1);
                _dirty = true;
            }
        }
        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                _controller.Stop();
        }
    }

    private void InsertWalkToHere()
    {
        if (_path is null || _selectedSeq < 0) return;
        var steps = _path.Sequences[_selectedSeq].Steps;
        var step = new QuestStep
        {
            Kind = StepKind.WalkTo, KindName = "WalkTo", Position = _playerPosition(), TerritoryId = _territory(),
            Comment = "added in editor",
        };
        var at = Math.Min(steps.Count, _selectedStep + 1);
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
