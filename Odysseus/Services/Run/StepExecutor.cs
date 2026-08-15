using System;
using System.Numerics;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Run;

public enum StepStatus
{
    /// <summary>Nothing begun.</summary>
    Idle,
    Running,
    /// <summary>The step's action has been performed. Whether the game moved on is the controller's question.</summary>
    Done,
    /// <summary>The step cannot be performed; <see cref="StepExecutor.FailReason"/> says why.</summary>
    Failed,
}

/// <summary>
/// Runs one <see cref="QuestStep"/> as a small state machine ticked every frame.
///
/// <para>
/// A step is <i>done</i> when its action has been carried out — arrived, interacted, fought — not
/// when the quest advanced. Advancement is server state and the controller reads it; keeping the
/// two apart is what lets a step be replayed harmlessly and lets the controller decide what
/// "nothing happened" means. Every phase has a watchdog, so a step can stall for a bounded time
/// and then <see cref="StepStatus.Failed"/> with a reason, never spin silently.
/// </para>
/// </summary>
public sealed class StepExecutor
{
    private enum Phase { None, Delay, Mount, Move, WaitReady, Interact, Dialogue, CombatWait, Combat, Finish }

    /// <summary>How close "arrived" is when the step does not say. Interact range is ~7y; 3 keeps us clearly inside it.</summary>
    public const float DefaultStopDistance = 3f;
    /// <summary>WalkTo without a StopDistance: land on the point.</summary>
    public const float WalkToStopDistance = 0.5f;
    /// <summary>Distances past this are worth a mount.</summary>
    public const float MountWorthDistance = 30f;
    /// <summary>Overworld enemies farther than this are not "ours".</summary>
    public const float CombatSearchRadius = 30f;
    /// <summary>
    /// vnavmesh declares arrival by its own tolerance and can stop a hair outside ours; without
    /// slack the executor would re-path three times over half a yalm and then fail the step.
    /// </summary>
    public const float ArrivalSlack = 1.5f;

    private static readonly TimeSpan MoveStall = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MoveTotal = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan MountWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DialogueSettle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DialogueMax = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CombatSpawnWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CombatClearSettle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CombatMax = TimeSpan.FromMinutes(5);
    private const int MaxMoveRetries = 3;

    private readonly IStepWorld _world;

    private QuestStep? _step;
    private Phase _phase = Phase.None;
    private DateTime _phaseStart;
    private DateTime _stepStart;
    private DateTime _lastMoveIssue;
    private int _moveRetries;
    private bool _sawOccupied;
    private bool _sawCombat;
    private DateTime _lastCombatSeen;

    public StepExecutor(IStepWorld world) => _world = world;

    public StepStatus Status { get; private set; } = StepStatus.Idle;
    public string FailReason { get; private set; } = string.Empty;
    public QuestStep? Current => _step;
    public string PhaseName => _phase.ToString();

    public void Begin(QuestStep step)
    {
        _step = step;
        _stepStart = _world.UtcNow;
        _moveRetries = 0;
        _sawOccupied = false;
        _sawCombat = false;
        FailReason = string.Empty;
        Status = StepStatus.Running;

        if (!IsSupported(step.Kind))
        {
            Fail($"step kind {step.KindName ?? step.Kind.ToString()} is not implemented yet");
            return;
        }

        Enter(step.DelaySecondsAtStart is > 0 ? Phase.Delay : NextAfterDelay());
    }

    public void Cancel()
    {
        if (Status == StepStatus.Running)
            _world.StopMoving();
        _world.ReleaseDialogue();
        _step = null;
        _phase = Phase.None;
        Status = StepStatus.Idle;
    }

    /// <summary>Kinds the executor can carry out today. Anything else fails at Begin with a clear reason.</summary>
    public static bool IsSupported(StepKind kind) => kind is
        StepKind.WalkTo or StepKind.Interact or StepKind.AcceptQuest or StepKind.CompleteQuest or StepKind.Combat
        or StepKind.AttuneAetheryte or StepKind.AttuneAethernetShard or StepKind.AttuneAetherCurrent or StepKind.None;

    public StepStatus Tick()
    {
        if (_step is null || Status != StepStatus.Running)
            return Status;

        var now = _world.UtcNow;
        var step = _step;

        if (_world.IsDead)
            return Fail("player is dead");

        switch (_phase)
        {
            case Phase.Delay:
                if (now - _phaseStart >= TimeSpan.FromSeconds(step.DelaySecondsAtStart ?? 0))
                    Enter(NextAfterDelay());
                break;

            case Phase.Mount:
                if (_world.IsMounted || now - _phaseStart > MountWait)
                    Enter(Phase.Move);
                break;

            case Phase.Move:
                TickMove(step, now);
                break;

            case Phase.WaitReady:
                if (_world.IsReady && !_world.IsOccupied)
                    Enter(NextAfterArrival(step));
                else if (now - _phaseStart > ReadyWait)
                    Fail("player never became ready");
                break;

            case Phase.Interact:
                TickInteract(step, now);
                break;

            case Phase.Dialogue:
                TickDialogue(step, now);
                break;

            case Phase.CombatWait:
            case Phase.Combat:
                TickCombat(step, now);
                break;

            case Phase.Finish:
                _world.ReleaseDialogue();
                Status = StepStatus.Done;
                break;
        }

        return Status;
    }

    // ── phases ──

    private Phase NextAfterDelay()
    {
        var step = _step!;
        if (step.Position is not { } target)
            return Phase.WaitReady;

        var distance = Vector3.Distance(_world.PlayerPosition, target);
        if (distance <= StopDistanceFor(step) + ArrivalSlack)
            return Phase.WaitReady;

        // Mount for long legs unless the step forbids it; the executor never dismounts.
        var wantMount = step.Mount == true || (step.Mount != false && distance > MountWorthDistance);
        if (wantMount && !_world.IsMounted && !_world.InCombat)
        {
            _world.Mount();
            return Phase.Mount;
        }
        return Phase.Move;
    }

    private static Phase NextAfterArrival(QuestStep step) => step.Kind switch
    {
        StepKind.WalkTo or StepKind.None => Phase.Finish,
        StepKind.Combat => step.EnemySpawnType == EnemySpawnType.AfterInteraction && step.DataId is not null
            ? Phase.Interact
            : Phase.CombatWait,
        _ => Phase.Interact,
    };

    private void TickMove(QuestStep step, DateTime now)
    {
        var target = step.Position!.Value;
        var tolerance = StopDistanceFor(step);
        var distance = Vector3.Distance(_world.PlayerPosition, target);

        if (distance <= tolerance + ArrivalSlack)
        {
            _world.StopMoving();
            Enter(Phase.WaitReady);
            return;
        }

        if (now - _stepStart > MoveTotal)
        {
            Fail($"did not reach {Fmt(target)} in {MoveTotal.TotalSeconds:F0}s ({distance:F1}y left)");
            return;
        }

        if (!_world.NavmeshReady)
        {
            if (now - _phaseStart > MoveStall)
                Fail("navmesh not ready");
            return;
        }

        if (_world.IsMoving)
        {
            _lastMoveIssue = now;
            return;
        }

        // Not moving and not there. Either we have not asked yet, or the path ended short.
        if (_lastMoveIssue != default && now - _lastMoveIssue < TimeSpan.FromSeconds(1))
            return; // give the pathfinder a beat before judging it

        if (_lastMoveIssue != default && _world.PathWaypointCount == 0)
        {
            Fail($"no path to {Fmt(target)}");
            return;
        }

        if (_moveRetries >= MaxMoveRetries)
        {
            Fail($"stalled {_moveRetries} times short of {Fmt(target)} ({distance:F1}y left)");
            return;
        }

        var fly = step.Fly && _world.CanFlyHere;
        var ok = tolerance > WalkToStopDistance
            ? _world.MoveCloseTo(target, tolerance, fly)
            : _world.MoveTo(target, fly);
        _lastMoveIssue = now;
        _moveRetries++;
        if (!ok)
            _world.Log($"move to {Fmt(target)} refused (attempt {_moveRetries})");
    }

    private void TickInteract(QuestStep step, DateTime now)
    {
        if (step.DataId is not { } dataId)
        {
            // Nothing to interact with — a bare position step of an interact kind. Treat as arrival.
            Enter(Phase.Finish);
            return;
        }

        if (!_world.IsReady || _world.IsOccupied)
        {
            if (now - _phaseStart > ReadyWait)
                Fail("player never became ready to interact");
            return;
        }

        if (!_world.IsDataIdSpawned(dataId))
        {
            if (now - _phaseStart > ReadyWait)
                Fail($"object {dataId} never appeared");
            return;
        }

        _world.HoldDialogue();
        if (!_world.TryInteractWithDataId(dataId))
        {
            if (now - _phaseStart > ReadyWait)
                Fail($"could not interact with {dataId}");
            return;
        }

        Enter(Phase.Dialogue);
    }

    private void TickDialogue(QuestStep step, DateTime now)
    {
        var occupied = _world.IsOccupied;
        if (occupied)
        {
            _sawOccupied = true;
            AnswerDialogue(step);
        }

        if (now - _phaseStart > DialogueMax)
        {
            Fail("dialogue never ended");
            return;
        }

        // Interaction over: we were in a dialogue and now are not, or nothing ever opened and
        // enough time has passed that it clearly is not going to.
        var settled = _sawOccupied ? !occupied : now - _phaseStart > DialogueSettle;
        if (!settled)
            return;

        if (step.Kind == StepKind.Combat)
        {
            Enter(Phase.CombatWait);
            return;
        }
        Enter(Phase.Finish);
    }

    private void AnswerDialogue(QuestStep step)
    {
        if (step.DialogueChoices is null)
            return;
        foreach (var choice in step.DialogueChoices)
        {
            if (choice.Type.Equals("YesNo", StringComparison.OrdinalIgnoreCase) && _world.IsAddonVisible("SelectYesno"))
                _world.SelectYesNo(choice.Yes ?? true);
            // List choices need the prompt/answer text keys resolved against the quest's dialogue
            // sheet to pick an index; that resolver is P3. Until then TextAdvance's own handling applies.
        }
    }

    private void TickCombat(QuestStep step, DateTime now)
    {
        var enemies = (System.Collections.Generic.IReadOnlyCollection<uint>?)step.KillEnemyDataIds ?? Array.Empty<uint>();

        if (_world.InCombat)
        {
            _sawCombat = true;
            _lastCombatSeen = now;
            _phase = Phase.Combat;
            return; // Daedalus is fighting; our only job is to not walk away.
        }

        if (now - _stepStart > CombatMax)
        {
            Fail("combat did not resolve in time");
            return;
        }

        // Out of combat. Anything left to pull?
        if (_world.AttackNearestEnemy(enemies, CombatSearchRadius))
        {
            _phase = Phase.Combat;
            return;
        }

        if (_sawCombat)
        {
            // Fought and it is quiet now — give stragglers a moment to spawn, then call it.
            if (now - _lastCombatSeen > CombatClearSettle)
                Enter(Phase.Finish);
            return;
        }

        // Never fought. Enemies that spawn on arrival can take a few seconds; enemies that were
        // meant to be found may simply not be here (already dead, or the flags are already set).
        if (now - _phaseStart > CombatSpawnWait)
            Enter(Phase.Finish);
    }

    // ── helpers ──

    private void Enter(Phase phase)
    {
        _phase = phase;
        _phaseStart = _world.UtcNow;
        if (phase == Phase.Move)
        {
            _lastMoveIssue = default;
            _moveRetries = 0;
        }
    }

    private StepStatus Fail(string reason)
    {
        _world.StopMoving();
        _world.ReleaseDialogue();
        FailReason = reason;
        Status = StepStatus.Failed;
        return Status;
    }

    private static float StopDistanceFor(QuestStep step)
        => step.StopDistance ?? (step.Kind == StepKind.WalkTo ? WalkToStopDistance : DefaultStopDistance);

    private static string Fmt(Vector3 v) => $"({v.X:F0},{v.Y:F0},{v.Z:F0})";
}
