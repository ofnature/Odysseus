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
    private enum Phase
    {
        None, Delay, Teleport, TeleportWait, Aethernet, AethernetWait, Mount, Move, WaitReady, Interact, Dialogue,
        CombatWait, Combat,
        /// <summary>Solo instance: interacted, waiting to be inside.</summary>
        SoloDutyEnter,
        /// <summary>Solo instance: inside, BossMod AI has it, waiting to be out.</summary>
        SoloDutyRun,
        /// <summary>Full duty: asked Theseus, waiting for it to take over.</summary>
        DutyEnter,
        /// <summary>Full duty: Theseus is running it, waiting for it to finish and for us to be outside.</summary>
        DutyRun,
        /// <summary>Emote / jump / item: fired, brief settle.</summary>
        ActionSettle,
        Finish,
    }

    /// <summary>
    /// Same zone, but this far from the target: the aetheryte is almost certainly closer than the
    /// walk. Below it we just walk even when the step names a shortcut.
    /// </summary>
    public const float TeleportWorthDistance = 250f;

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
    private static readonly TimeSpan TravelStart = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TravelMax = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DutyEnterMax = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SoloDutyMax = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DutyMax = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan ActionSettle = TimeSpan.FromSeconds(2);
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
    private bool _skipTeleport;
    /// <summary>The instance this step hands off has been run; arriving again means finish, not re-enter.</summary>
    private bool _handoffDone;
    private uint _teleportTarget;
    private uint _teleportTerritory;
    private bool _sawTravelBusy;

    public StepExecutor(IStepWorld world) => _world = world;

    public StepStatus Status { get; private set; } = StepStatus.Idle;
    public string FailReason { get; private set; } = string.Empty;
    public QuestStep? Current => _step;
    public string PhaseName => _phase.ToString();

    /// <param name="skipTeleport">The step's <c>AetheryteShortcutIf</c> holds — walk instead of teleporting.</param>
    public void Begin(QuestStep step, bool skipTeleport = false)
    {
        _step = step;
        _stepStart = _world.UtcNow;
        _moveRetries = 0;
        _sawOccupied = false;
        _sawCombat = false;
        _skipTeleport = skipTeleport;
        _sawTravelBusy = false;
        _handoffDone = false;
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
        or StepKind.AttuneAetheryte or StepKind.AttuneAethernetShard or StepKind.AttuneAetherCurrent or StepKind.None
        or StepKind.SinglePlayerDuty or StepKind.Duty or StepKind.Emote or StepKind.Jump or StepKind.UseItem;

    /// <summary>The step hands the character to another plugin for a whole instance.</summary>
    public static bool IsHandoff(StepKind kind) => kind is StepKind.SinglePlayerDuty or StepKind.Duty;

    public StepStatus Tick()
    {
        if (_step is null || Status != StepStatus.Running)
            return Status;

        var now = _world.UtcNow;
        var step = _step;

        // Inside a handoff the other plugin owns deaths and retries; outside one, dead means stop.
        if (_world.IsDead && _phase is not (Phase.SoloDutyRun or Phase.DutyRun))
            return Fail("player is dead");

        switch (_phase)
        {
            case Phase.Delay:
                if (now - _phaseStart >= TimeSpan.FromSeconds(step.DelaySecondsAtStart ?? 0))
                    Enter(NextAfterDelay());
                break;

            case Phase.Teleport:
                if (!_world.IsReady || _world.InCombat)
                {
                    if (now - _phaseStart > ReadyWait) Fail("never became ready to teleport");
                    break;
                }
                if (!_world.Teleport(_teleportTarget))
                {
                    Fail($"teleport to {step.AetheryteShortcut} (aetheryte {_teleportTarget}) was refused — Lifestream loaded and aetheryte attuned?");
                    break;
                }
                Enter(Phase.TeleportWait);
                break;

            case Phase.TeleportWait:
                TickTravelWait(now, arrived: _world.TerritoryId == _teleportTerritory && !_world.IsTravelBusy && _world.IsReady,
                    what: $"teleport to {step.AetheryteShortcut}", next: NextAfterTeleport);
                break;

            case Phase.Aethernet:
                if (!_world.IsReady || _world.IsTravelBusy)
                {
                    if (now - _phaseStart > ReadyWait) Fail("never became ready for the aethernet");
                    break;
                }
                if (!_world.AethernetTeleport(step.AethernetShortcut![1]))
                {
                    Fail($"aethernet to {step.AethernetShortcut[1]} was refused — Lifestream loaded?");
                    break;
                }
                Enter(Phase.AethernetWait);
                break;

            case Phase.AethernetWait:
                TickTravelWait(now, arrived: !_world.IsTravelBusy && _world.IsReady,
                    what: $"aethernet to {step.AethernetShortcut![1]}", next: NextAfterTravel);
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

            case Phase.SoloDutyEnter:
                // The interact + "commence" prompt has been answered; the instance loads.
                if (_world.InDuty)
                {
                    _world.SetBossModAi(true);
                    Enter(Phase.SoloDutyRun);
                }
                else if (now - _phaseStart > DutyEnterMax)
                    Fail("solo duty did not start after the interaction");
                break;

            case Phase.SoloDutyRun:
                if (!_world.InDuty && !_world.IsTravelBusy)
                {
                    _world.SetBossModAi(false);
                    _handoffDone = true;
                    Enter(Phase.WaitReady); // back outside; the wrap-up cutscene may still be playing
                }
                else if (now - _phaseStart > SoloDutyMax)
                {
                    _world.SetBossModAi(false);
                    Fail($"solo duty did not finish in {SoloDutyMax.TotalMinutes:F0} min");
                }
                break;

            case Phase.DutyEnter:
                if (_world.TheseusBusy || _world.InDuty)
                    Enter(Phase.DutyRun);
                else if (now - _phaseStart > DutyEnterMax)
                    Fail("Theseus accepted the duty but never started it");
                break;

            case Phase.DutyRun:
                if (!_world.TheseusBusy && !_world.InDuty && !_world.IsTravelBusy)
                {
                    _handoffDone = true;
                    Enter(Phase.WaitReady);
                }
                else if (now - _phaseStart > DutyMax)
                    Fail($"duty did not finish in {DutyMax.TotalMinutes:F0} min");
                break;

            case Phase.ActionSettle:
                if (step.Kind == StepKind.UseItem && _world.IsOccupied)
                {
                    // An item that opens a dialogue behaves like an interact from here.
                    _sawOccupied = true;
                    Enter(Phase.Dialogue);
                }
                else if (now - _phaseStart > ActionSettle)
                    Enter(step.Kind == StepKind.UseItem && step.EnemySpawnType == EnemySpawnType.AfterItemUse
                        ? Phase.CombatWait
                        : Phase.Finish);
                break;

            case Phase.Finish:
                _world.ReleaseDialogue();
                Status = StepStatus.Done;
                break;
        }

        return Status;
    }

    // ── phases ──

    /// <summary>
    /// Travel decision. Teleport when the step names an aetheryte and either we are in the wrong
    /// zone or the target is a long way off in this one; then the aethernet hop if named; then
    /// walk. A step in another zone with no shortcut is a clear failure, not a doomed pathfind.
    /// </summary>
    private Phase NextAfterDelay()
    {
        var step = _step!;

        if (step.AetheryteShortcut is { } aetheryteName && !_skipTeleport)
        {
            var id = _world.ResolveAetheryte(aetheryteName);
            if (id is null)
            {
                Fail($"unknown aetheryte \"{aetheryteName}\" in the path data");
                return Phase.None;
            }
            var territory = _world.AetheryteTerritory(id.Value) ?? 0;
            var farAway = step.Position is { } p && Vector3.Distance(_world.PlayerPosition, p) > TeleportWorthDistance;
            if (_world.TerritoryId != territory || farAway)
            {
                _teleportTarget = id.Value;
                _teleportTerritory = territory;
                return Phase.Teleport;
            }
        }

        return NextAfterTeleport();
    }

    private Phase NextAfterTeleport()
    {
        var step = _step!;
        if (step.AethernetShortcut is { Length: 2 })
            return Phase.Aethernet;
        return NextAfterTravel();
    }

    private Phase NextAfterTravel()
    {
        var step = _step!;

        if (step.TerritoryId != 0 && _world.TerritoryId != step.TerritoryId)
        {
            Fail($"step is in territory {step.TerritoryId} but you are in {_world.TerritoryId} and the path gives no way there");
            return Phase.None;
        }

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

    private void TickTravelWait(DateTime now, bool arrived, string what, Func<Phase> next)
    {
        if (_world.IsTravelBusy)
            _sawTravelBusy = true;

        if (arrived && (_sawTravelBusy || now - _phaseStart > TravelStart))
        {
            Enter(next());
            return;
        }

        if (!_sawTravelBusy && now - _phaseStart > TravelStart && !arrived)
        {
            Fail($"{what} never started");
            return;
        }
        if (now - _phaseStart > TravelMax)
            Fail($"{what} did not finish in {TravelMax.TotalSeconds:F0}s");
    }

    private Phase NextAfterArrival(QuestStep step)
    {
        switch (step.Kind)
        {
            case StepKind.WalkTo or StepKind.None:
                return Phase.Finish;

            case StepKind.Combat:
                return step.EnemySpawnType == EnemySpawnType.AfterInteraction && step.DataId is not null
                    ? Phase.Interact
                    : Phase.CombatWait;

            case StepKind.SinglePlayerDuty:
                if (_handoffDone)
                    return Phase.Finish;
                // Talk to the NPC; TextAdvance answers "commence"; the instance loads.
                if (_world.InDuty)
                {
                    _world.SetBossModAi(true);
                    return Phase.SoloDutyRun; // already inside (resumed mid-instance)
                }
                return step.DataId is not null ? Phase.Interact : Phase.SoloDutyEnter;

            case StepKind.Duty:
                if (_handoffDone)
                    return Phase.Finish;
                if (_world.InDuty || _world.TheseusBusy)
                    return Phase.DutyRun; // resumed while Theseus is mid-run
                if (step.ContentFinderConditionId is not { } cfc)
                {
                    Fail("duty step names no ContentFinderCondition");
                    return Phase.None;
                }
                // Theseus runs 4-player dungeons. Anything else — the nine 8-player trials in the
                // HW+SB MSQ, for instance — is a stop, named, before anyone is asked to try.
                if (_world.DescribeDuty(cfc) is { IsDungeon: false } notDungeon)
                {
                    Fail($"{notDungeon.Name} is an {notDungeon.Kind} — Odysseus does not automate those. " +
                         "Clear it with Duty Support or a party, then Retry");
                    return Phase.None;
                }
                if (!_world.TheseusCanEnterDuty)
                {
                    Fail("Theseus is not loaded, is disabled, or is busy — run the duty yourself, then Retry");
                    return Phase.None;
                }
                if (!_world.TheseusEnterDuty(cfc))
                {
                    Fail($"Theseus refused duty {cfc} — it may have no route for it. Run it yourself, then Retry");
                    return Phase.None;
                }
                return Phase.DutyEnter;

            case StepKind.Emote:
                if (step.DataId is { } emoteTarget)
                    _world.TryTargetDataId(emoteTarget);
                _world.SendChatCommand($"/{step.Emote}");
                return Phase.ActionSettle;

            case StepKind.Jump:
                _world.SendChatCommand("/generalaction Jump");
                return Phase.ActionSettle;

            case StepKind.UseItem:
                if (step.ItemId is not { } itemId)
                {
                    Fail("UseItem step names no item");
                    return Phase.None;
                }
                if (step.DataId is { } itemTarget && !_world.TryTargetDataId(itemTarget))
                {
                    Fail($"item target {itemTarget} is not here");
                    return Phase.None;
                }
                _world.HoldDialogue();
                if (!_world.UseItem(itemId))
                {
                    Fail($"could not use item {itemId}");
                    return Phase.None;
                }
                return Phase.ActionSettle;

            default:
                return Phase.Interact;
        }
    }

    private void TickMove(QuestStep step, DateTime now)
    {
        var target = step.Position!.Value;
        var tolerance = StopDistanceFor(step);
        var distance = Vector3.Distance(_world.PlayerPosition, target);

        // A walk across a zone line arrives by changing zone, not by reaching the point.
        if (step.TargetTerritoryId is { } targetTerritory && _world.TerritoryId == targetTerritory)
        {
            _world.StopMoving();
            Enter(Phase.WaitReady);
            return;
        }

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

        switch (step.Kind)
        {
            case StepKind.Combat:
                Enter(Phase.CombatWait);
                return;
            case StepKind.SinglePlayerDuty:
                // The dialogue that "ends" here is the commence prompt; the instance is loading.
                Enter(_world.InDuty ? Phase.SoloDutyRun : Phase.SoloDutyEnter);
                if (_world.InDuty) _world.SetBossModAi(true);
                return;
            case StepKind.UseItem when step.EnemySpawnType == EnemySpawnType.AfterItemUse:
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
        if (phase == Phase.None)
            return; // a Next* helper already failed the step
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
