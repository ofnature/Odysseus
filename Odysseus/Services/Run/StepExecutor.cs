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
        /// <summary>Vendor interacted with, waiting for the shop window.</summary>
        Shop,
        /// <summary>Shop open: buy the shortfall and watch the bag until it is covered.</summary>
        ShopBuy,
        /// <summary>Gearset equipped, waiting for the class to actually change.</summary>
        ClassSwitch,
        /// <summary>Artisan has the craft; watch the bag until it is covered.</summary>
        Craft,
        /// <summary>GatherBuddy is switched on; watch the bag until it is covered.</summary>
        Gather,
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
    /// <summary>How long the reward window may sit before we press Complete ourselves — TextAdvance gets first go.</summary>
    private static readonly TimeSpan RewardWindowGrace = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan RewardCompleteRetry = TimeSpan.FromSeconds(1.5);
    /// <summary>Same courtesy for the hand-over window: TextAdvance fills and presses it first if it is holding.</summary>
    private static readonly TimeSpan HandOverGrace = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan HandOverRetry = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan CombatSpawnWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CombatClearSettle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CombatMax = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TravelStart = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TravelMax = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DutyEnterMax = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SoloDutyMax = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DutyMax = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan ActionSettle = TimeSpan.FromSeconds(2);
    /// <summary>A purchase is a server round trip; leave a beat between rounds rather than spamming the handler.</summary>
    private static readonly TimeSpan ShopGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShopMax = TimeSpan.FromSeconds(30);
    /// <summary>How long a handoff may sit having done nothing before we call it stuck. Progress resets it.</summary>
    private static readonly TimeSpan MakeIdle = TimeSpan.FromSeconds(60);
    private const int MaxMoveRetries = 3;

    private readonly IStepWorld _world;
    private readonly Quest.IDialogueTexts? _texts;

    private QuestStep? _step;
    private ushort _questId;
    private bool _listAnswered;
    private DateTime _rewardWindowSince;
    private DateTime _rewardLastTry;
    private bool _rewardNeedsChoiceLogged;
    private DateTime _handOverSince;
    private DateTime _handOverLastTry;
    /// <summary>The shop the current PurchaseItem step buys from; learned from the window when the step names none.</summary>
    private uint _shopId;
    /// <summary>How many of the step's item the bag should hold when the purchase is done.</summary>
    private int _buyTarget;
    private DateTime _lastBuy;
    /// <summary>The ClassJob a SwitchClass step is waiting to land on.</summary>
    private uint _switchTarget;
    /// <summary>The handoff has been asked; a second ask would queue another batch on top.</summary>
    private bool _makeAsked;
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

    /// <param name="texts">Resolves dialogue text keys for the current quest; null means List choices and Say cannot be answered.</param>
    public StepExecutor(IStepWorld world, Quest.IDialogueTexts? texts = null)
    {
        _world = world;
        _texts = texts;
    }

    public StepStatus Status { get; private set; } = StepStatus.Idle;
    public string FailReason { get; private set; } = string.Empty;
    public QuestStep? Current => _step;
    public string PhaseName => _phase.ToString();

    /// <param name="skipTeleport">The step's <c>AetheryteShortcutIf</c> holds — walk instead of teleporting.</param>
    /// <param name="questId">The quest this step belongs to — needed to resolve dialogue text keys. 0 for a bare step.</param>
    public void Begin(QuestStep step, bool skipTeleport = false, ushort questId = 0)
    {
        _step = step;
        _questId = questId;
        _listAnswered = false;
        _rewardWindowSince = default;
        _rewardNeedsChoiceLogged = false;
        _handOverSince = default;
        _shopId = 0;
        _buyTarget = 0;
        _lastBuy = default;
        _switchTarget = 0;
        _makeAsked = false;
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
            Fail(WhyUnsupported(step));
            return;
        }

        Enter(step.DelaySecondsAtStart is > 0 ? Phase.Delay : NextAfterDelay());
    }

    public void Cancel()
    {
        if (Status == StepStatus.Running)
            _world.StopMoving();
        // A handoff we switched on outlives the step unless it is switched back off.
        if (_makeAsked && _step is { } running)
        {
            if (running.Kind == StepKind.Craft) _world.StopCrafting();
            if (running.Kind == StepKind.Gather) _world.StopGathering();
        }
        _world.ReleaseDialogue();
        _step = null;
        _phase = Phase.None;
        Status = StepStatus.Idle;
    }

    /// <summary>Kinds the executor can carry out today. Anything else fails at Begin with a clear reason.</summary>
    public static bool IsSupported(StepKind kind) => kind is
        StepKind.WalkTo or StepKind.Interact or StepKind.AcceptQuest or StepKind.CompleteQuest or StepKind.Combat
        or StepKind.AttuneAetheryte or StepKind.AttuneAethernetShard or StepKind.AttuneAetherCurrent or StepKind.None
        or StepKind.SinglePlayerDuty or StepKind.Duty or StepKind.Emote or StepKind.Jump or StepKind.UseItem or StepKind.Say
        or StepKind.EquipRecommended or StepKind.Action or StepKind.Instruction or StepKind.StatusOff
        or StepKind.PurchaseItem or StepKind.SwitchClass or StepKind.Craft or StepKind.Gather;

    /// <summary>The step hands the character to another plugin for a whole instance.</summary>
    public static bool IsHandoff(StepKind kind) => kind is StepKind.SinglePlayerDuty or StepKind.Duty;

    /// <summary>
    /// Why a step cannot run — and the two reasons are not the same reason.
    ///
    /// <para>
    /// An <see cref="StepKind.Unknown"/> step whose kept name parses to something we <i>do</i>
    /// support was converted before we supported it: the path is stale, not the feature missing.
    /// Saying "Craft is not implemented yet" there sends you looking for a feature that is already
    /// there, when the fix is a re-import.
    /// </para>
    /// </summary>
    public static string WhyUnsupported(QuestStep step)
    {
        if (step.Kind == StepKind.Unknown
            && step.KindName is { Length: > 0 } named
            && Enum.TryParse<StepKind>(named, ignoreCase: false, out var parsed)
            && IsSupported(parsed))
            return $"this path was converted before {named} steps were supported — " +
                   "re-import your paths from Settings, then Retry";
        return $"step kind {step.KindName ?? step.Kind.ToString()} is not implemented yet";
    }

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
                if (step.Kind == StepKind.EquipRecommended)
                {
                    // The module computes asynchronously; equip once it has, then let the swap settle.
                    if (_world.RecommendedGearReady && now - _phaseStart > TimeSpan.FromSeconds(0.5))
                    {
                        _world.EquipRecommendedGear();
                        Enter(Phase.Finish);
                    }
                    else if (now - _phaseStart > ReadyWait)
                        Fail("recommended gear never finished computing");
                    break;
                }
                if (step.Kind == StepKind.Action)
                {
                    // Cast/animation, then a moment for the game to register it.
                    if (_world.IsCasting)
                        break;
                    if (now - _phaseStart > TimeSpan.FromSeconds(3))
                        Enter(Phase.Finish);
                    break;
                }
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

            case Phase.Shop:
                if (_world.IsShopOpen(_shopId))
                {
                    // A step that named no shop still has to buy through one; the window says which.
                    if (_shopId == 0)
                        _shopId = _world.OpenShopId;
                    if (_shopId == 0)
                        Fail("the vendor window opened but does not say which shop it is");
                    else
                        Enter(Phase.ShopBuy);
                }
                else if (now - _phaseStart > ReadyWait)
                    Fail($"the shop at {step.DataId} never opened");
                break;

            case Phase.ShopBuy:
                TickPurchase(step, now);
                break;

            case Phase.ClassSwitch:
                if (_world.CurrentClassJob == _switchTarget)
                    Enter(Phase.Finish);
                else if (now - _phaseStart > ReadyWait)
                    Fail($"the class did not change to {step.TargetClass} in {ReadyWait.TotalSeconds:F0}s");
                break;

            case Phase.Craft:
                TickCraft(step, now);
                break;

            case Phase.Gather:
                TickGather(step, now);
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

            // A note in the path, and "drop status X" (used for a disguise/transparency the quest
            // gave you — the game clears it on the next relevant interaction). Nothing to do.
            case StepKind.Instruction or StepKind.StatusOff:
                if (step.Comment is { } note && step.Kind == StepKind.Instruction)
                    _world.Log($"Path note: {note}");
                return Phase.Finish;

            case StepKind.Action:
            {
                if (step.ActionName is not { } actionName || _world.ResolveAction(actionName) is not { } actionId)
                {
                    Fail($"action \"{step.ActionName ?? "?"}\" is not in the Action sheet");
                    return Phase.None;
                }
                if (!step.GroundTarget && step.DataId is { } actionTarget && !_world.TryTargetDataId(actionTarget))
                {
                    Fail($"action target {actionTarget} is not here");
                    return Phase.None;
                }
                if (!_world.UseAction(actionId, step.GroundTarget ? step.Position : null))
                {
                    Fail($"action \"{actionName}\" was refused");
                    return Phase.None;
                }
                return Phase.ActionSettle;
            }

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

            case StepKind.EquipRecommended:
                if (!_world.PrepareRecommendedGear())
                {
                    Fail("recommended gear could not be computed");
                    return Phase.None;
                }
                return Phase.ActionSettle;

            case StepKind.Say:
            {
                var text = step.ChatMessageKey is { } key ? _texts?.Resolve(_questId, key) : null;
                if (text is null)
                {
                    Fail($"Say step: text key {step.ChatMessageKey ?? "?"} could not be resolved for quest {_questId}");
                    return Phase.None;
                }
                if (step.DataId is { } sayTarget)
                    _world.TryTargetDataId(sayTarget);
                _world.SendChatCommand($"/say {text}");
                return Phase.ActionSettle;
            }

            case StepKind.PurchaseItem:
                return BeginPurchase(step);

            case StepKind.Craft:
                if (step.ItemId is null)
                {
                    Fail("Craft step names no item");
                    return Phase.None;
                }
                return Phase.Craft;

            case StepKind.Gather:
                if (step.GatherItems is not { Count: > 0 })
                {
                    Fail("Gather step names nothing to gather");
                    return Phase.None;
                }
                return Phase.Gather;

            case StepKind.SwitchClass:
                return BeginClassSwitch(step);

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

        // A step that disables the mesh must not wait on it, and must not be judged by it: the
        // waypoint count below belongs to a pathfind that never happens on this route.
        var direct = step.DisableNavmesh;

        if (!direct && !_world.NavmeshReady)
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

        if (!direct && _lastMoveIssue != default && _world.PathWaypointCount == 0)
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
        var ok = direct
            ? _world.MoveDirectTo(target, fly)
            : tolerance > WalkToStopDistance
                ? _world.MoveCloseTo(target, tolerance, fly)
                : _world.MoveTo(target, fly);
        _lastMoveIssue = now;
        _moveRetries++;
        if (!ok)
            _world.Log($"move{(direct ? " direct" : "")} to {Fmt(target)} refused (attempt {_moveRetries})");
    }

    /// <summary>
    /// Open the vendor's window. The count on the step is a <i>target total</i>, not an order —
    /// that is how the data's own "skip if already held" clause reads it — so a step replayed after
    /// a restart buys the shortfall and a step whose item is already in the bag buys nothing.
    /// </summary>
    private Phase BeginPurchase(QuestStep step)
    {
        if (step.ItemId is not { } item)
        {
            Fail("PurchaseItem step names no item");
            return Phase.None;
        }
        if (step.PurchaseShopSheet is { Length: > 0 } sheet
            && !sheet.Equals("GilShop", StringComparison.OrdinalIgnoreCase))
        {
            Fail($"PurchaseItem names a {sheet} shop — only gil shops are handled");
            return Phase.None;
        }

        _buyTarget = Math.Max(1, step.ItemCount ?? 1);
        if (_world.ItemCount(item) >= _buyTarget)
            return Phase.Finish;

        _shopId = step.PurchaseShopId ?? 0;
        if (_world.IsShopOpen(_shopId))
            return Phase.Shop; // already standing at an open window — that phase resolves the id

        if (step.DataId is not { } vendor)
        {
            Fail("PurchaseItem step names no vendor");
            return Phase.None;
        }
        if (!_world.OpenShop(vendor, _shopId))
        {
            Fail($"could not open the shop at {vendor}");
            return Phase.None;
        }
        return Phase.Shop;
    }

    /// <summary>
    /// Buy the shortfall, re-read the bag, buy again if it is still short. Re-planning each round
    /// off the live count rather than trusting one order is what makes a partly-filled purchase
    /// converge instead of double-buying — the same shape the delivery runner uses for ingredients.
    /// </summary>
    private void TickPurchase(QuestStep step, DateTime now)
    {
        var item = step.ItemId!.Value;
        var held = _world.ItemCount(item);

        if (held >= _buyTarget)
        {
            _world.CloseShop();
            Enter(Phase.Finish);
            return;
        }

        if (!_world.IsShopOpen(_shopId))
        {
            Fail("the shop window closed before the purchase finished");
            return;
        }
        if (_world.ShopBusy(_shopId))
            return;

        if (now - _phaseStart > ShopMax)
        {
            _world.CloseShop();
            var inChest = _world.FreeCompanyChestCount(item);
            Fail($"still {held} of {_buyTarget} × item {item} after {ShopMax.TotalSeconds:F0}s at the shop — " +
                 $"out of gil ({_world.Gil:N0}) or the shop is out of stock" +
                 (inChest > 0 ? $"; {inChest} are in the FC chest" : string.Empty));
            return;
        }

        if (_lastBuy != default && now - _lastBuy < ShopGap)
            return;
        _lastBuy = now;
        if (!_world.BuyFromShop(_shopId, item, _buyTarget - held))
        {
            _world.CloseShop();
            Fail($"shop {_shopId:X} does not stock item {item}");
        }
    }

    /// <summary>
    /// Hand the craft to Artisan and watch the bag. The count is a target total, as everywhere
    /// else, so a step replayed after a restart makes up the shortfall and one whose item is
    /// already in the bag makes nothing.
    ///
    /// <para>
    /// Artisan is asked exactly once. It stopping with the bag still short is the interesting
    /// case — it means the materials ran out, and the stop says which ones rather than leaving you
    /// to work it out from an empty crafting log.
    /// </para>
    /// </summary>
    private void TickCraft(QuestStep step, DateTime now)
    {
        var item = step.ItemId!.Value;
        var want = Math.Max(1, step.ItemCount ?? 1);
        var short_ = want - _world.ItemCount(item);

        if (short_ <= 0)
        {
            if (_makeAsked && _world.IsCrafting)
                _world.StopCrafting();
            Enter(Phase.Finish);
            return;
        }

        if (_world.IsCrafting)
        {
            _phaseStart = now; // it is working; the idle clock only runs while nothing happens
            return;
        }

        if (_makeAsked)
        {
            var missing = _world.CraftShortfall(item, short_);
            Fail($"Artisan stopped with {short_} × item {item} still to make" +
                 (missing.Length > 0 ? $" — short of {missing}" : "") + ". Stock up, then Retry");
            return;
        }

        if (!_world.CrafterReady)
        {
            Fail($"{short_} × item {item} needs crafting and Artisan is not loaded — " +
                 "make them yourself, then Retry");
            return;
        }

        if (_world.StartCraft(item, short_) is not { } job)
        {
            Fail($"no recipe for item {item}, or Artisan would not take the craft");
            return;
        }
        _makeAsked = true;
        _phaseStart = now;
        _world.Log($"Asked Artisan for {short_} × item {item} as {job}.");
    }

    /// <summary>
    /// Switch GatherBuddy on and watch our own bag, because it takes no request — it gathers from
    /// its own lists and cannot report progress. So the bag is the progress meter and "waiting" is
    /// the only failure signal it offers.
    ///
    /// <para>
    /// A quest <i>event</i> item is not something it can ever fetch: those exist only inside the
    /// quest, are in no sheet it reads and on no list you can add. Those stop immediately, named.
    /// </para>
    /// </summary>
    private void TickGather(QuestStep step, DateTime now)
    {
        var targets = step.GatherItems!;

        GatherTarget? outstanding = null;
        foreach (var t in targets)
            if (_world.ItemCount(t.ItemId) < t.ItemCount) { outstanding = t; break; }

        if (outstanding is null)
        {
            if (_makeAsked)
                _world.StopGathering();
            Enter(Phase.Finish);
            return;
        }

        if (outstanding.IsEventItem)
        {
            StopGathering();
            Fail($"item {outstanding.ItemId} is a quest-only gathering item — no plugin can fetch it. " +
                 "Gather it yourself, then Retry");
            return;
        }

        if (!_world.GathererReady)
        {
            Fail($"{outstanding.ItemCount - _world.ItemCount(outstanding.ItemId)} × item {outstanding.ItemId} " +
                 "needs gathering and GatherBuddy is not loaded — gather them yourself, then Retry");
            return;
        }

        if (_world.IsGathering)
        {
            if (!_world.GathererIdle)
            {
                _phaseStart = now; // working
                return;
            }
            if (now - _phaseStart <= MakeIdle)
                return;
            var why = _world.GathererStatus; // take the reason before switching it off
            StopGathering();
            Fail($"GatherBuddy has been idle for {MakeIdle.TotalSeconds:F0}s" +
                 (why.Length > 0 ? $" — \"{why}\"" : "") +
                 $". Check item {outstanding.ItemId} is on one of its auto-gather lists");
            return;
        }

        if (_makeAsked)
        {
            Fail($"GatherBuddy stopped on its own with item {outstanding.ItemId} still short — " +
                 "check it is on one of its auto-gather lists, then Retry");
            return;
        }

        if (!_world.StartGathering())
        {
            Fail("GatherBuddy would not start");
            return;
        }
        _makeAsked = true;
        _phaseStart = now;
        _world.Log($"Asked GatherBuddy for {outstanding.ItemCount} × item {outstanding.ItemId}.");
    }

    /// <summary>Only ever switch it off if we switched it on — the user's own session is not ours to stop.</summary>
    private void StopGathering()
    {
        if (_makeAsked)
            _world.StopGathering();
    }

    /// <summary>
    /// Equip the gearset a <c>SwitchClass</c> step means. Nothing here presses a class into being:
    /// if the character has no gearset for what the step asks, that is a stop with a name, because
    /// the alternative is a quest that silently cannot progress.
    /// </summary>
    private Phase BeginClassSwitch(QuestStep step)
    {
        if (step.TargetClass is not { Length: > 0 } target)
        {
            Fail("SwitchClass step names no class");
            return Phase.None;
        }

        var (set, failure) = ResolveSwitch(target);
        if (set is null)
        {
            Fail(failure);
            return Phase.None;
        }
        // Also the "already there" answer for a class asked for by its pre-30 name: the Conjurer a
        // step wants is satisfied by the White Mage gearset, whose ClassJob is what we are on.
        if (_world.CurrentClassJob == set.ClassJobId)
            return Phase.Finish;
        if (_world.InCombat)
        {
            Fail($"cannot switch to {target} in combat");
            return Phase.None;
        }
        if (!_world.EquipGearset(set.Id))
        {
            Fail($"gearset {set.Id} for {target} was refused");
            return Phase.None;
        }
        _switchTarget = set.ClassJobId;
        return Phase.ClassSwitch;
    }

    /// <summary>
    /// Which gearset a target name means. Three of the data's names are symbolic and resolve
    /// against the character rather than the ClassJob sheet; the rest are class names. Returns a
    /// null set and the reason when nothing fits.
    /// </summary>
    private (GearsetInfo? Set, string Failure) ResolveSwitch(string target)
    {
        var sets = _world.Gearsets();

        if (Same(target, "ConfiguredCombatJob"))
            return (Highest(sets, JobKind.Combat), "no combat gearset exists — save one, then Retry");
        if (Same(target, "ConfiguredCraftingJob"))
            return (Highest(sets, JobKind.Crafter), "no crafting gearset exists — save one, then Retry");

        var startJob = Same(target, "QuestStartJob");
        var wanted = startJob ? _world.QuestStartClassJob(_questId) : _world.ResolveClassJob(target);
        if (wanted is not { } job || job == 0)
            return (null, startJob
                ? $"quest {_questId} does not say which class it was accepted on"
                : $"unknown class \"{target}\" in the path data");

        // A job satisfies its own class, so a Conjurer step takes the White Mage gearset. Highest
        // level wins when several match — a character with both keeps the one it actually plays.
        GearsetInfo? best = null;
        foreach (var s in sets)
            if ((s.ClassJobId == job || s.ParentClassJobId == job) && (best is null || s.Level > best.Level))
                best = s;
        return (best, $"no gearset for {target} — save one, then Retry");
    }

    private static GearsetInfo? Highest(System.Collections.Generic.IReadOnlyList<GearsetInfo> sets, JobKind kind)
    {
        GearsetInfo? best = null;
        foreach (var s in sets)
            if (s.Kind == kind && (best is null || s.Level > best.Level))
                best = s;
        return best;
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

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

        var rewardWindow = _world.IsAddonVisible("JournalResult");
        if (rewardWindow)
            TickRewardWindow(now);
        else
            _rewardWindowSince = default;

        var handOverWindow = _world.IsAddonVisible("Request");
        if (handOverWindow)
        {
            if (TickHandOverWindow(now))
                return; // failed with a reason of its own
        }
        else
            _handOverSince = default;

        if (now - _phaseStart > DialogueMax)
        {
            Fail(rewardWindow
                ? "the quest reward window is waiting for a choice — pick a reward (or turn on \"Pick quest rewards automatically\"), then Retry"
                : handOverWindow
                    ? $"the hand-over window is still asking for {Describe(_world.HandOverRequests)}"
                    : "dialogue never ended");
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

    /// <summary>
    /// The quest reward window. TextAdvance (under our external control) picks any optional
    /// reward and normally completes; if the window is still up after a short grace we press
    /// Complete ourselves. A disabled Complete means a choice is outstanding — that is either the
    /// reward toggle being off or TextAdvance not being loaded, and we say which rather than wait
    /// two minutes in silence.
    /// </summary>
    private void TickRewardWindow(DateTime now)
    {
        if (_rewardWindowSince == default)
        {
            _rewardWindowSince = now;
            _rewardLastTry = default;
            return;
        }
        if (now - _rewardWindowSince < RewardWindowGrace || now - _rewardLastTry < RewardCompleteRetry)
            return;

        _rewardLastTry = now;
        if (_world.CompleteQuestRewardWindow())
        {
            _rewardNeedsChoiceLogged = false;
            return;
        }
        if (!_rewardNeedsChoiceLogged)
        {
            _rewardNeedsChoiceLogged = true;
            _world.Log("Quest reward window is up and Complete is not available — an optional reward needs choosing. " +
                       "Waiting for TextAdvance or you.");
        }
    }

    /// <summary>
    /// The NPC hand-over window ("Request"). An interaction that wants items cannot end until its
    /// slots are filled and Hand Over is pressed; TextAdvance does that when it is loaded and
    /// holding, so it gets the same short grace the reward window gives it before we do it ourselves.
    ///
    /// <para>
    /// The one thing worth failing fast on is a hand-in that <i>cannot</i> be satisfied: the game
    /// answers that itself, and saying "this wants 3 × Cracked Cluster and you have 1" the moment
    /// the window opens is worth more than two minutes of a dialogue that was never going to end.
    /// </para>
    /// </summary>
    /// <returns>True when the step has been failed and the caller should stop.</returns>
    private bool TickHandOverWindow(DateTime now)
    {
        if (_handOverSince == default)
        {
            _handOverSince = now;
            _handOverLastTry = default;
            return false;
        }

        if (now - _handOverSince < HandOverGrace)
            return false;

        // Judged only once the window has settled: its slots are populated a frame or two after it
        // appears, and an unsatisfiable hand-in is not something TextAdvance could have fixed anyway.
        if (!_world.CanSatisfyHandOver)
        {
            Fail($"the hand-over window wants {Describe(_world.HandOverRequests)} and the bags cannot cover it");
            return true;
        }

        if (now - _handOverLastTry < HandOverRetry)
            return false;

        _handOverLastTry = now;
        _world.CompleteHandOverWindow();
        return false;
    }

    /// <summary>
    /// What the window wants, and — for anything the bags are short of — whether it is sitting in
    /// the FC chest instead. That last part is the difference between "go and craft three of these"
    /// and "go and take the three you already own out of the chest".
    /// </summary>
    private string Describe(System.Collections.Generic.IReadOnlyList<HandOverRequest> requests)
    {
        if (requests.Count == 0)
            return "nothing it will name";
        var parts = new string[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            var chest = _world.ItemCount(r.ItemId) < r.Quantity ? _world.FreeCompanyChestCount(r.ItemId) : 0;
            parts[i] = $"{r.Quantity} × {r.Name}" + (chest > 0 ? $" ({chest} in the FC chest)" : string.Empty);
        }
        return string.Join(", ", parts);
    }

    private void AnswerDialogue(QuestStep step)
    {
        if (step.DialogueChoices is null)
            return;

        var listVisible = _world.IsAddonVisible("SelectString");
        if (!listVisible)
            _listAnswered = false; // a new list later in the same interaction gets its own answer

        foreach (var choice in step.DialogueChoices)
        {
            if (choice.Type.Equals("YesNo", StringComparison.OrdinalIgnoreCase) && _world.IsAddonVisible("SelectYesno"))
            {
                _world.SelectYesNo(choice.Yes ?? true);
                continue;
            }

            if (!choice.Type.Equals("List", StringComparison.OrdinalIgnoreCase) || !listVisible || _listAnswered)
                continue;

            // The data carries text keys; the menu shows text. Resolve the answer key against the
            // quest's dialogue sheet and pick the entry that says it.
            var wanted = choice.Answer is { } key ? _texts?.Resolve(_questId, key) : null;
            if (wanted is null)
            {
                _world.Log($"List choice {choice.Answer ?? "?"} could not be resolved for quest {_questId} — leaving the menu to TextAdvance/you.");
                _listAnswered = true;
                continue;
            }
            var entries = _world.SelectStringEntries();
            var index = FindEntry(entries, wanted);
            if (index < 0)
            {
                _world.Log($"List choice \"{wanted}\" not among [{string.Join(" | ", entries)}] — leaving the menu.");
                _listAnswered = true;
                continue;
            }
            _world.SelectStringIndex(index);
            _listAnswered = true;
        }
    }

    /// <summary>Exact match first, then a case-insensitive contains either way — menu text can carry a trailing marker.</summary>
    public static int FindEntry(System.Collections.Generic.IReadOnlyList<string> entries, string wanted)
    {
        for (var i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Trim(), wanted.Trim(), StringComparison.Ordinal))
                return i;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i].Trim();
            if (e.Contains(wanted.Trim(), StringComparison.OrdinalIgnoreCase) || wanted.Trim().Contains(e, StringComparison.OrdinalIgnoreCase) && e.Length > 3)
                return i;
        }
        return -1;
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
