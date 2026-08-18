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
    /// <summary>Ask before picking a quest up mid-way rather than just doing it.</summary>
    bool ConfirmBeforeResume { get; }
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
    private readonly Func<ushort, int> _questLevel;
    /// <summary>What a craft consumes — lets a purchase see whether anything downstream still needs it.</summary>
    private readonly PurchasePlan.IngredientsOf _ingredientsOf;
    private readonly IStepLog _stepLog;
    private readonly Action<string> _log;

    /// <summary>
    /// The priority list's answer to "anything to run before the story continues?" — null when
    /// none. Set by the plugin; consulted at every quest boundary, never mid-quest.
    /// </summary>
    public Func<ushort?>? PriorityNext { get; set; }

    /// <summary>
    /// The story's next quest regardless of what just finished — used after a priority quest,
    /// where "the quest after the one that completed" is meaningless. Set by the plugin.
    /// </summary>
    public Func<ushort?>? StoryCurrent { get; set; }

    /// <summary>The running quest came from the priority list, not the story.</summary>
    private bool _runningPriority;

    private ushort _questId;
    private DateTime _runStarted;
    private int _questsThisRun;
    private DateTime _stepStarted;
    private bool _awaitingResumeConfirm;
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
    /// <param name="questLevel">Level a quest requires (0 = unknown, no gate).</param>
    public QuestController(
        IQuestStateReader quests, PathStore paths, StepExecutor executor,
        IStepWorld world, IConditionWorld conditions, IRunPolicy policy,
        Func<ushort, ushort?> nextQuest, Func<ushort, int> questLevel, IStepLog stepLog, Action<string> log,
        PurchasePlan.IngredientsOf? ingredientsOf = null)
    {
        _ingredientsOf = ingredientsOf ?? (_ => []);
        _quests = quests;
        _paths = paths;
        _executor = executor;
        _world = world;
        _conditions = conditions;
        _policy = policy;
        _nextQuest = nextQuest;
        _questLevel = questLevel;
        _stepLog = stepLog;
        _log = log;
    }

    /// <summary>The run is paused on the Wake's question: pick up mid-quest, or not.</summary>
    public bool AwaitingResumeConfirm => _awaitingResumeConfirm;

    /// <summary>Answer the Wake's question with yes.</summary>
    public void ConfirmResume()
    {
        if (!_awaitingResumeConfirm) return;
        _awaitingResumeConfirm = false;
        StatusLine = "Resuming.";
        _log("Resume confirmed.");
    }

    /// <summary>Armed: finish the current quest, then stop instead of rolling into the next.</summary>
    public bool StopAfterQuest { get; set; }

    /// <summary>Armed: finish the current step, then stop — the "Step" button.</summary>
    public bool PauseAfterStep { get; set; }

    /// <summary>The step the run is on, or null.</summary>
    public QuestStep? CurrentStep => _block is not null && _stepIndex >= 0 && _stepIndex < _block.Steps.Count ? _block.Steps[_stepIndex] : null;

    /// <summary>The steps still ahead in the current block (the current one first).</summary>
    public IReadOnlyList<QuestStep> RemainingSteps
        => _block is null || _stepIndex >= _block.Steps.Count ? Array.Empty<QuestStep>() : _block.Steps.GetRange(_stepIndex, _block.Steps.Count - _stepIndex);

    /// <summary>Give up on the current step and move to the next — the "Skip" button. Logged as Skipped.</summary>
    public bool SkipStep()
    {
        if (_block is null || _path is null || State is RunState.Idle or RunState.Faulted && _block is null)
            return false;
        if (_stepIndex >= _block.Steps.Count)
            return false;
        var step = _block.Steps[_stepIndex];
        _executor.Cancel();
        _activeStepIndex = -1;
        LogStep(step, "Skipped", "skipped by user");
        _log($"Skipped step {_stepIndex + 1} ({step}) by request.");
        _stepIndex++;
        _waitingSince = null;
        if (State == RunState.Faulted)
            State = RunState.Step; // a skip is also how you get out of a fault
        StatusLine = $"Skipped step {_stepIndex}.";
        return true;
    }

    /// <summary>Run the current step again from the top — the "Stuck?" button. Also clears a fault on that step.</summary>
    public bool RetryStep()
    {
        if (_block is null || _path is null || _stepIndex >= _block.Steps.Count)
            return false;
        _executor.Cancel();
        _activeStepIndex = -1;
        _waitingSince = null;
        if (State == RunState.Faulted)
            State = RunState.Step;
        StatusLine = $"Retrying step {_stepIndex + 1}.";
        _log(StatusLine);
        return true;
    }

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

    /// <summary>
    /// A hand-in is about to happen. Raised as the <c>CompleteQuest</c> step begins, which is the
    /// last moment the bag is still free of whatever the quest is about to give us — the reward
    /// sweep counts here and again at <see cref="QuestCompleted"/>, and banks the difference.
    /// </summary>
    public event Action<ushort>? QuestCompleting;

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
        // Started by hand on a listed quest: treat it as the priority run it is.
        _runningPriority = PriorityNext?.Invoke() == questId;
        return Begin(questId, path);
    }

    private bool Begin(ushort questId, QuestPath path)
    {
        // The level gate: a quest the character cannot accept yet is a stop with a reason, not a
        // walk to an NPC who will not talk. (QuestFlow does the same.)
        var required = _questLevel(questId);
        var have = _world.PlayerLevel;
        if (required > 0 && have > 0 && required > have && !_quests.IsAccepted(questId))
        {
            Stop();
            StatusLine = $"{path.Name} needs level {required}; you are {have}. Level up, then Start.";
            _log(StatusLine);
            return false;
        }

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

        // The Wake's question: this quest is already under way. Ask, if asked to.
        var snap = _quests.Read(questId);
        _awaitingResumeConfirm = _policy.ConfirmBeforeResume && snap.IsAvailable && snap.Sequence > 0;
        if (_awaitingResumeConfirm)
        {
            State = RunState.Reconcile;
            WakeNote = $"{path.Name} is at sequence {snap.Sequence} — the game says so. Resume from there?";
            StatusLine = "Waiting for you: resume?";
        }
        return true;
    }

    public void Stop()
    {
        _singleStep = null;
        _awaitingResumeConfirm = false;
        PauseAfterStep = false;
        if (_executor.Status == StepStatus.Running && _executor.Current is { } running && _block is not null)
            LogStep(running, "Cancelled", null);
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
            if (_awaitingResumeConfirm)
                return; // parked on the Wake's question
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
                _stepStarted = _world.UtcNow;
                LogStep(step, "Skipped", "condition holds");
                _stepIndex++;
                return;
            }
            if (!PurchasePlan.IsWorthBuying(_block.Steps, _stepIndex, _conditions.ItemCount, _ingredientsOf))
            {
                _log($"Skip step {_stepIndex} ({step}) — nothing left in this sequence needs it.");
                _stepStarted = _world.UtcNow;
                LogStep(step, "Skipped", "the craft it feeds is already made");
                _stepIndex++;
                return;
            }
            if (snap.IsAvailable && !step.RequiredVariablesMet(snap.Variables.Span))
            {
                _log($"Skip step {_stepIndex} ({step}) — quest variables do not select it ({snap}).");
                _stepStarted = _world.UtcNow;
                LogStep(step, "Skipped", "not selected by quest variables");
                _stepIndex++;
                return;
            }
            if (!step.IsReplaySafe && _replays > 0)
            {
                _log($"Not replaying step {_stepIndex} ({step}) — not replay-safe; skipping.");
                _stepStarted = _world.UtcNow;
                LogStep(step, "Skipped", "not replay-safe");
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
            _stepStarted = _world.UtcNow;
            if (step.Kind == StepKind.CompleteQuest)
                QuestCompleting?.Invoke(_questId);
            _executor.Begin(step, skipTeleport, _questId);
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
                LogStep(step, "Done", null);
                _stepIndex++;
                _waitingSince = null;
                if (PauseAfterStep)
                {
                    PauseAfterStep = false;
                    _executor.Cancel();
                    _activeStepIndex = -1;
                    var line = $"Paused after step {_stepIndex} of sequence {sequence}. Start resumes from the game's state.";
                    Stop();
                    StatusLine = line;
                }
                break;
            case StepStatus.Failed:
                LogStep(step, "Failed", _executor.FailReason);
                Fault($"step {_stepIndex + 1} ({step}) failed: {_executor.FailReason}");
                break;
        }
    }

    private void LogStep(QuestStep step, string outcome, string? reason)
    {
        try
        {
            _stepLog.Record(new StepRecord
            {
                UtcStart = _stepStarted,
                Seconds = Math.Max(0, (_world.UtcNow - _stepStarted).TotalSeconds),
                QuestId = _questId,
                QuestName = _path?.Name ?? string.Empty,
                Sequence = _currentSequence,
                StepIndex = _stepIndex,
                Kind = step.KindName ?? step.Kind.ToString(),
                DataId = step.DataId,
                Outcome = outcome,
                Reason = reason,
                Phase = _executor.PhaseName,
            });
        }
        catch
        {
            // Telemetry must never fault a run.
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

        // Priority list first: a ready entry runs before the story continues.
        var wasPriority = _runningPriority;
        _runningPriority = false;
        if (PriorityNext?.Invoke() is { } priority && priority != completed && _paths.ForQuest(priority) is { } priorityPath)
        {
            _log($"Rolling on to priority quest {priority} ({priorityPath.Name}).");
            _runningPriority = true;
            Begin(priority, priorityPath);
            return;
        }

        // After a story quest, the story continues from it; after a priority quest, from wherever
        // the story actually is.
        var next = wasPriority ? StoryCurrent?.Invoke() : _nextQuest(completed);
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
        Begin(next.Value, path); // a level gate inside leaves us stopped with its own reason
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
