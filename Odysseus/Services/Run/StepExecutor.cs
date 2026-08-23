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
        /// <summary>Off the mount before doing something that needs both feet on the ground.</summary>
        Dismount,
        /// <summary>Use a quest item on a target, and try again if the game refuses.</summary>
        ItemUse,
        /// <summary>Fire the step's named action, waiting for its target to exist first.</summary>
        ActionUse,
        /// <summary>Fire the step's emote at its target, with the same patience for the target.</summary>
        EmoteUse,
        /// <summary>Pressing the descent key until the water accepts us.</summary>
        Dive,
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
        /// <summary>Equip submitted, waiting for the item to actually be worn.</summary>
        Equip,
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

    /// <summary>Close enough to a combat mark to land and finish the approach on foot.</summary>
    private const float CombatLandRadius = 15f;

    /// <summary>How far above an object a flight may end and still count as arrived — the dismount descends the rest.</summary>
    private const float HoverAboveObject = 10f;

    /// <summary>Steps whose business is a thing in the world, reached when the thing is, done from the ground.</summary>
    private static bool IsObjectStep(StepKind kind) => kind is StepKind.Interact or StepKind.AcceptQuest
        or StepKind.CompleteQuest or StepKind.AttuneAetheryte or StepKind.AttuneAethernetShard or StepKind.AttuneAetherCurrent;

    /// <summary>A WalkTo given up on this close to its mark is taken as arrived rather than faulted.</summary>
    private const float WalkToNearEnough = 5f;
    /// <summary>
    /// Close enough to an aethernet shard to use it. Lifestream has to <i>interact</i> with the
    /// shard, so this is interact range plus the object's own bulk — not "somewhere near it". A
    /// wider reach stopped the approach fifteen yalms out, where the hop was refused every time.
    /// </summary>
    public const float AethernetReachDistance = 6f;

    /// <summary>
    /// Inside this, a detour that the mesh cannot finish is closed in a straight line. The navmesh
    /// does not extend under a solid object, so a path to a shard ends a few yalms short and stays
    /// there — which is why jumping, which nudges you off the mesh edge, made a stalled approach
    /// complete.
    /// </summary>
    private const float DetourNudgeDistance = 12f;

    /// <summary>How far around an off-mesh destination to look for a point the mesh does reach.</summary>
    private const float OffMeshSnapRange = 10f;

    /// <summary>The most that is walked blind from the mesh's nearest point to the destination.</summary>
    private const float OffMeshDirectMax = 15f;

    /// <summary>How far around the player's own feet to look for the mesh, and how far off it counts as off.</summary>
    private const float OffMeshFootingRange = 4f;
    private const float OffMeshFeet = 0.75f;

    /// <summary>Distances past this are worth a mount.</summary>
    public const float MountWorthDistance = 30f;
    /// <summary>Overworld enemies farther than this are not "ours".</summary>
    public const float CombatSearchRadius = 30f;

    /// <summary>How far a named overworld target is hunted from the mark — the roamers' range.</summary>
    public const float OverworldHuntRadius = 90f;
    /// <summary>
    /// vnavmesh declares arrival by its own tolerance and can stop a hair outside ours; without
    /// slack the executor would re-path three times over half a yalm and then fail the step.
    /// </summary>
    public const float ArrivalSlack = 1.5f;

    private static readonly TimeSpan MoveStall = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MoveTotal = TimeSpan.FromSeconds(180);
    /// <summary>
    /// How long to keep asking for a mount before walking instead. Longer than it looks like it
    /// needs to be: the seconds after a teleport are a lock, and a mount asked for inside it is
    /// dropped without a word.
    /// </summary>
    private static readonly TimeSpan MountWait = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MountRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DialogueSettle = TimeSpan.FromSeconds(3);

    /// <summary>Closing this much counts as progress; anything less is standing still.</summary>
    private const float StallProgress = 0.5f;

    /// <summary>How long without getting closer before a jump is worth a try.</summary>
    private static readonly TimeSpan StallJumpAfter = TimeSpan.FromSeconds(4);

    /// <summary>And how long before another one.</summary>
    private static readonly TimeSpan StallJumpGap = TimeSpan.FromSeconds(8);

    /// <summary>How long to leave a refused item use before trying it again.</summary>
    private static readonly TimeSpan ItemUseRetry = TimeSpan.FromSeconds(1.5);

    /// <summary>How many refusals before believing the game means it.</summary>
    private const int MaxItemUseTries = 4;

    /// <summary>How often to ask again while a dismount is still coming down.</summary>
    private static readonly TimeSpan DismountRetry = TimeSpan.FromSeconds(2);

    /// <summary>How many times an interaction that opened nothing is asked again before moving on.</summary>
    private const int MaxInteractRetries = 2;

    /// <summary>
    /// Close enough for a keypress to land on an NPC. Measured against the object itself rather
    /// than the step's recorded position, and in three dimensions, because the way this fails is
    /// vertical: the walk finishes on the lip above the NPC, well inside the step's stop distance
    /// on the map, and every interact from up there does nothing at all.
    /// </summary>
    public const float InteractReach = 3.5f;

    /// <summary>How long a list the step does not name is left to TextAdvance, or to you, before we take it.</summary>
    private static readonly TimeSpan UndeclaredListGrace = TimeSpan.FromSeconds(3);
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
    /// <summary>
    /// A hop idle this long has not worked. Short on purpose — it fires before the "never started"
    /// verdict, and it only applies while Lifestream is doing nothing, so a hop mid-cast is never
    /// interrupted by it.
    /// </summary>
    private static readonly TimeSpan AethernetRetry = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DutyEnterMax = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SoloDutyMax = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DutyMax = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan ActionSettle = TimeSpan.FromSeconds(2);
    /// <summary>A purchase is a server round trip; leave a beat between rounds rather than spamming the handler.</summary>
    private static readonly TimeSpan ShopGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShopMax = TimeSpan.FromSeconds(30);
    /// <summary>How long a handoff may sit having done nothing before we call it stuck. Progress resets it.</summary>
    private static readonly TimeSpan MakeIdle = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Artisan is asked and answers later — it has to open the crafting log and start its endurance
    /// loop. Judging it on the next frame declared a craft dead before it had begun.
    /// </summary>
    private static readonly TimeSpan CraftStartGrace = TimeSpan.FromSeconds(10);
    /// <summary>How long a pathfind is given to answer before the attempt is judged.</summary>
    private static readonly TimeSpan PathSettle = TimeSpan.FromSeconds(2);
    private const int MaxMoveRetries = 3;

    private readonly IStepWorld _world;
    private readonly Quest.IDialogueTexts? _texts;

    private QuestStep? _step;
    private ushort _questId;
    private bool _listAnswered;
    private DateTime _listOpenedAt;
    private DateTime _rewardWindowSince;
    private DateTime _rewardLastTry;
    private bool _rewardNeedsChoiceLogged;
    private DateTime _handOverSince;
    private DateTime _handOverLastTry;
    /// <summary>The shop the current PurchaseItem step buys from; learned from the window when the step names none.</summary>
    private uint _shopId;
    /// <summary>How many of the item being bought the bag should hold when the purchase is done.</summary>
    private int _buyTarget;
    /// <summary>What is being bought — the step's own item, or a material a craft turned out to need.</summary>
    private uint _buyItem;
    /// <summary>The purchase is feeding a craft, so go back to crafting rather than finishing.</summary>
    private bool _shopThenCraft;
    /// <summary>Materials already bought for this step; each is tried once so a stall cannot loop.</summary>
    private readonly System.Collections.Generic.HashSet<uint> _boughtForCraft = [];
    /// <summary>The NPC whose shop is being opened.</summary>
    private uint _vendorDataId;
    /// <summary>A detour: walk here rather than to the step's own point, then go to <see cref="_detourThen"/>.</summary>
    private Vector3? _detourTo;
    private Phase _detourThen;
    /// <summary>How close the detour has to get — interact range for a merchant, a shard's bulk for a shard.</summary>
    private float _detourTolerance = DefaultStopDistance;
    /// <summary>The straight-line finish has been used for this detour; it gets one go.</summary>
    private bool _detourNudged;
    private Vector3? _offMeshSnap;
    private bool _offMeshNudged;
    private bool _footingTaken;
    private bool _meshRebuilt;
    private readonly Func<bool> _acceptOvercap;
    private DateTime _lastOvercapYes;
    private bool _flyFallback;
    private bool _combatLanded;
    private bool _landedToFinish;
    /// <summary>This detour ends at an aethernet stop, so the game can say when it is done.</summary>
    private bool _detourNeedsShard;
    private DateTime _lastBuy;
    private DateTime _lastShopOpen;
    /// <summary>The ClassJob a SwitchClass step is waiting to land on.</summary>
    private uint _switchTarget;
    /// <summary>An aethernet hop the route resolver added, for a zone with no aetheryte of its own.</summary>
    private string? _autoAethernet;
    /// <summary>The zone the current hop should land in; 0 when the sheet does not know it.</summary>
    private uint _aethernetTerritory;
    /// <summary>How many times this step has re-asked for its hop.</summary>
    private int _aethernetRetries;
    /// <summary>The handoff has been asked; a second ask would queue another batch on top.</summary>
    private bool _makeAsked;
    /// <summary>The item last handed to Artisan — the target, or a sub-component of it.</summary>
    private uint _craftAsked;
    /// <summary>How many of it were held when it was asked for, so "did anything arrive" is answerable.</summary>
    private int _craftHeldAtAsk;
    private Phase _phase = Phase.None;
    private DateTime _phaseStart;
    private DateTime _stepStart;
    private DateTime _lastMoveIssue;
    private DateTime _lastMountTry;
    private int _moveRetries;
    private bool _sawOccupied;
    private bool _groundOnly;
    private bool _dismountAsked;
    private bool _itemReachTried;
    private bool _iconAnswered;
    private bool _iconReported;
    private int _itemUseTries;
    private DateTime _lastItemTry;
    private float _closestSeen;
    private DateTime _stalledSince;
    private DateTime _lastStallJump;
    private Phase _dismountThen;
    private bool _dismountRechoose;
    private DateTime _lastDismountTry;
    private int _interactRetries;
    private bool _sawCombat;
    private bool _inFight;
    private int _fights;
    private DateTime _lastCombatSeen;
    private bool _skipTeleport;
    /// <summary>The instance this step hands off has been run; arriving again means finish, not re-enter.</summary>
    private bool _handoffDone;
    private uint _teleportTarget;
    private uint _teleportTerritory;
    private bool _sawTravelBusy;

    /// <param name="texts">Resolves dialogue text keys for the current quest; null means List choices and Say cannot be answered.</param>
    public StepExecutor(IStepWorld world, Quest.IDialogueTexts? texts = null, Func<bool>? acceptOvercap = null)
    {
        _acceptOvercap = acceptOvercap ?? (() => true);
        _world = world;
        _texts = texts;
    }

    public StepStatus Status { get; private set; } = StepStatus.Idle;
    public string FailReason { get; private set; } = string.Empty;

    /// <summary>
    /// The step failed because the thing it wanted was not in the world. Worth telling apart from
    /// every other failure: an NPC that is not there is often one already dealt with, which is what
    /// a sequence of "talk to each of these three" looks like when it is resumed part-done.
    /// </summary>
    public bool TargetMissing { get; private set; }
    public QuestStep? Current => _step;
    public string PhaseName => _phase.ToString();

    /// <param name="skipTeleport">The step's <c>AetheryteShortcutIf</c> holds — walk instead of teleporting.</param>
    /// <param name="questId">The quest this step belongs to — needed to resolve dialogue text keys. 0 for a bare step.</param>
    /// <param name="groundOnly">
    /// Ignore the step's <c>Fly</c> flag and walk. Set for an allied society path in a base-game
    /// zone, where the flight the data asks for catches on scenery.
    /// </param>
    public void Begin(QuestStep step, bool skipTeleport = false, ushort questId = 0, bool groundOnly = false)
    {
        _groundOnly = groundOnly;
        _step = step;
        _questId = questId;
        _listAnswered = false;
        _listOpenedAt = default;
        _rewardWindowSince = default;
        _rewardNeedsChoiceLogged = false;
        _handOverSince = default;
        _shopId = 0;
        _buyTarget = 0;
        _buyItem = 0;
        _shopThenCraft = false;
        _vendorDataId = 0;
        _detourTo = null;
        _boughtForCraft.Clear();
        _lastBuy = default;
        _lastShopOpen = default;
        _switchTarget = 0;
        _autoAethernet = null;
        _aethernetTerritory = 0;
        _aethernetRetries = 0;
        _makeAsked = false;
        _craftAsked = 0;
        _craftHeldAtAsk = 0;
        _stepStart = _world.UtcNow;
        _moveRetries = 0;
        _sawOccupied = false;
        _interactRetries = 0;
        _dismountAsked = false;
        _lastDismountTry = default;
        _itemReachTried = false;
        _iconAnswered = false;
        _iconReported = false;
        _itemUseTries = 0;
        _lastItemTry = default;
        _closestSeen = float.MaxValue;
        _stalledSince = default;
        _lastStallJump = default;
        _sawCombat = false;
        _flyFallback = false;
        _combatLanded = false;
        _landedToFinish = false;
        _lastDiveTry = default;
        _diveAttempts = 0;
        _inFight = false;
        _fights = 0;
        _skipTeleport = skipTeleport;
        _sawTravelBusy = false;
        _handoffDone = false;
        FailReason = string.Empty;
        TargetMissing = false;
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
        if ((_makeAsked || _craftAsked != 0) && _step is { } running)
        {
            if (running.Kind == StepKind.Craft && _craftAsked != 0) _world.StopCrafting();
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
        or StepKind.PurchaseItem or StepKind.SwitchClass or StepKind.Craft or StepKind.Gather
        or StepKind.EquipItem or StepKind.CreateGearset or StepKind.UpdateGearset or StepKind.Dive;

    /// <summary>The step hands the character to another plugin for a whole instance.</summary>
    public static bool IsHandoff(StepKind kind) => kind is StepKind.SinglePlayerDuty or StepKind.Duty;

    /// <summary>
    /// Carried out on the character rather than in the world, so there is nowhere to be.
    ///
    /// <para>
    /// Every step in the data carries a territory, but for these it records where the path author
    /// happened to be standing, not a requirement — equipping a hammer, saving a gearset, switching
    /// class or handing a craft to Artisan all work from anywhere. Enforcing that tag stopped a run
    /// at the Free Company workshop because a step was written in Ul'dah.
    /// </para>
    ///
    /// <para>
    /// <see cref="StepKind.Gather"/> is deliberately not here. Its territory may well be the zone
    /// its nodes are in, and guessing wrong there means sending the gatherer somewhere useless.
    /// </para>
    /// </summary>
    public static bool IsPlaceless(StepKind kind) => kind is
        StepKind.EquipItem or StepKind.CreateGearset or StepKind.UpdateGearset or StepKind.SwitchClass
        or StepKind.Craft or StepKind.EquipRecommended or StepKind.Instruction or StepKind.StatusOff;

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

        // The high-quality trade confirmation is modal: it blocks the interaction that raised it
        // AND everything queued behind it — it was found stalling an aethernet hop, two phases
        // away from the hand-in it belongs to. So it is answered wherever it appears, which is
        // safe because the world matches it against the game's own string for that one prompt.
        _world.ConfirmTradeDialog();

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
                if (AethernetDestination is not { } hop)
                {
                    Fail("aethernet hop with no destination");
                    break;
                }
                if (!_world.AethernetTeleport(hop))
                {
                    Fail($"aethernet to {hop} was refused — Lifestream loaded?");
                    break;
                }
                _aethernetTerritory = _world.AethernetTerritoryOf(hop) ?? 0;
                Enter(Phase.AethernetWait);
                break;

            case Phase.AethernetWait:
            {
                // Judged by where it landed, not merely by Lifestream having gone quiet. A hop that
                // matched nothing stops being busy immediately, and calling that "arrived" reported
                // the wrong failure two phases later.
                var landed = !_world.IsTravelBusy && _world.IsReady
                             && (_aethernetTerritory == 0 || _world.TerritoryId == _aethernetTerritory);

                // The two ways of asking take different routes inside Lifestream, and one has been
                // seen to refuse a destination the other reaches. So a hop still in the air is asked
                // for the other way rather than waited out for a minute and a half.
                if (!landed && !_world.IsTravelBusy && _aethernetRetries == 0
                    && now - _phaseStart > AethernetRetry && AethernetDestination is { } again)
                {
                    _aethernetRetries++;
                    _world.Log($"Aethernet to {again} has not landed in {AethernetRetry.TotalSeconds:F0}s " +
                               "and Lifestream is idle; asking again by name.");
                    _world.AethernetTeleport(again, byNameOnly: true);
                    _phaseStart = now;
                    _sawTravelBusy = false;
                    break;
                }

                TickTravelWait(now, arrived: landed,
                    what: $"aethernet to {AethernetDestination}", next: NextAfterTravel);
                break;
            }

            case Phase.Mount:
                if (_world.IsMounted || now - _phaseStart > MountWait)
                {
                    Enter(Phase.Move); // mounted, or long enough — walking is always an option
                    break;
                }
                // Asked here rather than once on the way in, because the request only lands when the
                // character can act, and after a teleport that is not straight away.
                if (_world.IsReady && !_world.InCombat && now - _lastMountTry >= MountRetry)
                {
                    _lastMountTry = now;
                    _world.Mount();
                }
                break;

            case Phase.Move:
                TickMove(step, now);
                break;

            case Phase.Dismount:
                TickDismount(step, now);
                break;

            case Phase.ItemUse:
                TickItemUse(step, now);
                break;

            case Phase.ActionUse:
                TickActionUse(step, now);
                break;

            case Phase.EmoteUse:
                TickEmoteUse(step, now);
                break;

            case Phase.Dive:
                TickDive(now);
                break;

            case Phase.WaitReady:
                if (_world.IsReady && !_world.IsOccupied)
                    Enter(NextAfterArrival(step));
                else if (_world.IsOccupied && step.Kind is StepKind.CompleteQuest or StepKind.AcceptQuest)
                {
                    // The previous hand-in's chain is still open, and for an accept or turn-in
                    // that conversation IS the step — join it rather than waiting behind it.
                    _sawOccupied = true;
                    Enter(Phase.Dialogue);
                }
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
                if (step.Kind is StepKind.UseItem or StepKind.Combat && _world.IsCasting)
                {
                    // A quest item is a cast, and anything that interrupts it — mounting for the
                    // next leg above all — cancels the use without a word: the ash is spent, the
                    // beacon stays lit, and the objective sits at 0/5. So the settle clock starts
                    // when the cast *ends*, not when it began: the step holds here through the
                    // cast, then gives the game the full settle to register the effect before
                    // anything is allowed to move.
                    _phaseStart = now;
                    break;
                }
                if (step.Kind == StepKind.UseItem && _world.IsOccupied)
                {
                    // An item that opens a dialogue behaves like an interact from here.
                    _sawOccupied = true;
                    Enter(Phase.Dialogue);
                }
                else if (now - _phaseStart > ActionSettle)
                    Enter(step.Kind == StepKind.Combat
                        || (step.Kind == StepKind.UseItem && step.EnemySpawnType == EnemySpawnType.AfterItemUse)
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
                {
                    if (_shopThenCraft) { Enter(Phase.Craft); break; }
                    Fail($"the shop at {_vendorDataId} never opened");
                }
                else if (now - _lastShopOpen >= ShopGap)
                {
                    // Interacting can bounce off a moment of being occupied, so it is retried
                    // rather than issued once and hoped for.
                    _lastShopOpen = now;
                    _world.OpenShop(_vendorDataId, _shopId);
                }
                break;

            case Phase.ShopBuy:
                TickPurchase(step, now);
                break;

            case Phase.ClassSwitch:
                if (_world.CurrentClassJob == _switchTarget)
                    Enter(Phase.Finish);
                else if (now - _phaseStart > ReadyWait)
                    // An EquipItem falling back to a gearset gets here too, and names no class.
                    Fail($"the class did not change to {step.TargetClass ?? $"job {_switchTarget}"} " +
                         $"in {ReadyWait.TotalSeconds:F0}s");
                break;

            case Phase.Equip:
                // Equipping is a server round trip; the slot filling is the only signal.
                if (_world.IsEquipped(step.ItemId!.Value))
                    Enter(Phase.Finish);
                else if (now - _phaseStart > ReadyWait)
                    Fail($"item {step.ItemId} never reached an equipment slot");
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
                // Bad or renamed data. Say so, but do not stop on it — working the route out
                // ourselves below gets there anyway, and the log keeps the data problem visible.
                _world.Log($"Unknown aetheryte \"{aetheryteName}\" in the path data; finding my own way.");
            }
            else
            {
                var territory = _world.AetheryteTerritory(id.Value) ?? 0;
                var farAway = step.Position is { } p && Vector3.Distance(_world.PlayerPosition, p) > TeleportWorthDistance;
                if (_world.TerritoryId != territory || farAway)
                {
                    _teleportTarget = id.Value;
                    _teleportTerritory = territory;
                    return Phase.Teleport;
                }
                return NextAfterTeleport();
            }
        }

        return NextAfterOwnRoute();
    }

    /// <summary>
    /// Get to the step's zone when the path does not say how.
    ///
    /// <para>
    /// Most steps name their own aetheryte, and those are honoured above. The rest assume you are
    /// already in the right place because the previous quest left you there — true while a run
    /// rolls on, false the moment you press Start from somewhere else. That is the case this
    /// exists for: the run should take itself to the quest rather than stopping, or worse, waiting
    /// in silence for a zone change that is never going to happen.
    /// </para>
    /// </summary>
    private Phase NextAfterOwnRoute()
    {
        var step = _step!;

        if (step.TerritoryId == 0 || _world.TerritoryId == step.TerritoryId || IsPlaceless(step.Kind))
            return NextAfterTeleport();

        // The path already says which shard to hop to, and it chose the one beside the NPC. This
        // resolver exists for steps that say nothing — overriding a named destination sent the run
        // to the Gladiators' Guild for a quest in the Goldsmiths'. Reaching here with a hop named
        // means the step's own teleport was skipped because its condition says we are already in
        // the right city, so the hop is usable as written.
        if (step.AethernetShortcut is { Length: 2 })
            return NextAfterTeleport();

        // Already where a zone-line walk was meant to end — the walk itself handles that arrival.
        if (step.TargetTerritoryId is { } crossing && _world.TerritoryId == crossing)
            return NextAfterTeleport();

        if (_world.RouteTo(step.TerritoryId, step.Position) is not { } route)
            return NextAfterTeleport(); // no way in; the travel check names it

        _autoAethernet = route.AethernetName;
        _world.Log($"Quest step is in territory {step.TerritoryId} and the path names no way there — " +
                   (route.AetheryteId is { } id
                       ? $"teleporting to aetheryte {id}" + (route.AethernetName is { } hop ? $", then {hop}." : ".")
                       : $"taking the aethernet to {route.AethernetName}."));

        if (route.AetheryteId is not { } aetheryte)
            return Phase.Aethernet; // already in the city; the hop is the whole journey

        _teleportTarget = aetheryte;
        _teleportTerritory = route.AetheryteTerritory;
        return Phase.Teleport;
    }

    /// <summary>The aethernet destination in play — the route we worked out, else the one the step names.</summary>
    private string? AethernetDestination => _autoAethernet ?? (_step?.AethernetShortcut is { Length: 2 } s ? s[1] : null);

    private Phase NextAfterTeleport()
    {
        if (AethernetDestination is { } hop && WorthHopping(hop))
            return BeginAethernet();
        return NextAfterTravel();
    }

    /// <summary>
    /// Whether the hop is worth making at all.
    ///
    /// <para>
    /// The aethernet is for crossing a city. Standing in the half the step is already in, taking it
    /// walks you out to a shard, teleports you to the shard you were standing beside, and walks you
    /// back — which is what every Goldsmith quest did, because its NPC sits a few paces from the
    /// Goldsmiths' Guild shard the step names.
    /// </para>
    ///
    /// <para>
    /// A destination in another zone is always taken: that is the only way across. In the same one
    /// it is taken only when the walk would be long enough to be worth the detour, on the same
    /// reasoning as <see cref="TeleportWorthDistance"/>.
    /// </para>
    /// </summary>
    private bool WorthHopping(string destination)
    {
        if (_world.AethernetTerritoryOf(destination) is not { } lands || lands != _world.TerritoryId)
            return true;
        return _step!.Position is { } target
               && Vector3.Distance(_world.PlayerPosition, target) > TeleportWorthDistance;
    }

    /// <summary>
    /// Walk to an aethernet access point before hopping.
    ///
    /// <para>
    /// The network is only reachable from a shard or the city aetheryte — standing in the middle of
    /// Ul'dah, there is nothing to use. The path data says so by naming the shard to travel
    /// <i>from</i> as well as the one to travel to, which we had been ignoring, so the hop was
    /// asked for from wherever the previous step happened to end.
    /// </para>
    /// </summary>
    private Phase BeginAethernet()
    {
        if (_world.AtAethernetShard)
            return Phase.Aethernet; // the game says we are at one; nothing to walk

        if (_world.NearestAethernetAccess(_world.TerritoryId, _world.PlayerPosition) is not { } access)
            return Phase.Aethernet; // nothing placed in this zone; let the hop try anyway

        if (Vector3.Distance(_world.PlayerPosition, access) <= AethernetReachDistance)
            return Phase.Aethernet;

        _world.Log($"Walking to the aethernet at {Fmt(access)} before hopping to {AethernetDestination}.");
        _detourTo = access;
        _detourThen = Phase.Aethernet;
        _detourTolerance = AethernetReachDistance;
        _detourNeedsShard = true;
        return Phase.Move;
    }

    private Phase NextAfterTravel()
    {
        var step = _step!;

        // Nothing to reach and nowhere to be — do it where you stand.
        if (IsPlaceless(step.Kind))
            return Phase.WaitReady;

        // A step that crosses a zone line names both ends: TerritoryId is where it starts and
        // TargetTerritoryId is where it finishes. Standing in the far one means the crossing has
        // already happened — which is what an aethernet hop into it does — so it is arrival, not
        // the wrong zone. (Highway Robbery's first step: starts in 129, ends in 128, hops there.)
        if (step.TargetTerritoryId is { } crossedInto && _world.TerritoryId == crossedInto)
            return Phase.WaitReady;

        if (step.TerritoryId != 0 && _world.TerritoryId != step.TerritoryId)
        {
            // Getting here means the route resolver could not help either, so say which of the two
            // reasons it is. "Waiting to be somewhere" with no explanation is the failure mode this
            // whole path exists to avoid.
            Fail($"the quest is in territory {step.TerritoryId} and you are in {_world.TerritoryId} — " +
                 "no aetheryte there that you have attuned. Attune one or travel there yourself, then Retry");
            return Phase.None;
        }

        if (step.Position is not { } target)
            return Phase.WaitReady;

        var distance = Vector3.Distance(_world.PlayerPosition, target);
        if (distance <= StopDistanceFor(step) + ArrivalSlack)
            return Phase.WaitReady;

        // A step that says to get off the mount does so before it walks anywhere, and does not get
        // back on for this leg.
        if (step.Dismount && _world.IsMounted)
            return BeginDismount(Phase.Move);

        // Mount for long legs unless the step forbids it.
        var wantMount = !step.Dismount
                        && (step.Mount == true || (step.Mount != false && distance > MountWorthDistance));
        if (wantMount && _world.CanMountHere && !_world.IsMounted)
        {
            _lastMountTry = default;   // the phase does the asking, and keeps asking
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
        // "Land", and every step whose business is an object: the approach ends mounted — often
        // in the air over the mark — and the interaction is done from the ground. A dismount up
        // here is the game's own descent — TickDismount rides it all the way down — so landing
        // is just dismounting early, before the step acts. The chooser below has side effects
        // (emotes fire from it), so it re-runs after the ground rather than being pre-computed
        // as a destination.
        if ((step.Land || IsObjectStep(step.Kind)) && _world.IsMounted)
        {
            _dismountRechoose = true;
            _lastDismountTry = default;
            _dismountedAt = default;
            return Phase.Dismount;
        }

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

            case StepKind.Dive:
                // Below the surface already is arrival; from the surface (or the saddle over
                // it), press the game's own descent bind until the water accepts us.
                if (_world.IsDiving)
                    return Phase.Finish;
                return Phase.Dive;

            case StepKind.Action:
                if (step.ActionName is not { } actionName || _world.ResolveAction(actionName) is not { } _)
                {
                    Fail($"action \"{step.ActionName ?? "?"}\" is not in the Action sheet");
                    return Phase.None;
                }
                return Phase.ActionUse;

            case StepKind.Combat:
                // Enemies that spawn from a thrown item — truesight scalebombs at suspicious
                // objects — need the throw before there is anything to fight. Routing straight to
                // CombatWait sat out the whole quest without doing a step of it.
                if (step.EnemySpawnType == EnemySpawnType.AfterItemUse && step.ItemId is not null)
                    return BeginItemUse(step);
                if (step.EnemySpawnType == EnemySpawnType.AfterEmote && step.Emote is not null)
                    return _world.IsMounted ? BeginDismount(Phase.EmoteUse) : Phase.EmoteUse;
                if (step.EnemySpawnType == EnemySpawnType.AfterAction && step.ActionName is not null)
                    return _world.IsMounted ? BeginDismount(Phase.ActionUse) : Phase.ActionUse;
                if (step.EnemySpawnType == EnemySpawnType.AfterInteraction && step.DataId is not null)
                    return Phase.Interact;
                // Nothing swings from the saddle: a pull made mounted targets the mob and does
                // nothing else, and the fight never starts. Feet first, then the fight.
                return _world.IsMounted ? BeginDismount(Phase.CombatWait) : Phase.CombatWait;

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

            case StepKind.EquipItem:
                return BeginEquip(step);

            // Both of these exist for the moment a class is first unlocked: the quest hands you a
            // tool and expects a gearset to exist for it. On a character that already plays the
            // class there is nothing to do, and creating a second gearset for it would be worse
            // than doing nothing — which is also what makes the step safe to replay.
            case StepKind.CreateGearset:
            {
                var job = _world.CurrentClassJob;
                foreach (var set in _world.Gearsets())
                    if (set.ClassJobId == job)
                        return Phase.Finish;
                if (!_world.CreateGearset())
                {
                    Fail("no free gearset slot — all 100 are in use");
                    return Phase.None;
                }
                return Phase.ActionSettle;
            }

            case StepKind.UpdateGearset:
                if (!_world.UpdateGearset())
                {
                    Fail("no active gearset to update");
                    return Phase.None;
                }
                return Phase.ActionSettle;

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
                return BeginItemUse(step);

            default:
                return Phase.Interact;
        }
    }

    /// <summary>The shared way into using a step's item: reach, saddle, then the use itself.</summary>
    private Phase BeginItemUse(QuestStep step)
    {
        if (step.ItemId is null)
        {
            Fail("the step names no item to use");
            return Phase.None;
        }
        if (step.DataId is { } itemTarget)
        {
            // Using an item on someone is a targeted action with the same reach as talking
            // to them, and the step's recorded position is not always inside it. Close the
            // gap once, measured against where the target actually is.
            if (!_itemReachTried && _world.DistanceToDataId(itemTarget) is { } far && far > InteractReach
                && _world.PositionOfDataId(itemTarget) is { } where)
            {
                _itemReachTried = true;
                _world.Log($"Item target {itemTarget} is {far:F1}y away — walking into range first.");
                _detourTo = where;
                _detourThen = Phase.WaitReady;
                _detourTolerance = InteractReach - ArrivalSlack;
                _detourNudged = false;
                _detourNeedsShard = false;
                return Phase.Move;
            }
        }

        // A quest item cannot be used from the back of a chocobo. Same reason a flight that
        // ends over an NPC cannot talk to them, and the same remedy.
        if (_world.IsMounted)
            return BeginDismount(Phase.ItemUse);

        return Phase.ItemUse;
    }

    /// <summary>Ask to get off the mount, and go to <paramref name="then"/> once we are actually off it.</summary>
    private Phase BeginDismount(Phase then)
    {
        _dismountThen = then;
        _dismountRechoose = false;
        _lastDismountTry = default;
        _dismountedAt = default;
        return Phase.Dismount;
    }

    /// <summary>How long after the mounted flag clears before anything is pressed.</summary>
    private static readonly TimeSpan DismountSettle = TimeSpan.FromSeconds(0.8);

    private DateTime _dismountedAt;

    /// <summary>Feet down long enough to act. First call starts the clock.</summary>
    private bool DismountSettled(DateTime now)
    {
        if (_dismountedAt == default)
        {
            _dismountedAt = now;
            return false;
        }
        return now - _dismountedAt >= DismountSettle;
    }

    /// <summary>
    /// Waiting for both feet on the ground. Dismounting in the air is a descent and you stay
    /// mounted the whole way down, so this keeps asking rather than assuming it took.
    /// </summary>
    private void TickDismount(QuestStep step, DateTime now)
    {
        if (!_world.IsMounted)
        {
            // The flag clears before the animation ends, and a press in that gap is eaten
            // silently. Both feet on the ground, then a beat.
            if (!DismountSettled(now))
                return;
            if (_dismountRechoose)
            {
                _dismountRechoose = false;
                Enter(NextAfterArrival(step)); // Land: pick the step's real phase from the ground
            }
            else
                Enter(_dismountThen);
            return;
        }
        if (now - _lastDismountTry > DismountRetry)
        {
            _lastDismountTry = now;
            _world.Dismount();
        }
        if (now - _phaseStart > ReadyWait)
            Fail("could not get off the mount");
    }

    /// <summary>
    /// Use the step's item on its target.
    ///
    /// <para>
    /// The game refuses this for reasons that pass on their own — an animation still playing, the
    /// last of a dismount, a target that has not finished spawning — so a refusal is retried before
    /// it is believed. <see cref="IStepWorld.UseItem"/> logs the game's own status code for the
    /// refusal, which is the only way to tell those apart from a genuine one.
    /// </para>
    /// </summary>
    /// <summary>
    /// The step's named action, with the same patience every other targeted step gets. The sneeze
    /// targets on the goobbue ride spawn as you approach — failing the moment the object table
    /// lacks them was the whole of "action target is not here", and it cost the daily.
    /// </summary>
    private void TickActionUse(QuestStep step, DateTime now)
    {
        if (_world.IsOccupied)
            return;

        if (!step.GroundTarget && step.DataId is { } target && !_world.TryTargetDataId(target))
        {
            if (now - _phaseStart > ReadyWait)
                Fail($"action target {target} never appeared");
            return;
        }

        if (now - _lastItemTry < ItemUseRetry && _itemUseTries > 0)
            return;
        _lastItemTry = now;

        var actionId = step.ActionName is { } name ? _world.ResolveAction(name) : null;
        if (actionId is null)
        {
            Fail($"action \"{step.ActionName ?? "?"}\" is not in the Action sheet");
            return;
        }

        if (_world.UseAction(actionId.Value, step.GroundTarget ? step.Position : null))
        {
            Enter(Phase.ActionSettle);
            return;
        }

        if (++_itemUseTries >= MaxItemUseTries)
            Fail($"action \"{step.ActionName}\" was refused — see the log for the game's reason");
    }

    /// <summary>How long between descent attempts, and how many before asking for a human.</summary>
    private static readonly TimeSpan DiveRetry = TimeSpan.FromSeconds(5);
    private const int MaxDiveAttempts = 3;
    private DateTime _lastDiveTry;
    private int _diveAttempts;

    /// <summary>
    /// Press the descent bind until Diving is set. The press itself is a queue of key messages
    /// pumped one per frame by the world; this phase's only job is patience and the retry clock.
    /// Ported behaviour from Questionable's Dive task (AGPL-3.0, as is this plugin).
    /// </summary>
    private void TickDive(DateTime now)
    {
        if (_world.IsDiving)
        {
            Enter(Phase.Finish);
            return;
        }
        if (!_world.IsSwimming && !_world.IsMounted)
        {
            Fail("not in the water — a dive needs to start swimming (check the step's position)");
            return;
        }
        if (_diveAttempts >= MaxDiveAttempts && now - _lastDiveTry > DiveRetry)
        {
            Fail("the descent key did not take — dive manually, then Retry");
            return;
        }
        if (_lastDiveTry == default || now - _lastDiveTry > DiveRetry)
        {
            _lastDiveTry = now;
            _diveAttempts++;
        }
        _world.PressDescent(); // pumps one key message per call
    }

    /// <summary>
    /// The step's emote at its target — the doze that baits a spawn. The target gets the same
    /// patience an action target does; it can pop in on approach.
    /// </summary>
    private void TickEmoteUse(QuestStep step, DateTime now)
    {
        if (_world.IsOccupied)
            return;

        if (step.DataId is { } target && !_world.TryTargetDataId(target))
        {
            if (now - _phaseStart > ReadyWait)
                Fail($"emote target {target} never appeared");
            return;
        }

        _world.SendChatCommand($"/{step.Emote}");
        Enter(Phase.ActionSettle);
    }

    private void TickItemUse(QuestStep step, DateTime now)
    {
        if (now - _lastItemTry < ItemUseRetry && _itemUseTries > 0)
            return;

        if (step.DataId is { } target && !_world.TryTargetDataId(target))
        {
            if (_itemUseTries >= MaxItemUseTries)
            {
                Fail($"item target {target} is not here");
                return;
            }
            _itemUseTries++;
            _lastItemTry = now;
            return;
        }

        // Face them before using it. The walk ends pointed along its last leg — which, after the
        // straight-line finish, is usually past the target rather than at it — and an item used on
        // someone you are not looking at does nothing.
        if (step.DataId is { } facing)
            _world.FaceDataId(facing);

        _lastItemTry = now;
        _world.HoldDialogue();

        // A ground-targeted item is thrown at a spot, not used on a target — the scalebomb lands
        // on the suspicious object, wherever the object actually stands.
        var used = step.GroundTarget && step.DataId is { } ground && _world.PositionOfDataId(ground) is { } spot
            ? _world.UseItemOnGround(step.ItemId!.Value, spot)
            : _world.UseItem(step.ItemId!.Value);
        if (used)
        {
            Enter(Phase.ActionSettle);
            return;
        }

        if (++_itemUseTries >= MaxItemUseTries)
        {
            Fail($"the game would not let us use item {step.ItemId} here — see the log for its reason");
            return;
        }
        _world.Log($"Item {step.ItemId} was refused — trying again ({_itemUseTries}/{MaxItemUseTries}).");
    }

    private void TickMove(QuestStep step, DateTime now)
    {
        // A cutscene or a conversation owns the character: it cannot move, and the mesh is not
        // dependable while one plays — a step failed with "navmesh not ready" for no reason but
        // that. None of the clocks should run through it, including the overall one, because the
        // step is not being attempted.
        if (_world.IsOccupied)
        {
            // The quest chain can roll straight into the hand-in conversation without any
            // travelling — Clutch and Kin's join choice opened off the last objective, and the
            // move phase sat behind it with every clock frozen while the choice sat unanswered.
            // A choice window during an accept or turn-in IS the step: join it.
            if (step.Kind is StepKind.CompleteQuest or StepKind.AcceptQuest
                && (_world.IsAddonVisible("SelectString") || _world.IsAddonVisible("SelectYesno")
                    || _world.IsAddonVisible("SelectIconString") || _world.IsAddonVisible("JournalResult")))
            {
                _sawOccupied = true;
                Enter(Phase.Dialogue);
                return;
            }
            _phaseStart = now;
            _stepStart = now;
            _lastMoveIssue = now;
            return;
        }

        // A detour — walking to a merchant the craft turned out to need — borrows this phase and
        // lands somewhere other than the step's own destination.
        var detour = _detourTo;
        var target = detour ?? step.Position!.Value;
        var tolerance = detour is null ? StopDistanceFor(step) : _detourTolerance;
        var distance = Vector3.Distance(_world.PlayerPosition, target);

        // A walk across a zone line arrives by changing zone, not by reaching the point.
        if (detour is null && step.TargetTerritoryId is { } targetTerritory && _world.TerritoryId == targetTerritory)
        {
            _world.StopMoving();
            Enter(Phase.WaitReady);
            return;
        }

        // A detour to a shard is finished by the game's own answer as readily as by the distance:
        // the menu can be open while a range measured from the object's origin still reads as far.
        if (_stalledSince == default)
        {
            _stalledSince = now;
            _closestSeen = distance;
        }

        var arrived = distance <= tolerance + ArrivalSlack
                      || (_detourNeedsShard && _world.AtAethernetShard);
        // A flight that ends hanging over its mark never arrives: the walk-into rings fire for
        // someone standing in them, and the mark's own Y is the ground. Horizontally there and
        // above it — land, then judge arrival from the ground.
        if (!arrived && detour is null && _world.IsInFlight
            && _world.PlayerPosition.Y > target.Y
            && Vector3.Distance(target with { Y = _world.PlayerPosition.Y }, _world.PlayerPosition) <= tolerance + ArrivalSlack)
        {
            _world.StopMoving();
            _world.Log($"Flight ended {_world.PlayerPosition.Y - target.Y:F0}y above the mark {Fmt(target)} — landing.");
            Enter(BeginDismount(Phase.Move));
            return;
        }

        // A fight is entered on foot: close enough to the combat mark, land (a dismount from the
        // air is the game's own descent), and walk the rest. Pulling from the saddle does
        // nothing, and a flight that circles the mark hunting the exact yalm never fights.
        if (!arrived && detour is null && step.Kind == StepKind.Combat && _world.IsMounted
            && !_combatLanded && distance <= CombatLandRadius)
        {
            _combatLanded = true;
            _world.StopMoving();
            _world.Log($"Within {distance:F0}y of the fight — landing to finish on foot.");
            Enter(BeginDismount(Phase.Move));
            return;
        }
        // The mark is where the recording stood; the step's business is the object. Within
        // interact reach of the thing itself is arrival, however far the mark sits — marks get
        // recorded from mid-dismount, sit inside the object's own collision, or claim a spot the
        // world refuses by a yalm. The interact phase re-measures for itself either way.
        if (!arrived && detour is null && _world.TerritoryId == step.TerritoryId && step.DataId is { } objectId
            && IsObjectStep(step.Kind) && _world.PositionOfDataId(objectId) is { } objectAt)
        {
            var flat = objectAt with { Y = _world.PlayerPosition.Y };
            // Horizontal reach with a forgiving vertical: a flight that ends hovering over the
            // object has arrived — the dismount on the way in is a descent, and that is what
            // pins the interaction to the floor.
            arrived = Vector3.Distance(flat, _world.PlayerPosition) <= InteractReach
                      && Math.Abs(objectAt.Y - _world.PlayerPosition.Y) <= HoverAboveObject;
        }
        if (arrived)
        {
            _world.StopMoving();
            var next = detour is null ? Phase.WaitReady : _detourThen;
            _detourTo = null;
            _detourNudged = false;
            _detourNeedsShard = false;
            Enter(next);
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
            // A mesh still building is a wait, not a fault: a fresh zone takes far longer than the
            // stall clock, which is there for a mesh that is not coming at all. Progress resets it,
            // the same way an active handoff does, and MoveTotal above remains the backstop.
            if (_world.NavmeshBuildProgress >= 0f)
            {
                _phaseStart = now;
                _stepStart = now; // a build is not this step failing to move; hold its clock too
                return;
            }
            if (now - _phaseStart > MoveStall)
                Fail("navmesh not ready");
            return;
        }

        if (_world.IsMoving)
        {
            _lastMoveIssue = now;

            // Moving but not getting anywhere: running into scenery the mesh thinks is passable, or
            // caught on the lip of something. A jump clears most of it, and it is what a person does
            // without thinking. Once per stall, with a long gap, so a genuinely slow leg is not
            // turned into a pogo stick.
            if (distance < _closestSeen - StallProgress)
            {
                _closestSeen = distance;
                _stalledSince = now;
            }
            else if (now - _stalledSince > StallJumpAfter && now - _lastStallJump > StallJumpGap)
            {
                _lastStallJump = now;
                _stalledSince = now;
                _world.Log($"Not getting any closer to {Fmt(target)} ({distance:F1}y for {StallJumpAfter.TotalSeconds:F0}s) — jumping.");
                _world.SendChatCommand("/generalaction Jump");
            }
            return;
        }

        // Not moving and not there. Either we have not asked yet, or the path ended short.
        if (_lastMoveIssue != default && now - _lastMoveIssue < PathSettle)
            return; // give the pathfinder a beat before judging it

        // A pathfind that came back with no waypoints used to end the step outright. It is a real
        // signal but not a reliable one — a mesh still loading, or a fresh area, answers zero for a
        // moment — so it is asked again before it is believed. Exhausting the retries is still far
        // quicker than waiting out the three-minute movement timeout, which is why the check exists.
        // The mesh has done all it can and we are nearly there: close the gap directly. Only for a
        // detour, where the target is an object the mesh cannot path onto rather than a step's own
        // destination, and only once — a second failure is a real one.
        if (detour is not null && !_detourNudged && distance <= DetourNudgeDistance)
        {
            _detourNudged = true;
            _lastMoveIssue = now;
            _world.Log($"Mesh path to {Fmt(target)} ended {distance:F1}y short; walking the rest directly.");
            _world.MoveDirectTo(target, false);
            return;
        }

        // While diving, every move is a volume move — the ground mesh has nothing down here.
        var fly = (step.Fly && _world.CanFlyHere && (!_groundOnly || _flyFallback) && !_combatLanded)
                  || _world.IsDiving;

        // The mesh answered nothing and we are standing still. Before asking again: a destination
        // that is simply off the mesh — an NPC's platform painted non-walkable is the usual shape,
        // Hamujj Gah's among them — is reached by pathing to the nearest point the mesh does reach
        // and walking the rest on foot. A genuine "no route" snaps to nothing, or to a point no
        // nearer than here, and keeps its failure below.
        if (!direct && _moveRetries > 0 && _world.PathWaypointCount == 0)
        {
            // Off-mesh feet: the pathfind cannot start from here, however good the destination.
            // The last step's direct walk onto Hamujj Gah's platform leaves us exactly so; the
            // mesh's edge is a yalm or two away. Step onto it, then ask again.
            if (!_footingTaken
                && _world.NearestReachablePoint(_world.PlayerPosition, OffMeshFootingRange) is { } footing
                && Vector3.Distance(footing, _world.PlayerPosition) > OffMeshFeet)
            {
                _footingTaken = true;
                _lastMoveIssue = now;
                _world.Log($"Standing {Vector3.Distance(footing, _world.PlayerPosition):F1}y off the mesh — stepping onto it before pathing to {Fmt(target)}.");
                _world.MoveDirectTo(footing, false);
                return;
            }
            // Close enough to walk blind: do that first. It is also what gets us off a platform
            // the mesh disowns — a pathfind cannot start from off-mesh feet even when the mesh's
            // edge is a yalm away, and the nearest-point query will not say so.
            if (!_offMeshNudged && distance <= OffMeshDirectMax)
            {
                _offMeshNudged = true;
                _lastMoveIssue = now;
                _world.Log($"Walking the last {distance:F1}y to {Fmt(target)} directly — the mesh gave no path.");
                _world.MoveDirectTo(target, false);
                return;
            }
            if (_offMeshSnap is null
                && _world.NearestReachablePoint(target, OffMeshSnapRange) is { } snap
                && Vector3.Distance(snap, target) > ArrivalSlack          // the mesh reaches the target itself: a snap is just the same ask again
                && Vector3.Distance(snap, _world.PlayerPosition) > ArrivalSlack)
            {
                _offMeshSnap = snap;
                _lastMoveIssue = now;
                _world.Log($"{Fmt(target)} is off the mesh; going to the nearest point it reaches " +
                           $"({Vector3.Distance(snap, target):F1}y short) and walking the rest.");
                _world.MoveTo(snap, fly);
                return;
            }
        }

        if (_moveRetries >= MaxMoveRetries)
        {
            // Giving up while still in the air is premature: a hover is fat and snags on lips
            // and rings a walker slips past — Clutch and Kin's ring sat 2.9y away, level, for
            // ten minutes of hover. Land once and run the attempts again on foot.
            if (_world.IsInFlight && !_landedToFinish)
            {
                _landedToFinish = true;
                _moveRetries = 0;
                _offMeshNudged = false;
                _footingTaken = false;
                _world.Log($"Still in flight {distance:F1}y from {Fmt(target)} — landing to finish on foot.");
                Enter(BeginDismount(Phase.Move));
                return;
            }
            // A waypoint, not a target: a WalkTo that the world will not let us finish — three
            // yalms from the mark with the pathfinder silent and a straight walk stalled — has
            // done its job, which was to get us *here*. The step that needs exactness is the next
            // one, and it measures from its own target.
            if (step.Kind == StepKind.WalkTo && detour is null && distance <= WalkToNearEnough)
            {
                _world.Log($"Ended {distance:F1}y short of the mark {Fmt(target)} and can get no closer — near enough for a waypoint. "
                    + $"(standing at {Fmt(_world.PlayerPosition)}, {(_world.IsInFlight ? "in flight" : _world.IsMounted ? "mounted" : "on foot")})");
                _world.StopMoving();
                Enter(Phase.WaitReady);
                return;
            }
            // The ground has no route and the path itself says to fly. The ground-only rule for
            // allied-society runs in old zones is a preference; a leg that cannot be walked at
            // all — a fenced camp, a cave with a doorway the mesh does not span — yields to the
            // path's own answer. One leg, not the run.
            if (!_flyFallback && _groundOnly && step.Fly && _world.CanFlyHere)
            {
                _flyFallback = true;
                _world.Log($"The ground mesh has no route to {Fmt(target)} and the path says to fly — flying this leg.");
                Enter(_world.IsMounted ? Phase.Move : Phase.Mount);
                return;
            }
            // Both ends on the mesh and still nothing: the mesh is lying about the world — built
            // before a quest opened a gate, usually. Rebuild it once and start the attempts over;
            // the not-ready wait above holds the clocks while it builds.
            if (!direct && !_meshRebuilt && _world.PathWaypointCount == 0 && MeshDiagnosis(target).Length == 0
                && _world.RebuildNavmesh())
            {
                _meshRebuilt = true;
                _moveRetries = 0;
                _lastMoveIssue = now;
                _offMeshNudged = false;
                _footingTaken = false;
                _world.Log($"The mesh reaches both here and {Fmt(target)} yet gives no path — it predates the world's current shape. Rebuilding it, then trying again.");
                return;
            }
            Fail(!direct && _world.PathWaypointCount == 0
                ? $"no path to {Fmt(target)} after {_moveRetries} attempts{MeshDiagnosis(target)}"
                : $"stalled {_moveRetries} times short of {Fmt(target)} ({distance:F1}y left)");
            return;
        }

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
    /// Why the mesh gave nothing — asked only once it is being given up on. "No path" from a mesh
    /// that says it is ready has two usual causes, and they want different hands: a mesh that does
    /// not cover where we stand is vnavmesh holding a stale one (every walk in the zone fails the
    /// same way, and a rebuild fixes it); a destination nothing reaches is the data's, or a door's.
    /// </summary>
    private string MeshDiagnosis(Vector3 target)
    {
        if (_world.NearestReachablePoint(_world.PlayerPosition, 3f) is null)
            return " — the loaded navmesh does not cover where you stand, so it is probably stale for this zone: /vnav rebuild, then Retry";
        if (_world.NearestReachablePoint(target, 3f) is null)
            return " — the mesh has no route from here to there (off the mesh, or behind a door or zone line)";
        return string.Empty;
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
        _buyItem = item;
        if (_world.ItemCount(item) >= _buyTarget)
            return Phase.Finish;

        _shopId = step.PurchaseShopId ?? 0;
        if (!_world.IsShopOpen(_shopId))
        {
            if (step.DataId is not { } named)
            {
                Fail("PurchaseItem step names no vendor");
                return Phase.None;
            }
            _vendorDataId = named;
        }
        return Phase.Shop; // that phase opens it, retries, and resolves an unnamed shop's id
    }

    /// <summary>
    /// Buy the shortfall, re-read the bag, buy again if it is still short. Re-planning each round
    /// off the live count rather than trusting one order is what makes a partly-filled purchase
    /// converge instead of double-buying — the same shape the delivery runner uses for ingredients.
    /// </summary>
    private void TickPurchase(QuestStep step, DateTime now)
    {
        var item = _buyItem;
        var held = _world.ItemCount(item);

        if (held >= _buyTarget)
        {
            _world.CloseShop();
            if (!_shopThenCraft)
            {
                Enter(Phase.Finish);
                return;
            }
            // The materials changed, so the last "Artisan made nothing" is stale — clearing it is
            // what lets the craft be attempted again instead of being judged on the old attempt.
            _craftAsked = 0;
            _craftHeldAtAsk = 0;
            Enter(Phase.Craft);
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
            if (_shopThenCraft) { Enter(Phase.Craft); return; }
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

        if (_world.ItemCount(item) >= want)
        {
            if (_craftAsked != 0 && _world.IsCrafting)
                _world.StopCrafting();
            Enter(Phase.Finish);
            return;
        }

        if (_world.IsCrafting)
        {
            _phaseStart = now; // it is working; the idle clock only runs while nothing happens
            return;
        }

        if (!_world.CrafterReady)
        {
            Fail($"{want - _world.ItemCount(item)} × item {item} needs crafting and Artisan is not loaded — " +
                 "make them yourself, then Retry");
            return;
        }

        // Artisan is idle. If the last thing we asked for did not arrive, the materials for it ran
        // out — that is the end of the line, and the shortfall says what is missing.
        // Nothing arrived — but only once Artisan has had time to start. Between the ask and its
        // endurance loop reporting itself there is a window where "not crafting" means "not yet".
        if (_craftAsked != 0 && now - _phaseStart > CraftStartGrace
            && _world.ItemCount(_craftAsked) <= _craftHeldAtAsk)
        {
            // Artisan produced nothing, so the materials ran out. A character new to the class has
            // none of them, which is the ordinary case rather than the exceptional one — so if a
            // merchant here sells what is missing, go and buy it instead of stopping.
            if (TryBuyMaterials(item, want))
                return;

            var short_ = want - _world.ItemCount(item);
            var missing = _world.CraftShortfall(item, short_);
            // No shortfall means the materials are all there and something else stopped it — the
            // recipe's level, or Artisan not being able to reach the log. Saying "stock up" there
            // would send you looking for materials you already have.
            Fail($"Artisan stopped with {short_} × item {item} still to make" +
                 (missing.Count > 0
                     ? $" — short of {Describe(missing)}. Get those, then Retry"
                     : ", and the materials are all there — check the recipe's level and that Artisan can craft it"));
            return;
        }

        // Nothing craftable is left to try: the item has no recipe, or what it is short of has to
        // be bought or gathered rather than made.
        // Something is already in flight; leave it alone until the grace above has run out.
        if (_craftAsked != 0 && now - _phaseStart <= CraftStartGrace)
            return;

        if (_world.NextCraft(item, want) is not { } next)
        {
            if (TryBuyMaterials(item, want))
                return;
            var missing = _world.CraftShortfall(item, want - _world.ItemCount(item));
            Fail($"no recipe for item {item}, or its materials cannot be crafted" +
                 (missing.Count > 0 ? $" — short of {Describe(missing)}" : "") + ". Buy or gather the rest, then Retry");
            return;
        }

        // Sampled before the ask, not after: this is the baseline that answers "did anything
        // actually arrive", and reading it afterwards would compare the result against itself.
        var heldBefore = _world.ItemCount(next.ItemId);
        if (_world.StartCraft(next.ItemId, next.Count) is not { } job)
        {
            Fail($"no recipe for item {next.ItemId}, or Artisan would not take the craft");
            return;
        }
        _craftAsked = next.ItemId;
        _craftHeldAtAsk = heldBefore;
        _phaseStart = now;
        _world.Log(next.ItemId == item
            ? $"Asked Artisan for {next.Count} × item {item} as {job}."
            : $"Asked Artisan for {next.Count} × item {next.ItemId} first — item {item} is made from it.");
    }

    /// <summary>
    /// Buy a base material the craft is short of, from a merchant standing here.
    ///
    /// <para>
    /// The path data assumes you already own the materials, which is true of a character who has
    /// run the class before and false of the one these quests are written for. Every crafting guild
    /// keeps its material vendor beside the guildmaster, so the shop is usually a few paces away —
    /// and this only ever buys from one already in reach, because a shop cannot be opened across a
    /// zone.
    /// </para>
    ///
    /// <para>
    /// Each material is bought at most once per step. A second failure after buying means something
    /// other than the shopping is wrong, and looping between the shop and the crafting log would
    /// hide that.
    /// </para>
    /// </summary>
    private bool TryBuyMaterials(uint item, int want)
    {
        foreach (var missing in _world.CraftShortfall(item, want - _world.ItemCount(item)))
        {
            if (!_boughtForCraft.Add(missing.ItemId))
                continue; // already tried this one
            if (_world.VendorNearbyFor(missing.ItemId) is not { } vendor)
                continue;

            _buyTarget = _world.ItemCount(missing.ItemId) + missing.Missing;
            _buyItem = missing.ItemId;
            _shopId = vendor.ShopId;
            _vendorDataId = vendor.VendorDataId;
            _shopThenCraft = true;
            _lastShopOpen = default;
            _world.Log($"Short of {missing.Missing} × {missing.Name} — buying from {vendor.VendorName}.");

            // Being in the object table is not being in reach: a merchant across the guild hall is
            // visible and still too far to talk to, which is what made the first attempt stand
            // still. Walk over unless already beside them.
            if (_world.PositionOfDataId(vendor.VendorDataId) is { } where
                && Vector3.Distance(_world.PlayerPosition, where) > DefaultStopDistance + ArrivalSlack)
            {
                _detourTo = where;
                _detourThen = Phase.Shop;
                _detourTolerance = DefaultStopDistance;
                Enter(Phase.Move);
                return true;
            }
            Enter(Phase.Shop);
            return true;
        }
        return false;
    }

    private static string Describe(System.Collections.Generic.IReadOnlyList<MaterialShortfall> missing)
    {
        var parts = new string[missing.Count];
        for (var i = 0; i < missing.Count; i++)
            parts[i] = $"{missing[i].Missing} × {missing[i].Name}";
        return string.Join(", ", parts);
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
    /// Wear what the step names — or, when it is a class tool, be the class by any means.
    ///
    /// <para>
    /// These steps come from the quest that unlocks a class, where the point of the weathered
    /// hammer is that you own no Goldsmith tool at all. On a character who already plays the class
    /// that premise is simply false: the tool may have been sold years ago, and equipping it would
    /// be a downgrade even if it were still there. The game changes class off the main hand, so the
    /// gearset satisfies the step exactly as the quest item would — the same swap Artisan makes to
    /// reach a recipe's job.
    /// </para>
    ///
    /// <para>
    /// The fallback is deliberately narrow. It applies only to a main hand naming a single class,
    /// never to gear that merely happens to be restricted — for those the item <i>is</i> the
    /// requirement and no gearset stands in for it.
    /// </para>
    /// </summary>
    private Phase BeginEquip(QuestStep step)
    {
        if (step.ItemId is not { } wear)
        {
            Fail("EquipItem step names no item");
            return Phase.None;
        }
        if (_world.IsEquipped(wear))
            return Phase.Finish;

        var toolClass = _world.EquipClassOf(wear);

        // Already that class: a tool for it is in your hand, which is all the step was ever after.
        if (toolClass is { } already && _world.CurrentClassJob == already)
            return Phase.Finish;

        if (_world.EquipItem(wear))
            return Phase.Equip;

        // Not in the bags or the armoury. For a class tool that is recoverable.
        if (toolClass is not { } job)
        {
            Fail($"item {wear} could not be equipped — not equipment, or not in the bags or armoury");
            return Phase.None;
        }
        if (GearsetFor(job) is not { } set)
        {
            Fail($"item {wear} is not held and there is no gearset for its class — save one, then Retry");
            return Phase.None;
        }
        if (_world.InCombat)
        {
            Fail("cannot change class in combat");
            return Phase.None;
        }
        if (!_world.EquipGearset(set.Id))
        {
            Fail($"gearset {set.Id} was refused");
            return Phase.None;
        }
        _switchTarget = set.ClassJobId;
        _world.Log($"Item {wear} is not held; equipping gearset {set.Id} for its class instead.");
        return Phase.ClassSwitch;
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
        if (Same(target, "ConfiguredCombatJob"))
            return (Highest(_world.Gearsets(), JobKind.Combat), "no combat gearset exists — save one, then Retry");
        if (Same(target, "ConfiguredCraftingJob"))
            return (Highest(_world.Gearsets(), JobKind.Crafter), "no crafting gearset exists — save one, then Retry");

        var startJob = Same(target, "QuestStartJob");
        var wanted = startJob ? _world.QuestStartClassJob(_questId) : _world.ResolveClassJob(target);
        if (wanted is not { } job || job == 0)
            return (null, startJob
                ? $"quest {_questId} does not say which class it was accepted on"
                : $"unknown class \"{target}\" in the path data");

        return (GearsetFor(job), $"no gearset for {target} — save one, then Retry");
    }

    /// <summary>
    /// The gearset for a class. A job satisfies the class it grew out of, so a Conjurer request
    /// takes the White Mage gearset; highest level wins when several match, which keeps the one
    /// actually played.
    /// </summary>
    private GearsetInfo? GearsetFor(uint job)
    {
        GearsetInfo? best = null;
        foreach (var s in _world.Gearsets())
            if ((s.ClassJobId == job || s.ParentClassJobId == job) && (best is null || s.Level > best.Level))
                best = s;
        return best;
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
            // A hand-in chain left open by the previous quest — its reward window, the overcap
            // warning — occupies the player, and for an accept or turn-in that conversation IS
            // the step: join it and let the dialogue machinery answer it, rather than waiting
            // behind it for thirty seconds and calling the player not ready. The third Kobold
            // hand-in of the day sat exactly there, one Yes away from done.
            if (_world.IsOccupied && step.Kind is StepKind.CompleteQuest or StepKind.AcceptQuest)
            {
                _sawOccupied = true;
                Enter(Phase.Dialogue);
                return;
            }
            if (now - _phaseStart > ReadyWait)
                Fail("player never became ready to interact");
            return;
        }

        // Asked to get off the mount and still on it — a dismount from the air is a descent, and it
        // was pressing again halfway down that wasted both retries. Wait for the ground, asking
        // again as it goes, and let the phase clock be the limit.
        if (_dismountAsked && _world.IsMounted)
        {
            if (now - _lastDismountTry > DismountRetry)
            {
                _lastDismountTry = now;
                _world.Dismount();
            }
            if (now - _phaseStart > ReadyWait)
                Fail($"could not get off the mount to interact with {dataId}");
            return;
        }
        if (_dismountAsked && !DismountSettled(now))
            return; // off the mount but the animation is still playing; a press now is eaten
        _dismountAsked = false;

        if (!_world.IsDataIdSpawned(dataId))
        {
            if (now - _phaseStart > ReadyWait)
            {
                TargetMissing = true;
                Fail($"object {dataId} never appeared");
            }
            return;
        }

        _world.FaceDataId(dataId);
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
            AnswerDialogue(step, now);
        }

        // A capped reward: the game warns that not everything will be received and waits. The
        // run proceeding without the excess is the whole point of automating the day — and the
        // toggle exists for whoever disagrees. Matched against the game's own warning strings,
        // never any other question.
        if (_world.IsAddonVisible("SelectYesno") && _acceptOvercap()
            && now - _lastOvercapYes > TimeSpan.FromSeconds(1) && _world.ConfirmOvercapDialog())
        {
            _lastOvercapYes = now;
            _sawOccupied = true;
            _phaseStart = now;
            _world.Log("Reward overcap warning — proceeding without the excess (Settings has the toggle).");
            return;
        }

        // The multi-quest hand-in menu: an issuer holding several finished dailies asks which one,
        // and this step's quest is the answer. Left unanswered, the CompleteQuest step reports its
        // dialogue over with the menu still up, the quest never completes, and the sequence sits at
        // "all steps done, waiting for the game" for ever.
        if (_world.IsAddonVisible("SelectIconString"))
        {
            if (!_iconAnswered)
            {
                var entries = _world.SelectIconStringEntries();
                if (entries.Count > 0)
                {
                    var name = _world.QuestName(_questId);
                    var index = name is null ? -1 : FindEntry(entries, name);
                    if (index >= 0)
                    {
                        _world.SelectIconStringIndex(index);
                        _iconAnswered = true;
                        _sawOccupied = true;   // a menu opened: this is a live conversation
                        _phaseStart = now;
                    }
                    else if (!_iconReported)
                    {
                        _iconReported = true;
                        _world.Log($"The hand-in menu does not list \"{name ?? _questId.ToString()}\" — " +
                                   $"[{string.Join(" | ", entries)}]; leaving it for you.");
                    }
                }
            }
            return; // the menu is up; nothing settles while it stands
        }
        _iconAnswered = false;

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

        // Nothing opened at all. The interaction did not take — a sprint keybind firing on the same
        // frame will do it, and so will an NPC turned away at the wrong moment. Ask again rather
        // than report a conversation that never happened: the alternative is every step of this
        // kind finishing "successfully", the sequence not moving, and the block being replayed
        // twenty seconds later to do the same thing.
        if (!_sawOccupied && step.DataId is { } target && _interactRetries < MaxInteractRetries)
        {
            _interactRetries++;

            // Airborne is the commonest way this fails and the one that never recovers on its own:
            // the path data flies you to the NPC, the flight ends above their head, and every
            // interact from up there does nothing. Land first — the walk below then has somewhere
            // to start from.
            if (_world.IsMounted)
            {
                _world.Log($"Nothing opened after interacting with {target} — dismounting first.");
                _dismountAsked = true;
                _lastDismountTry = now;
                _dismountedAt = default;
                _world.Dismount();
            }

            // Out of reach means the keypress was never going to land, and pressing it again from
            // the same spot will not change that. Close on the object itself — its own position,
            // not the one the step was recorded at, which is what put us out of reach.
            if (_world.DistanceToDataId(target) is { } distance && distance > InteractReach
                && _world.PositionOfDataId(target) is { } where)
            {
                _world.Log($"Nothing opened after interacting with {target} — {distance:F1}y away, " +
                           $"walking to it before asking again ({_interactRetries}/{MaxInteractRetries}).");
                _detourTo = where;
                _detourThen = Phase.Interact;
                _detourTolerance = InteractReach - ArrivalSlack;
                _detourNudged = false;
                _detourNeedsShard = false;
                Enter(Phase.Move);
                return;
            }

            _world.Log($"Nothing opened after interacting with {target} — asking again ({_interactRetries}/{MaxInteractRetries}).");
            Enter(Phase.Interact);
            return;
        }

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

    /// <summary>
    /// Answer whatever the conversation is asking.
    ///
    /// <para>
    /// Most of these are named by the step, but not all of them: quest 2601's "ask the townspeople"
    /// sequence opens a "what will you say?" menu at every one of its three NPCs and the path data
    /// declares none of them. TextAdvance does not pick list entries either, so the menu simply sat
    /// there — the player stays occupied, the dialogue never ends, and the step never finishes. The
    /// run got to the first NPC of that quest and no further.
    /// </para>
    ///
    /// <para>
    /// So an undeclared list is answered too, taking the first entry after a grace long enough for
    /// TextAdvance or you to get there first. These asides are flavour: the wording changes what is
    /// said back, not what happens. The choices that decide something — taking up a class, taking on
    /// a first DoH or DoL quest — are YesNo, and those are answered only where the step names them.
    /// </para>
    /// </summary>
    private void AnswerDialogue(QuestStep step, DateTime now)
    {
        DialogueChoice? listChoice = null;
        foreach (var choice in (System.Collections.Generic.IEnumerable<DialogueChoice>?)step.DialogueChoices ?? Array.Empty<DialogueChoice>())
        {
            if (choice.Type.Equals("YesNo", StringComparison.OrdinalIgnoreCase) && _world.IsAddonVisible("SelectYesno"))
                _world.SelectYesNo(choice.Yes ?? true);
            else if (choice.Type.Equals("List", StringComparison.OrdinalIgnoreCase))
                listChoice ??= choice;
        }

        // Either window asks the same question; a choice put mid-conversation uses the second.
        var listVisible = _world.IsAddonVisible("SelectString") || _world.IsAddonVisible("CutSceneSelectString");
        if (!listVisible)
        {
            _listAnswered = false; // a new list later in the same interaction gets its own answer
            _listOpenedAt = default;
            return;
        }

        if (_listOpenedAt == default)
            _listOpenedAt = now;

        if (_listAnswered)
            return;

        var entries = _world.SelectStringEntries();
        if (entries.Count == 0)
            return; // the window is up but has not filled in yet

        if (listChoice is null)
        {
            if (now - _listOpenedAt < UndeclaredListGrace)
                return;
            _world.Log($"A list choice is open that quest {_questId} does not name — [{string.Join(" | ", entries)}]; taking the first option.");
            _world.SelectStringIndex(0);
            _listAnswered = true;
            return;
        }

        // The data carries text keys; the menu shows text. Resolve the answer key against the
        // quest's dialogue sheet and pick the entry that says it.
        var wanted = listChoice.Answer is { } key ? _texts?.Resolve(_questId, key) : null;
        var index = wanted is null ? -1 : FindEntry(entries, wanted);

        // Falling back to the first option rather than leaving the menu hanging, on the same
        // reasoning as an undeclared one: an answer is expected here and the wording is flavour.
        if (index < 0)
        {
            _world.Log(wanted is null
                ? $"List choice {listChoice.Answer ?? "?"} could not be resolved for quest {_questId}; taking the first option."
                : $"List choice \"{wanted}\" not among [{string.Join(" | ", entries)}]; taking the first option.");
            index = 0;
        }

        _world.Log($"List choice: picking {index + 1}/{entries.Count} \"{entries[index]}\" "
            + $"(key {listChoice.Answer ?? "-"} resolved to \"{wanted ?? "-"}\") from [{string.Join(" | ", entries)}]");
        _world.SelectStringIndex(index);
        _listAnswered = true;
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
            if (!_inFight)
            {
                // A new fight. Stop any approach still running so we do not jog past the mob,
                // and count it — MinimumKillCount is paid in fights, one mob to a pull.
                _inFight = true;
                _fights++;
                _world.StopMoving();
            }
            _sawCombat = true;
            _lastCombatSeen = now;
            _phase = Phase.Combat;
            return; // Daedalus is fighting; our only job is to not walk away.
        }
        _inFight = false;

        if (now - _stepStart > CombatMax)
        {
            Fail("combat did not resolve in time");
            return;
        }

        // Out of combat. Anything left to pull? Named overworld mobs roam: the Banestools stood
        // in plain sight past the thirty-yalm ring while the step waited at the mark for nothing.
        // With ids to look for, hunt as far as the object table sees and walk to the nearest;
        // the tight ring stays for unnamed pulls, where wide means someone else's mobs.
        // With ids in hand the hunt is safe at range whatever spawned them — an ambush that
        // triggered on the fly-over can stand well off the mark by the time we land and walk in.
        var radius = enemies.Count > 0 ? OverworldHuntRadius : CombatSearchRadius;
        if (_world.AttackNearestEnemy(enemies, radius))
        {
            _phase = Phase.Combat;
            return;
        }

        if (_sawCombat)
        {
            // Fought and it is quiet now. A step that wants more kills than there were mobs
            // waits here for the respawn — the clock above is the limit — and the sequence
            // advancing ends it sooner if the game is already satisfied.
            if (step.MinimumKillCount is { } wanted && _fights < wanted)
                return;
            // Otherwise give stragglers a moment to spawn, then call it.
            if (now - _lastCombatSeen > CombatClearSettle)
                Enter(Phase.Finish);
            return;
        }

        // Never fought. Enemies that spawn on arrival can take a few seconds; enemies that were
        // meant to be found may simply not be here (already dead, or the flags are already set).
        // Optional combat has nothing to wait for: if the leftovers were here, the pull above
        // would have taken them.
        if (step.EnemySpawnType == EnemySpawnType.FinishCombatIfAny || now - _phaseStart > CombatSpawnWait)
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
            _offMeshSnap = null;
            _offMeshNudged = false;
            _footingTaken = false;
            _meshRebuilt = false;
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
