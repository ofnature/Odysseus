using System;
using System.Collections.Generic;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;

namespace Odysseus.Services.Run;

/// <summary>
/// Drives one quest from its current sequence to completion, ticked every frame.
///
/// <para>
/// The controller never stores progress. Every tick it asks the game for
/// <c>{questId, sequence, variables}</c>, picks the path block for that sequence, and runs the
/// block's steps in order. When the sequence changes — because a step worked, or because the
/// character logged back in mid-quest — the step index is re-derived from the variables
/// (<see cref="SelectResumeIndex"/>). That is the whole of the Wake: a read, not a restore.
/// </para>
///
/// <para>
/// A block whose steps have all run without the sequence moving is <i>replayed</i> after a
/// grace period, at most a few times, because the dominant step kinds are idempotent. A block
/// the path does not have at all is waited on — the game advances some sequences by itself.
/// Both waits are bounded and end in <see cref="RunState.Faulted"/> with a reason.
/// </para>
/// </summary>
/// <summary>What the user allows the run to hand off. Read live so a settings change applies mid-run.</summary>
public interface IRunPolicy
{
    bool HandOffSoloDuties { get; }
    bool HandOffDuties { get; }
    /// <summary>Roll into the next MSQ quest when one completes.</summary>
    bool ContinueToNextQuest { get; }
    /// <summary>Stop rolling on once the character reaches this level; 0 = never.</summary>
    int StopAtLevel { get; }
}

public sealed class QuestController
{
    private static readonly TimeSpan AdvanceGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NoBlockMax = TimeSpan.FromMinutes(5);
    private const int MaxReplays = 3;

    private readonly IQuestStateReader _quests;
    private readonly PathStore _paths;
    private readonly StepExecutor _executor;
    private readonly IStepWorld _world;
    private readonly IConditionWorld _conditions;
    private readonly IRunPolicy _policy;
    private readonly Func<ushort, ushort?> _nextQuest;
    private readonly Action<string> _log;

    private ushort _questId;
    private DateTime _runStarted;
    private int _questsThisRun;
    private QuestPath? _path;
    private int _currentSequence = -1;
    private QuestSequence? _block;
    private int _stepIndex;
    /// <summary>Index the executor was last given, so a replay of the same step object is a fresh Begin.</summary>
    private int _activeStepIndex = -1;
    private DateTime? _waitingSince;
    private int _replays;
    private QuestSnapshot _lastSnapshot;

    /// <param name="nextQuest">Given a completed quest id, the next MSQ quest to run, or null when the story is blocked or over.</param>
    public QuestController(
        IQuestStateReader quests, PathStore paths, StepExecutor executor,
        IStepWorld world, IConditionWorld conditions, IRunPolicy policy,
        Func<ushort, ushort?> nextQuest, Action<string> log)
    {
        _quests = quests;
        _paths = paths;
        _executor = executor;
        _world = world;
        _conditions = conditions;
        _policy = policy;
        _nextQuest = nextQuest;
        _log = log;
    }

    /// <summary>Armed: finish the current quest, then stop instead of rolling into the next.</summary>
    public bool StopAfterQuest { get; set; }

    /// <summary>How long the current run has been going; zero when idle.</summary>
    public TimeSpan Elapsed => State is RunState.Idle or RunState.Faulted ? TimeSpan.Zero : _world.UtcNow - _runStarted;

    /// <summary>Quests completed since Start was pressed.</summary>
    public int QuestsThisRun => _questsThisRun;

    public RunState State { get; private set; } = RunState.Idle;
    public string StatusLine { get; private set; } = string.Empty;
    public ushort QuestId => _questId;
    public QuestPath? Path => _path;
    public int Sequence => _currentSequence;
    public int StepIndex => _stepIndex;
    public int StepCount => _block?.Steps.Count ?? 0;
    public string Phase => _executor.PhaseName;
    public QuestSnapshot LastSnapshot => _lastSnapshot;
    /// <summary>Set when the current block was entered by resume rather than from step 0 — the Wake's line in the UI.</summary>
    public string WakeNote { get; private set; } = string.Empty;

    public event Action<ushort>? QuestCompleted;

    private QuestStep? _singleStep;

    /// <summary>
    /// Run exactly one step, then stop. For checking a repaired step from the editor without
    /// committing to the quest — editing a position by hand is guesswork until something walks it.
    /// </summary>
    public bool StepOnce(QuestStep step)
    {
        if (State is not (RunState.Idle or RunState.Faulted))
            return false;
        _singleStep = step;
        _path = null;
        _block = null;
        _executor.Begin(step);
        State = RunState.Step;
        StatusLine = $"Single step: {step}";
        _log($"Step once: {step}");
        return true;
    }

    public bool Start(ushort questId)
    {
        var path = _paths.ForQuest(questId);
        if (path is null)
        {
            StatusLine = $"No path stored for quest {questId} — import first.";
            return false;
        }
        Stop();
        _runStarted = _world.UtcNow;
        _questsThisRun = 0;
        StopAfterQuest = false;
        return Begin(questId, path);
    }

    private bool Begin(ushort questId, QuestPath path)
    {
        _singleStep = null;
        _executor.Cancel();
        _questId = questId;
        _path = path;
        _currentSequence = -1;
        _replays = 0;
        WakeNote = string.Empty;
        State = RunState.Select;
        StatusLine = $"Starting {path.Name}";
        _log($"Start quest {questId} ({path.Name}), {path.Sequences.Count} sequences / {path.StepCount} steps.");
        return true;
    }

    public void Stop()
    {
        _singleStep = null;
        _executor.Cancel();
        _world.StopMoving();
        _world.ReleaseDialogue();
        if (State != RunState.Idle)
            _log("Stopped.");
        State = RunState.Idle;
        _block = null;
        _waitingSince = null;
        StatusLine = string.Empty;
    }

    /// <summary>Called every framework tick.</summary>
    public void Tick()
    {
        if (State is RunState.Idle or RunState.Faulted)
            return;

        try
        {
            if (_singleStep is not null)
            {
                TickSingleStep();
                return;
            }
            if (_path is null)
                return;
            TickInner();
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TickSingleStep()
    {
        var phase = _executor.PhaseName;
        State = phase.Contains("Duty") ? RunState.Handoff
            : _world.InCombat ? RunState.Combat
            : phase.StartsWith("Teleport") || phase.StartsWith("Aethernet") ? RunState.Travel
            : RunState.Step;
        StatusLine = $"Single step · {_singleStep} · {phase}";

        switch (_executor.Tick())
        {
            case StepStatus.Done:
                _log($"Step once done: {_singleStep}");
                var done = _singleStep;
                Stop();
                StatusLine = $"Step done: {done}";
                break;
            case StepStatus.Failed:
                var reason = _executor.FailReason;
                _singleStep = null;
                Fault($"single step failed: {reason}");
                break;
        }
    }

    private void TickInner()
    {
        if (_quests.IsComplete(_questId))
        {
            _log($"Quest {_questId} ({_path!.Name}) complete.");
            var id = _questId;
            _questsThisRun++;
            QuestCompleted?.Invoke(id);
            RollOn(id);
            return;
        }

        var snap = _quests.Read(_questId);
        _lastSnapshot = snap;
        var sequence = snap.IsAvailable ? snap.Sequence : (byte)0;

        if (sequence != _currentSequence)
            EnterSequence(sequence, snap);

        if (_block is null || _block.Steps.Count == 0)
        {
            // Nothing for us to do here; the game moves this one along (a duty, a cutscene, a
            // solo instance). Wait, but not forever.
            State = RunState.Advance;
            _waitingSince ??= _world.UtcNow;
            StatusLine = $"Sequence {sequence}: waiting for the game";
            if (_world.UtcNow - _waitingSince > NoBlockMax)
                Fault($"sequence {sequence} has no steps and the game did not advance in {NoBlockMax.TotalMinutes:F0} min");
            return;
        }

        if (_stepIndex >= _block.Steps.Count)
        {
            // Every step ran. The sequence should move; give it a moment, then replay the block.
            State = RunState.Advance;
            _waitingSince ??= _world.UtcNow;
            StatusLine = $"Sequence {sequence}: all {_block.Steps.Count} steps done, waiting for the game";
            if (_world.UtcNow - _waitingSince > AdvanceGrace)
            {
                if (_replays >= MaxReplays)
                {
                    Fault($"sequence {sequence} did not advance after {_replays} replays");
                    return;
                }
                _replays++;
                _executor.Cancel();
                _activeStepIndex = -1;
                _stepIndex = SelectResumeIndex(_block, snap);
                _waitingSince = null;
                _log($"Sequence {sequence} did not advance — replaying from step {_stepIndex} ({_replays}/{MaxReplays}).");
            }
            return;
        }

        var step = _block.Steps[_stepIndex];
        if (_activeStepIndex != _stepIndex || _executor.Status == StepStatus.Idle)
        {
            _activeStepIndex = _stepIndex;
            if (StepConditions.ShouldSkipStep(step, _conditions, snap))
            {
                _log($"Skip step {_stepIndex} ({step}) — condition holds.");
                _stepIndex++;
                return;
            }
            if (!step.IsReplaySafe && _replays > 0)
            {
                _log($"Not replaying step {_stepIndex} ({step}) — not replay-safe; skipping.");
                _stepIndex++;
                return;
            }
            if (step.Kind == StepKind.SinglePlayerDuty && !_policy.HandOffSoloDuties)
            {
                Fault("solo duty ahead and the BossMod handoff is off — run it yourself, then Retry");
                return;
            }
            if (step.Kind == StepKind.Duty && !_policy.HandOffDuties)
            {
                Fault("dungeon or trial ahead and the Theseus handoff is off — run it yourself, then Retry");
                return;
            }
            var skipTeleport = StepConditions.ShouldSkipAetheryte(step, _conditions, snap);
            _executor.Begin(step, skipTeleport);
            _log($"Step {_stepIndex + 1}/{_block.Steps.Count} in seq {sequence}: {step}" +
                 (step.AetheryteShortcut is { } a ? $" via {a}{(skipTeleport ? " (skipped)" : "")}" : ""));
        }

        var phase = _executor.PhaseName;
        State = phase.Contains("Duty") ? RunState.Handoff
            : _world.InCombat ? RunState.Combat
            : phase.StartsWith("Teleport") || phase.StartsWith("Aethernet") ? RunState.Travel
            : RunState.Step;
        StatusLine = $"Seq {sequence} · step {_stepIndex + 1}/{_block.Steps.Count} · {step.Kind} · {_executor.PhaseName}";

        switch (_executor.Tick())
        {
            case StepStatus.Done:
                _stepIndex++;
                _waitingSince = null;
                break;
            case StepStatus.Failed:
                Fault($"step {_stepIndex + 1} ({step}) failed: {_executor.FailReason}");
                break;
        }
    }

    /// <summary>After a quest completes: stop, or begin the next one the story allows.</summary>
    private void RollOn(ushort completed)
    {
        var elapsed = _world.UtcNow - _runStarted;
        var count = _questsThisRun;
        void StopWith(string line)
        {
            Stop();
            StatusLine = line;
        }

        if (StopAfterQuest)
        {
            StopWith($"Stopped after the quest, as armed — {count} done in {elapsed:h\\:mm}.");
            return;
        }
        if (!_policy.ContinueToNextQuest)
        {
            StopWith($"Quest complete — {count} done in {elapsed:h\\:mm}.");
            return;
        }
        if (_policy.StopAtLevel > 0 && _world.PlayerLevel >= _policy.StopAtLevel)
        {
            StopWith($"Reached level {_world.PlayerLevel} — stopping as configured ({count} quests, {elapsed:h\\:mm}).");
            return;
        }

        var next = _nextQuest(completed);
        if (next is null)
        {
            StopWith($"No next MSQ quest is available after {completed} — story blocked or finished ({count} quests, {elapsed:h\\:mm}).");
            return;
        }
        var path = _paths.ForQuest(next.Value);
        if (path is null)
        {
            StopWith($"Next quest {next} has no stored path — import, then Start.");
            return;
        }
        _log($"Rolling on to {next} ({path.Name}).");
        Begin(next.Value, path);
    }

    private void EnterSequence(int sequence, QuestSnapshot snap)
    {
        _executor.Cancel();
        _activeStepIndex = -1;
        _currentSequence = sequence;
        _block = _path!.Block((byte)sequence);
        _replays = 0;
        _waitingSince = null;
        _stepIndex = _block is null ? 0 : SelectResumeIndex(_block, snap);
        WakeNote = _stepIndex > 0 && _block is not null
            ? $"Resumed sequence {sequence} at step {_stepIndex + 1}/{_block.Steps.Count} from the quest's own variables"
            : string.Empty;
        _log($"Sequence {sequence}: {(_block is null ? "no block" : $"{_block.Steps.Count} steps")}, starting at step {_stepIndex}. {snap}");
    }

    /// <summary>
    /// Where in a block to (re)start, given the live variables. Steps that carry a completion
    /// mask are the landmarks: resume at the first one whose mask is not yet satisfied; if every
    /// tagged step is satisfied, resume just after the last of them; with no tags at all, replay
    /// from the top. Public and pure so it can be pinned by tests.
    /// </summary>
    public static int SelectResumeIndex(QuestSequence block, QuestSnapshot snap)
    {
        var firstUnsatisfied = -1;
        var lastSatisfied = -1;
        for (var i = 0; i < block.Steps.Count; i++)
        {
            var flags = block.Steps[i].CompletionQuestVariablesFlags;
            if (flags is null) continue;
            if (snap.Satisfies(flags))
                lastSatisfied = i;
            else if (firstUnsatisfied < 0)
                firstUnsatisfied = i;
        }
        if (firstUnsatisfied >= 0) return firstUnsatisfied;
        if (lastSatisfied >= 0) return Math.Min(lastSatisfied + 1, block.Steps.Count);
        return 0;
    }

    private void Fault(string reason)
    {
        _executor.Cancel();
        _world.StopMoving();
        _world.ReleaseDialogue();
        State = RunState.Faulted;
        StatusLine = reason;
        _log($"FAULT: {reason}");
    }
}
