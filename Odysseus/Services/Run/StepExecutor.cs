using System;
using System.Collections.Generic;
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
        None, Delay,
        /// <summary>The game is still moving the character — a cutscene, a zone load, a conversation. Travel waits for it to finish.</summary>
        Settle,
        Teleport, TeleportWait, Aethernet, AethernetWait,
        /// <summary>Walked toward a shard the map places; look again now it should be in view.</summary>
        AethernetApproach,
        /// <summary>The crafted item needs materia melded before the quest will take it.</summary>
        Meld,
        /// <summary>At an aetheryte or shard, interacting until the game says it is attuned.</summary>
        Attune,
        /// <summary>At a city lift attendant, riding to the level the step is on.</summary>
        Lift,
        /// <summary>At the door into a zone with no aetheryte (the Rising Stones), going through it.</summary>
        Door,
        /// <summary>Beastmaster: the pet onto its battlehorn and summoned, before the interact.</summary>
        Battlehorn,
        Mount, Move, WaitReady, Interact, Dialogue,
        CombatWait, Combat,
        /// <summary>Solo instance: interacted, waiting to be inside.</summary>
        SoloDutyEnter,
        /// <summary>Solo instance: inside, BossMod AI has it, waiting to be out.</summary>
        SoloDutyRun,
        /// <summary>Solo instance: back outside — did the quest actually move, or was it lost?</summary>
        SoloDutyVerify,
        /// <summary>Solo instance: lost too many times; staying put until it is won by hand.</summary>
        SoloDutyHold,
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
        /// <summary>Holding until the step's NPC stands at the position it is walking to.</summary>
        NpcWait,
        /// <summary>Holding on a path note: the player has something to do that we cannot.</summary>
        Instruction,
        /// <summary>Holding while the player runs a duty we do not: the quest moving on releases it.</summary>
        DutyByHand,
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

    /// <summary>
    /// A leg longer than this flies even when the path says walk. Mnemosyne's calibrated
    /// planner speeds — 6 y/s on the ground, 20 in the air — put the break-even far lower;
    /// this saves fourteen seconds or more, enough to be worth a take-off and a landing.
    /// </summary>
    public const float FlyWorthDistance = 120f;
    private const float WalkSpeed = 6f;
    private const float AirSpeed = 20f;

    /// <summary>How often a refused teleport is asked again, and for how long before faulting.</summary>
    private static readonly TimeSpan TeleportRetryEvery = TimeSpan.FromSeconds(2);

    /// <summary>How long a duty left to the player waits outside the duty before it gives up.</summary>
    private static readonly TimeSpan DutyByHandMax = TimeSpan.FromHours(2);

    /// <summary>How long a path note waits for the player before it gives up on them.</summary>
    private static readonly TimeSpan InstructionMax = TimeSpan.FromMinutes(15);

    /// <summary>How long a waited-for NPC gets to walk to their spot.</summary>
    private static readonly TimeSpan NpcWaitMax = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TeleportGiveUp = TimeSpan.FromSeconds(12);

    /// <summary>How close "arrived" is when the step does not say. Interact range is ~7y; 3 keeps us clearly inside it.</summary>
    public const float DefaultStopDistance = 3f;
    /// <summary>WalkTo without a StopDistance: land on the point.</summary>
    public const float WalkToStopDistance = 0.5f;

    /// <summary>Close enough to a combat mark to land and finish the approach on foot.</summary>
    private const float CombatLandRadius = 15f;

    /// <summary>How high over a wedged mark the over-the-top escape climbs before descending.</summary>
    private const float OverTopClimb = 20f;

    /// <summary>How far above an object a flight may end and still count as arrived — the dismount descends the rest.</summary>
    private const float HoverAboveObject = 10f;

    /// <summary>
    /// A window that needs the dialogue machinery is up: for an accept or turn-in, any of the
    /// choice windows (the chain rolled ahead of us); for every step, the hand-over Request —
    /// it can only belong to the quest being run, and the fill-and-confirm lives in Dialogue.
    /// Kurobana's vinegar hand-over sat open while the move phase froze its clocks politely.
    /// </summary>
    private bool NeedsDialogueJoin(QuestStep step)
        => _world.IsAddonVisible("Request")
           || ((step.Kind is StepKind.CompleteQuest or StepKind.AcceptQuest
                || step.DialogueChoices is { Count: > 0 })
               && (_world.IsAddonVisible("SelectString") || _world.IsAddonVisible("SelectYesno")
                   || _world.IsAddonVisible("SelectIconString") || _world.IsAddonVisible("JournalResult")
                   || _world.IsAddonVisible(GameStepWorld.CutsceneChoice)));

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

    /// <summary>Fly every mounted leg where flying is unlocked (Settings). On by default.</summary>
    public bool FlyByDefault { get; set; } = true;

    /// <summary>On foot where no mount is allowed, a leg this long is worth a Sprint.</summary>
    private const float SprintWorthDistance = 25f;
    private static readonly TimeSpan SprintRetry = TimeSpan.FromSeconds(3);
    private DateTime _lastSprintTry;
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

    /// <summary>How long an action gets to come off cooldown before the step asks for a person.</summary>
    private static readonly TimeSpan ActionRecastMax = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RecastNoteEvery = TimeSpan.FromSeconds(15);
    private DateTime _lastRecastNote;

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
    /// <summary>The same grace for a yes/no the step does not name — but that one is never taken, only reported.</summary>
    private static readonly TimeSpan UndeclaredYesNoGrace = TimeSpan.FromSeconds(3);
    /// <summary>How often a yes/no answer is pressed again while its window is still standing.</summary>
    private static readonly TimeSpan YesNoRetry = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DialogueMax = TimeSpan.FromSeconds(120);
    /// <summary>How long the reward window may sit before we press Complete ourselves — TextAdvance gets first go.</summary>
    private static readonly TimeSpan RewardWindowGrace = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan RewardCompleteRetry = TimeSpan.FromSeconds(1.5);
    /// <summary>Same courtesy for the hand-over window: TextAdvance fills and presses it first if it is holding.</summary>
    private static readonly TimeSpan HandOverGrace = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan HandOverRetry = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan CombatSpawnWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CombatClearSettle = TimeSpan.FromSeconds(3);

    /// <summary>How long an arrival-spawn fight waits before creeping onto the exact mark.</summary>
    private static readonly TimeSpan CombatCreepAfter = TimeSpan.FromSeconds(4);

    /// <summary>"Moving" with the position frozen this long is a wedge, not a walk.</summary>
    private static readonly TimeSpan FrozenStallLimit = TimeSpan.FromSeconds(12);

    /// <summary>A mid-air dismount still airborne after this long is a descent with no floor.</summary>
    // A three-yalm descent touches down in a second or two; five is a descent that will not.
    private static readonly TimeSpan BlockedDescentAfter = TimeSpan.FromSeconds(5);

    /// <summary>Wedge re-paths before the leg is declared unservable and the fault says where.</summary>
    private const int MaxFrozenStops = 6;
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

    /// <summary>
    /// How long after coming out of a solo duty the quest is given to show it moved. The wrap-up
    /// cutscene holds this clock like every other; once the character can act again, this is only
    /// the server's word catching up.
    /// </summary>
    private static readonly TimeSpan SoloVerifySettle = TimeSpan.FromSeconds(4);


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

    /// <summary>The unnamed list this step already took the first option of — its entries, joined.</summary>
    private string? _unnamedListTaken;
    private DateTime _listOpenedAt;
    private DateTime _yesNoOpenedAt;
    private bool _yesNoReported;
    private bool _yesNoAnswered;

    /// <summary>How long the world must stay still before a travel decision trusts where the character is.</summary>
    private static readonly TimeSpan SettleGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to wait for the game to let go before deciding anyway. Cutscenes do not count
    /// against it — they hold the clock — so this is only a conversation or a menu left hanging.
    /// </summary>
    private static readonly TimeSpan SettleMax = TimeSpan.FromSeconds(30);

    private DateTime _settledSince;
    private bool _settleWaived;

    /// <summary>How often a quest item is tried again while its target is still ready for it.</summary>
    private static readonly TimeSpan CombatItemRetry = TimeSpan.FromSeconds(2);

    /// <summary>How often the hold on Daedalus is renewed — well inside its lease.</summary>
    private static readonly TimeSpan CombatHoldRenew = TimeSpan.FromSeconds(1);

    private DateTime _combatItemUsedAt;
    private DateTime _combatHoldAsserted;
    private bool _combatHoldOn;
    private bool _combatItemUnknownSaid;
    private bool _combatHoldRefusedSaid;
    private float _combatItemLoggedHealth = -1;

    private Quest.QuestSnapshot _soloProgressAtStart;
    private bool _soloHoldSaid;
    private string _soloHoldReason = string.Empty;
    private DateTime _lastDutyCallsYes;
    private DateTime _lastTravelYes;
    private DateTime _lastYesNoPress;

    /// <summary>
    /// Every dialogue choice in the sequence this step belongs to, set by the controller. A yes/no
    /// the data records against a neighbouring step is still this quest's own answer.
    /// </summary>
    public IReadOnlyList<DialogueChoice>? SequenceChoices { get; set; }
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
    private DateTime _lastOfferAccept;
    private bool _flyFallback;
    private bool _combatLanded;
    private bool _landedToFinish;
    private bool _creptToMark;
    private bool _interactFlew;
    private bool _detourFly;
    private Vector3 _frozenAt;
    private DateTime _frozenSince;
    private bool _descentRerouted;
    private bool _groundFallback;
    private bool _lastIssuedFly;
    private bool _wedgeFly;

    /// <summary>The leg is long enough that flying beats the authored walk. Sticky per step.</summary>
    private bool _farFly;

    /// <summary>The over-the-top escape has been spent for this step.</summary>
    private bool _overTop;
    private int _frozenStops;
    /// <summary>This detour ends at an aethernet stop, so the game can say when it is done.</summary>
    private bool _detourNeedsShard;

    /// <summary>This step already walked toward a shard from the map — a second look does not walk again.</summary>
    private bool _shardApproached;

    /// <summary>Close enough to a mapped shard for the object itself to be loaded and found.</summary>
    private const float ShardSightDistance = 40f;
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

    /// <summary>
    /// Which way past a wedge worked, by the mark it was reached at: true when the ground
    /// carried it, false when the air did. Yedlihmad's doorway cost a minute of climbing
    /// against the roof on every one of three visits; the second visit should know better.
    /// Instance-lived: a run's executor persists across its quests.
    /// </summary>
    private readonly Dictionary<(uint Territory, int X, int Y, int Z), bool> _wedgeMemory = new();
    private bool _wedgeMemoryUsed;
    private int _stallJumps;

    /// <summary>Where the character stood when the stall clock last started — moving off it is progress too.</summary>
    private Vector3 _stallAnchor;

    /// <summary>Moving this far in the stall window is not a stall, whatever the straight-line distance does.</summary>
    private const float StallMoveProgress = 2.5f;

    private (uint, int, int, int) WedgeKey(Vector3 target)
        => (_world.TerritoryId, (int)MathF.Round(target.X / 8f), (int)MathF.Round(target.Y / 8f), (int)MathF.Round(target.Z / 8f));

    /// <summary>
    /// The last AI state we commanded, so transitions send one chat command, not one per tick.
    /// BossMod's AI movement controller fights vnavmesh over off-mesh legs — it refuses ground
    /// it cannot path — so travel runs with it off and fights turn it back on.
    /// </summary>
    private bool? _aiCommanded;

    /// <summary>
    /// Readiness clocks do not tick against a cutscene: rolling onto the next quest while the
    /// last one's finale plays timed the accept out through no fault of the world's.
    /// </summary>
    private void HoldClockForCutscene(DateTime now)
    {
        if (_world.InCutscene)
            _phaseStart = now;
    }

    private bool _daedalusOffSaid;

    private void CommandAi(bool on)
    {
        if (_aiCommanded == on)
            return;
        _aiCommanded = on;
        _world.SetBossModAi(on);
    }
    private float _bestMoveDistance;
    private DateTime _lastMoveProgress;

    /// <summary>
    /// Gathering Odysseus does itself, when one is wired in and switched on — quest Gather
    /// steps prefer it over the GatherBuddy handoff, which needs the item on an auto-gather
    /// list the user must maintain by hand. Null (release builds today) changes nothing.
    /// </summary>
    public Services.Gathering.IOwnGatherer? OwnGatherer { get; set; }
    private bool _ownGatherAsked;
    private bool _ownGatherDeclineSaid;
    private DateTime _lastTeleportTry;
    private bool _dutyByHand;
    private string _byHandNote = string.Empty;
    private int _teleportAsks;
    private const int MaxTeleportAsks = 2;
    private bool _aetheryteListWarmed;
    private DateTime _aetheryteWarmedAt;
    private static readonly TimeSpan AetheryteWarmup = TimeSpan.FromMilliseconds(500);

    /// <summary>A teleport that took goes travel-busy within a second or two; past this, it did not.</summary>
    private static readonly TimeSpan TeleportStartGrace = TimeSpan.FromSeconds(4);
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
    public string PhaseName => _phase switch
    {
        Phase.Instruction when _step?.Comment is { Length: > 0 } note => $"Instruction — {note} (press Skip when done)",
        Phase.DutyByHand when _byHandNote.Length > 0 => $"Waiting for you — {_byHandNote}",
        _ => _phase.ToString(),
    };

    /// <param name="skipTeleport">The step's <c>AetheryteShortcutIf</c> holds — walk instead of teleporting.</param>
    /// <param name="questId">The quest this step belongs to — needed to resolve dialogue text keys. 0 for a bare step.</param>
    /// <param name="groundOnly">
    /// Ignore the step's <c>Fly</c> flag and walk. Set for an allied society path in a base-game
    /// zone, where the flight the data asks for catches on scenery.
    /// </param>
    public void Begin(QuestStep step, bool skipTeleport = false, ushort questId = 0, bool groundOnly = false, bool dutyByHand = false)
    {
        _soloHoldSaid = false;
        _soloHoldReason = string.Empty;
        _settledSince = default;
        _settleWaived = false;
        ReleaseCombatHold();
        _combatItemUsedAt = default;
        _combatItemUnknownSaid = false;
        _combatHoldRefusedSaid = false;
        _lastTravelYes = default;
        _combatItemLoggedHealth = -1;
        // A sniping section (Securing the Saltery and 31 others): interacting with the rifle starts
        // it, and it is shot from inside the event. The snipe skip (CBT's "Sniper no sniping",
        // ported) answers "hit" for it, switched on for the step and back off after; if a patch has
        // moved it the player takes the shots and the step waits for them. Run as the interact it
        // is, the stored path left alone.
        // Beastmaster: "Assign Cu Sith to first battlehorn, summon, then interact with J'yhuh Tia"
        // (The Wilds Call, 5491) is a manual step in the data. Run as the interact it ends in, with
        // the pet assigned and summoned first.
        _battlehorn = null;
        if (step.Kind == StepKind.WaitForManualProgress && step.DataId is not null && BattlehornAsk(step.Comment) is { } ask)
        {
            _battlehorn = ask;
            _battlehornAsked = false;
            _battlehornReady = false;
            _battlehornSummonedAt = default;
            _lastBattlehornTry = default;
            step = step.As(StepKind.Interact);
        }

        ReleaseAutoSnipe();
        _sniping = step.Kind == StepKind.Snipe;
        if (_sniping)
        {
            step = step.As(StepKind.Interact);
            switch (_world.AutoSnipeEnabled)
            {
                case false:
                    _world.SetAutoSnipe(true);
                    _autoSnipeSwitched = true;
                    _world.Log("Sniping section: skipping the shots.");
                    break;
                case true:
                    _world.Log("Sniping section: the shots are skipped already.");
                    break;
                default:
                    _world.Log("Sniping section and the snipe skip is unavailable this patch — take the shots yourself; the run carries on after.");
                    _world.Notify("Odysseus: a sniping section — take the shots yourself.");
                    break;
            }
        }
        _soloProgressAtStart = step.Kind == StepKind.SinglePlayerDuty ? _world.QuestState(questId) : default;
        _groundOnly = groundOnly;
        _step = step;
        _questId = questId;
        _listAnswered = false;
        _unnamedListTaken = null;
        _listOpenedAt = default;
        _yesNoOpenedAt = default;
        _yesNoReported = false;
        _yesNoAnswered = false;
        _lastYesNoPress = default;
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
        _ownGatherAsked = false;
        _ownGatherDeclineSaid = false;
        _craftAsked = 0;
        _craftHeldAtAsk = 0;
        _stepStart = _world.UtcNow;
        _daedalusOffSaid = false;
        _moveRetries = 0;
        _shardApproached = false;
        _hopSkipped = false;
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
        _creptToMark = false;
        _interactFlew = false;
        _detourFly = false;
        _frozenAt = default;
        _frozenSince = default;
        _descentRerouted = false;
        _groundFallback = false;
        _lastIssuedFly = false;
        _wedgeFly = false;
        _overTop = false;
        _farFly = false;
        _frozenStops = 0;
        _inFight = false;
        _fights = 0;
        _skipTeleport = skipTeleport;
        _dutyByHand = dutyByHand;
        _byHandNote = string.Empty;
        _teleportAsks = 0;
        _aetheryteListWarmed = false;
        // BossMod's AI movement controller refuses legs it cannot path ("off mesh") and fights
        // vnavmesh for the character. Travel belongs to us; the fight turns it back on below.
        CommandAi(false);
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

        // A door into another zone, with the character already through it. Gosetsu and Tsuyu (3070)
        // opens with Kugane's guard into the Ruby Bazaar offices, and the quest before it ends inside
        // them: the aethernet hop was asked for from the offices, where there is none, and Lifestream
        // could not find the destination. Being on the far side is what the step is for.
        if (step.Kind == StepKind.Interact && step.TargetTerritoryId is { } into
            && into != step.TerritoryId && _world.TerritoryId == into)
        {
            _world.Log($"Already in territory {into}, where this step's door leads — nothing to do.");
            Enter(Phase.Finish);
            return;
        }

        _attuneThenStep = false;
        if (step.Kind is StepKind.AttuneAetheryte or StepKind.AttuneAethernetShard)
        {
            BeginAttuneStep(step);
            return;
        }

        // Passing an aetheryte or shard not yet attuned: a few seconds now saves a refused
        // teleport later — the newtoons reached the Doman Enclave's teleport without it.
        if (AttuneInPassing && !IsHandoff(step.Kind) && !_world.IsRidingVehicle
            && _world.UnattunedNear(_world.PlayerPosition, AttunePassingRange) is { } passing)
        {
            _world.Log($"Passing {passing.Name}, not yet attuned — attuning it first.");
            StartAttune(passing.Id, passing.Name, passing.At, thenStep: true);
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
        if (_ownGatherAsked)
        {
            OwnGatherer?.Stop();
            _ownGatherAsked = false;
        }
        _world.ReleaseDialogue();
        ReleaseCombatHold();
        _world.ReleaseDescent();
        ReleaseAutoSnipe();
        _step = null;
        _phase = Phase.None;
        Status = StepStatus.Idle;
    }

    /// <summary>Kinds the executor can carry out today. Anything else fails at Begin with a clear reason.</summary>
    public static bool IsSupported(StepKind kind) => kind is
        StepKind.WalkTo or StepKind.Interact or StepKind.AcceptQuest or StepKind.CompleteQuest or StepKind.Combat
        or StepKind.AttuneAetheryte or StepKind.AttuneAethernetShard or StepKind.AttuneAetherCurrent or StepKind.None
        or StepKind.SinglePlayerDuty or StepKind.Duty or StepKind.Emote or StepKind.Jump or StepKind.UseItem or StepKind.Say
        or StepKind.WaitForNpcAtPosition
        or StepKind.EquipRecommended or StepKind.Action or StepKind.Instruction or StepKind.StatusOff
        or StepKind.PurchaseItem or StepKind.SwitchClass or StepKind.Craft or StepKind.Gather
        or StepKind.EquipItem or StepKind.CreateGearset or StepKind.UpdateGearset or StepKind.Dive
        or StepKind.CleanUpOtherQuests or StepKind.Snipe;

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
    /// <summary>
    /// The step has nowhere to go. Placeless kinds never do; a duty with no NPC to talk to is
    /// queued through the Duty Finder by its ContentFinderCondition, which works from anywhere —
    /// the territory on it is where the author stood, not where the character has to be. A City
    /// Fallen's alliance raid was recorded on the Prima Vista's bridge, an instanced area with no
    /// aetheryte, and travel faulted there before the duty rule could say what the step was.
    /// </summary>
    private static bool TravelsNowhere(QuestStep step)
        => IsPlaceless(step.Kind) || (step.Kind == StepKind.Duty && step.DataId is null);

    public static bool IsPlaceless(StepKind kind) => kind is
        StepKind.EquipItem or StepKind.CreateGearset or StepKind.UpdateGearset or StepKind.SwitchClass
        or StepKind.Craft or StepKind.EquipRecommended or StepKind.Instruction or StepKind.StatusOff
        or StepKind.CleanUpOtherQuests;

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

        // A door's travel question, whatever phase the step is in (the Dialogue phase answers it too).
        if (_phase != Phase.Dialogue)
            AnswerTravelQuestion(step, now);

        // A fight that wants its mob left alive under a line holds Daedalus from the step's first
        // tick, not from the fight phase: a mob that aggroes on the approach or the landing is
        // otherwise Daedalus's to kill before the fight phase ever sees it (2883, run solo, on
        // v0.2.9). Out of combat the hold costs nothing.
        if (step.Kind == StepKind.Combat && step.CombatItemUse is { } heldFor && HoldsAllFight(heldFor))
            SetCombatHold(true, now);

        switch (_phase)
        {
            case Phase.Delay:
                if (now - _phaseStart >= TimeSpan.FromSeconds(step.DelaySecondsAtStart ?? 0))
                    Enter(NextAfterDelay());
                break;

            case Phase.Settle:
                // An open conversation that belongs to this step — a turn-in chain, a hand-over
                // window — is not the game moving us somewhere: it is the step. Join it, as every
                // other phase does, rather than waiting for it to go away.
                if (_world.IsOccupied && NeedsDialogueJoin(step))
                {
                    _sawOccupied = true;
                    Enter(Phase.Dialogue);
                    break;
                }
                HoldClockForCutscene(now);
                if (!SettledForTravel)
                {
                    _settledSince = default;
                    if (now - _phaseStart > SettleMax)
                    {
                        _world.Log($"The game has not let go of the character in {SettleMax.TotalSeconds:F0}s — deciding the route anyway.");
                        _settleWaived = true;
                        Enter(NextAfterDelay());
                    }
                    break;
                }
                if (_settledSince == default)
                    _settledSince = now;
                if (now - _settledSince >= SettleGrace)
                    Enter(NextAfterDelay()); // where we are now is where the game put us
                break;

            case Phase.Teleport:
                if (!_world.IsReady || _world.InCombat)
                {
                    HoldClockForCutscene(now);
                    if (now - _phaseStart > ReadyWait) Fail("never became ready to teleport");
                    break;
                }
                // The game's aetheryte list fills a frame or two after it is asked to: a request
                // made in the same breath is accepted and casts nothing. Warm it, then ask.
                if (!_aetheryteListWarmed)
                {
                    _aetheryteListWarmed = true;
                    _aetheryteWarmedAt = now;
                    _world.RefreshAetheryteList();
                    break;
                }
                if (now - _aetheryteWarmedAt < AetheryteWarmup)
                    break;
                // A refusal right after a gearset change or a dismount is the game's action lock
                // still settling, not a missing attunement — ask again for a few seconds before
                // deciding it is real. The Qitari opener's equip refused the very next cast.
                if (now - _lastTeleportTry < TeleportRetryEvery)
                    break;
                if (!_world.Teleport(_teleportTarget))
                {
                    _lastTeleportTry = now;
                    if (now - _phaseStart > TeleportGiveUp)
                        Fail($"teleport to {step.AetheryteShortcut} (aetheryte {_teleportTarget}) kept being refused for {(now - _phaseStart).TotalSeconds:F0}s — Lifestream loaded and aetheryte attuned?");
                    break;
                }
                Enter(Phase.TeleportWait);
                break;

            case Phase.TeleportWait:
                // Lifestream said yes and nothing happened: the cast fell into the action lock a
                // gearset change leaves behind (the gather runner switches job, then teleports).
                // Ask again before believing it — the gather lists faulted every item this way.
                if (!_sawTravelBusy && !_world.IsTravelBusy && _world.TerritoryId != _teleportTerritory
                    && now - _phaseStart > TeleportStartGrace && _teleportAsks < MaxTeleportAsks)
                {
                    _teleportAsks++;
                    _world.Log($"Teleport to {step.AetheryteShortcut ?? $"aetheryte {_teleportTarget}"} never started — asking again ({_teleportAsks}/{MaxTeleportAsks}).");
                    Enter(Phase.Teleport);
                    break;
                }
                TickTravelWait(now, arrived: _world.TerritoryId == _teleportTerritory && !_world.IsTravelBusy && _world.IsReady,
                    what: $"teleport to {step.AetheryteShortcut}", next: NextAfterTeleport);
                break;

            case Phase.Aethernet:
                if (!_world.IsReady || _world.IsTravelBusy)
                {
                    HoldClockForCutscene(now);
                    if (now - _phaseStart > ReadyWait) Fail("never became ready for the aethernet");
                    break;
                }
                if (AethernetDestination is not { } hop)
                {
                    Fail("aethernet hop with no destination");
                    break;
                }
                // An Airship Landing has no shard: it joins the aethernet once every shard in the
                // city is attuned, and until then the walk up ends at the lift doors. Ride it.
                if (!_world.AethernetAttuned(hop) && step.Position is { } liftMark
                    && (OnAnotherLevel(liftMark) || step.TerritoryId != _world.TerritoryId)
                    && Travel.Lifts.Ride(_world.TerritoryId, _world.PlayerPosition, step.TerritoryId, liftMark) is { } ride)
                {
                    _world.Log($"{hop} is not on the aethernet yet — taking the lift.");
                    _autoAethernet = null;
                    _hopSkipped = true;
                    StartLift(ride.Board, ride.Alight);
                    break;
                }
                if (!_world.AethernetAttuned(hop) && _world.AethernetTerritoryOf(hop) == _world.TerritoryId)
                {
                    // Lifestream cannot hop to a shard never attuned; in the same zone the walk gets there.
                    _world.Log($"{hop} is not attuned — walking instead.");
                    _autoAethernet = null;
                    _hopSkipped = true;
                    Enter(NextAfterTravel());
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

            case Phase.AethernetApproach:
                Enter(BeginAethernet());
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
                    if (!_world.IsMounted)
                    {
                        // The saddle never came — a quest section that forbids it, usually. The
                        // fly intents die with it, or the leg presses fly-on-foot forever.
                        _farFly = false;
                        _wedgeFly = false;
                        _flyFallback = false;
                        if (_overTop && _detourFly)
                        {
                            _detourTo = null;
                            _detourFly = false;
                        }
                        _world.Log("The mount would not come — this leg stays on foot.");
                    }
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
                // On the way to an aetheryte or shard: the moment the object itself is in view, go
                // to it. The walk aims at the map marker with a guessed height, which in a city of
                // levels is the wrong level — the route then ends beside the shard, 45y "short",
                // and was given up as no path (the Crystarium's Cabinet of Curiosity, every time).
                if (_detourThen == Phase.Attune && _detourTo is not null
                    && _world.NearestAttuneObject(_attuneAt, 40f) is not null)
                {
                    _detourTo = null;
                    Enter(Phase.Attune);
                    break;
                }
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

            case Phase.DutyByHand:
                // Queues and alliance raids are long: the budget is generous, and time spent inside
                // the duty does not count against it.
                if (_world.InDuty)
                    _phaseStart = now;
                else if (now - _phaseStart > DutyByHandMax)
                    Fail($"waited {DutyByHandMax.TotalHours:F0}h for the duty to be run by hand — {_byHandNote}");
                break;

            case Phase.Instruction:
                if (now - _phaseStart > InstructionMax)
                    Fail($"nobody answered the note \"{step.Comment}\" in {InstructionMax.TotalMinutes:F0} min");
                break;

            case Phase.NpcWait:
            {
                // An escort's beat: the NPC is walking to a spot, and the step is done when
                // they stand on it. They walk at their own pace — the budget is generous, and
                // cutscenes hold it like every other wait.
                var wanted = step.NpcWaitDistance ?? 0.5f;
                if (step.DataId is { } npc && step.Position is { } spot
                    && _world.PositionOfDataId(npc) is { } npcAt
                    && Vector3.Distance(npcAt, spot) <= wanted + ArrivalSlack)
                {
                    Enter(Phase.Finish);
                    break;
                }
                HoldClockForCutscene(now);
                if (now - _phaseStart > NpcWaitMax)
                    Fail($"NPC {step.DataId} never reached {(step.Position is { } p2 ? Fmt(p2) : "its spot")} "
                        + $"in {NpcWaitMax.TotalMinutes:F0} min");
                break;
            }

            case Phase.WaitReady:
                // A WalkTo has nothing to press: arrival is completion, whatever window is up —
                // the ascend prompt at the Mol Guide belongs to the NEXT step, which declares
                // its Yes, and holding the finished walk hostage kept the answer a step away.
                if (step.Kind is StepKind.WalkTo or StepKind.None)
                    Enter(NextAfterArrival(step));
                else if (_world.IsReady && !_world.IsOccupied)
                    Enter(NextAfterArrival(step));
                else if (_world.IsOccupied && NeedsDialogueJoin(step))
                {
                    // The previous hand-in's chain is still open, and for an accept or turn-in
                    // that conversation IS the step — join it rather than waiting behind it.
                    _sawOccupied = true;
                    Enter(Phase.Dialogue);
                }
                else if (now - _phaseStart > ReadyWait)
                    Fail("player never became ready");
                else
                    HoldClockForCutscene(now);
                break;

            case Phase.Interact:
                if (_battlehorn is not null)
                {
                    Enter(Phase.Battlehorn);
                    break;
                }
                TickInteract(step, now);
                break;

            case Phase.Battlehorn:
                TickBattlehorn(now);
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
                    CommandAi(true);
                    Enter(Phase.SoloDutyRun);
                }
                else if (now - _phaseStart > DutyEnterMax)
                    Fail("solo duty did not start after the interaction");
                break;

            case Phase.SoloDutyRun:
                if (!_world.InDuty && !_world.IsTravelBusy)
                {
                    // Back outside — which is where a lost duty leaves you too. Calling that done
                    // sent the run on to the next step with the quest unmoved, and the controller
                    // then replayed the sequence from its top: for The Key to Victory, a walk to
                    // another zone. Whether it was won is the quest's to say, not the door's.
                    CommandAi(false);
                    Enter(Phase.SoloDutyVerify);
                }
                else if (now - _phaseStart > SoloDutyMax)
                {
                    CommandAi(false);
                    Fail($"solo duty did not finish in {SoloDutyMax.TotalMinutes:F0} min");
                }
                break;

            case Phase.SoloDutyVerify:
                if (_world.InDuty)
                {
                    Enter(Phase.SoloDutyRun); // straight back in (the game's own retry, or ours)
                    break;
                }
                if (!_soloProgressAtStart.IsAvailable)
                {
                    // Nothing to judge by: done, as it always was, with no wait for a verdict.
                    _handoffDone = true;
                    Enter(Phase.WaitReady);
                    break;
                }
                HoldClockForCutscene(now);    // the wrap-up plays before the quest can have moved
                if (_world.InCutscene || !_world.IsReady || now - _phaseStart < SoloVerifySettle)
                    break;
                if (SoloDutyWon())
                {
                    _handoffDone = true;
                    Enter(Phase.WaitReady);
                    break;
                }
                // Lost. Not tried again: neither duty AI has modules for quest battles, so a retry
                // loses the same way — a chain of failures with nobody watching. It waits instead.
                _soloHoldReason = "the solo duty was lost";
                Enter(Phase.SoloDutyHold);
                break;

            case Phase.SoloDutyHold:
                if (!_soloHoldSaid)
                {
                    _soloHoldSaid = true;
                    _world.Log($"Quest {_questId}: {_soloHoldReason}. Staying here rather than moving on — "
                        + "run it yourself and the quest carries on from where it is.");
                    _world.Notify($"Odysseus: {_soloHoldReason} — staying put. Run it yourself and the run carries on.");
                }
                if (_world.InDuty)
                {
                    _phaseStart = now; // they are in it now; time inside does not count
                    break;
                }
                if (SoloDutyWon())
                {
                    _handoffDone = true;
                    Enter(Phase.WaitReady);
                    break;
                }
                if (now - _phaseStart > DutyByHandMax)
                    Fail($"waited {DutyByHandMax.TotalHours:F0}h for the solo duty of quest {_questId} to be won by hand");
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

            case Phase.Meld:
                TickMeld(now);
                break;

            case Phase.Attune:
                TickAttune(step, now);
                break;

            case Phase.Lift:
                TickLift(now);
                break;

            case Phase.Door:
                TickDoor(now);
                break;

            case Phase.Gather:
                TickGather(step, now);
                break;

            case Phase.Finish:
                _world.ReleaseDialogue();
                ReleaseCombatHold();
                _world.ReleaseDescent();
                ReleaseAutoSnipe();
                Status = StepStatus.Done;
                break;
        }

        return Status;
    }

    // ── phases ──

    /// <summary>
    /// Travel decision. Standing on the mark already, no travel happens at all; otherwise teleport
    /// when the step names an aetheryte and either we are in the wrong zone or the target is a long
    /// way off in this one; then the aethernet hop if named; then walk. A step in another zone with
    /// no shortcut is a clear failure, not a doomed pathfind.
    /// </summary>
    private Phase NextAfterDelay()
    {
        var step = _step!;

        // Where the character is, is only worth deciding a route from once the game has stopped
        // moving it. A quest step ending with a cutscene that carries you to the next NPC begins
        // the next step while the cutscene is still playing: In the Dark of Night (3159) decided
        // "teleport to New Gridania, then aethernet" from East Shroud, the cutscene then set the
        // character down beside the Old Gridania NPC, and the teleport — already chosen, only
        // waiting for the cutscene to end — took it away from there, to run straight back.
        if (!_settleWaived && !TravelsNowhere(step) && !SettledForTravel)
            return Phase.Settle;

        if (AlreadyThere(step))
            return NextAfterTravel();

        if (step.AetheryteShortcut is { } aetheryteName && !_skipTeleport)
        {
            var id = _world.ResolveAetheryte(aetheryteName);
            if (id is null)
            {
                // Bad or renamed data. Say so, but do not stop on it — working the route out
                // ourselves below gets there anyway, and the log keeps the data problem visible.
                _world.Log($"Unknown aetheryte \"{aetheryteName}\" in the path data; finding my own way.");
            }
            else if (!_world.IsAttuned(id.Value))
            {
                _world.Log($"The path teleports to {aetheryteName}, which is not attuned on this character — finding another way.");
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
    /// Whether the step's mark is already within reach, in the zone the step names.
    ///
    /// <para>
    /// A recorded <c>AetheryteShortcut</c> is how the path's author reached the step from wherever
    /// they happened to be — usually the previous quest's turn-in, often a zone away, and the
    /// aetheryte they used is frequently not even in the step's own zone. Start a run cold while
    /// standing in front of the quest giver and honouring that recording teleports the run out of
    /// the zone and leaves it to find its way back. The step's <b>mark</b> is the authority on
    /// where it wants us; standing on it means the travel the path recorded has already happened,
    /// so neither the teleport nor the hop is part of the step.
    /// </para>
    /// </summary>
    /// <summary>
    /// Nothing is moving the character: no cutscene or conversation, no zone load, no cast, no
    /// Lifestream hop. Mounted or riding pillion does not count — that is travel we chose.
    /// </summary>
    private bool SettledForTravel => !_world.IsOccupied && !_world.IsTravelBusy;

    private bool AlreadyThere(QuestStep step)
    {
        if (step.TerritoryId == 0 || _world.TerritoryId != step.TerritoryId || step.Position is not { } mark)
            return false;

        var away = Vector3.Distance(_world.PlayerPosition, mark);
        if (away > TeleportWorthDistance)
            return false;

        // A mark on another level with a hop recorded: the hop is the way up. Ul'dah's top
        // level (A Sultana's Duty, It Could Happen to You) read as 148y away and walkable; the
        // walk went to the lift instead of the Airship Landing shard beside the NPC.
        if (step.AethernetShortcut is { Length: 2 } && OnAnotherLevel(mark))
            return false;

        if (step.AetheryteShortcut is not null || step.AethernetShortcut is { Length: 2 })
            _world.Log($"Already in the step's zone and {away:F0}y from the mark — skipping the recorded "
                       + (step.AetheryteShortcut is { } named ? $"teleport to {named}." : "aethernet hop."));
        return true;
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

        // Same zone, no shortcut, and the mark a long ride away with an attuned aetheryte
        // standing near it: the crystal beats even the flight. Only when it wins by a clear
        // margin — a teleport is a cast and a loading screen.
        if (_world.TerritoryId == step.TerritoryId && !TravelsNowhere(step) && !_skipTeleport && !_world.IsRidingVehicle
            && step.Position is { } sameZoneGoal
            && Vector3.Distance(_world.PlayerPosition, sameZoneGoal) is var wayOff
            && wayOff > TeleportWorthDistance
            && _world.NearestAttunedAetheryte(step.TerritoryId, sameZoneGoal, 200f) is { } nearId
            && _world.AetherytePosition(nearId) is { } nearAt
            && Vector3.Distance(nearAt, sameZoneGoal) + 200f < wayOff)
        {
            _world.Log($"The mark is {wayOff:F0}y away and an attuned aetheryte stands "
                + $"{Vector3.Distance(nearAt, sameZoneGoal):F0}y from it — teleporting there instead.");
            _teleportTarget = nearId;
            _teleportTerritory = step.TerritoryId;
            return Phase.Teleport;
        }

        // Same zone, a city, no shortcut in the path, and the mark across it: the aethernet. The
        // Disciple of the Hand quests walked Ul'dah end to end one way and hopped it back, because
        // only the return step named a shard.
        if (_world.TerritoryId == step.TerritoryId && step.AethernetShortcut is null && !TravelsNowhere(step)
            && !_skipTeleport && !_world.IsRidingVehicle && step.Position is { } cityGoal
            && _world.CityHop(step.TerritoryId, _world.PlayerPosition, cityGoal) is { } shard)
        {
            _world.Log($"The mark is {Vector3.Distance(_world.PlayerPosition, cityGoal):F0}y across the city — hopping to {shard} on the aethernet.");
            _autoAethernet = shard;
            return BeginAethernet();
        }

        if (step.TerritoryId == 0 || _world.TerritoryId == step.TerritoryId || TravelsNowhere(step))
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
        {
            // No aetheryte inside: a zone entered by a door, like the Rising Stones from Mor Dhona.
            // Get to the door's zone; the travel check takes it through the door from there.
            if (_world.DoorInto(step.TerritoryId) is { } door && door.From != _world.TerritoryId
                && _world.RouteTo(door.From, door.At) is { } outside)
            {
                _world.Log($"Quest step is in territory {step.TerritoryId}, entered by a door in {door.From} — going there first.");
                _autoAethernet = outside.AethernetName;
                if (outside.AetheryteId is not { } outsideAetheryte)
                    return Phase.Aethernet;
                _teleportTarget = outsideAetheryte;
                _teleportTerritory = outside.AetheryteTerritory;
                return Phase.Teleport;
            }
            return NextAfterTeleport(); // no way in; the travel check names it
        }

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
        if (!_hopSkipped && AethernetDestination is { } hop && WorthHopping(hop))
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
               && (Vector3.Distance(_world.PlayerPosition, target) > TeleportWorthDistance || OnAnotherLevel(target));
    }

    /// <summary>A city's levels are this far apart and joined by lifts; the aethernet is the way between them.</summary>
    private const float LevelChange = 15f;

    private bool OnAnotherLevel(Vector3 mark) => MathF.Abs(_world.PlayerPosition.Y - mark.Y) > LevelChange;

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
        {
            // None in view. The map knows where they are: walk toward the nearest until it loads,
            // then look again (Blood Ties, 2617 — the step ended at the far end of the Upper
            // Decks, and the hop asked from there never went anywhere).
            if (!_shardApproached && _world.MappedAethernetAccess(_world.TerritoryId, _world.PlayerPosition) is { } mapped)
            {
                _shardApproached = true;
                _world.Log($"No aethernet shard in view — walking toward {mapped.Name} at {Fmt(mapped.At)} before hopping to {AethernetDestination}.");
                _detourTo = mapped.At;
                _detourThen = Phase.AethernetApproach;
                _detourTolerance = ShardSightDistance;
                _detourNeedsShard = true;
                return Phase.Move;
            }
            return Phase.Aethernet; // nothing placed in this zone; let the hop try anyway
        }

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
        if (TravelsNowhere(step))
            return Phase.WaitReady;

        // A step that crosses a zone line names both ends: TerritoryId is where it starts and
        // TargetTerritoryId is where it finishes. Standing in the far one means the crossing has
        // already happened — which is what an aethernet hop into it does — so it is arrival, not
        // the wrong zone. (Highway Robbery's first step: starts in 129, ends in 128, hops there.)
        if (step.TargetTerritoryId is { } crossedInto && _world.TerritoryId == crossedInto)
            return Phase.WaitReady;

        // A zone entered by a door standing here: go through it.
        if (step.TerritoryId != 0 && _world.TerritoryId != step.TerritoryId
            && _world.DoorInto(step.TerritoryId) is { } door && door.From == _world.TerritoryId)
            return BeginDoor(door);

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
            case StepKind.Instruction when step.Comment is { Length: > 0 } note:
                // A note is something the run cannot do — a levequest, a beast assigned by hand.
                // Walking past it and carrying on is doing the wrong thing quietly, so it holds
                // until the player has done it and pressed Skip.
                _world.Notify($"Odysseus: {note} — do it, then press Skip.");
                return Phase.Instruction;

            case StepKind.Instruction or StepKind.StatusOff:
                return Phase.Finish;

            // Questionable's journal clean-up before a duty. Abandoning quests on the player's
            // behalf is not something to do on a guess about why the step exists, and Rock the
            // Castrum ran without it before upstream added it — so it is passed, and said.
            case StepKind.CleanUpOtherQuests:
                _world.Log("This step is Questionable clearing other quests out of the journal. Odysseus leaves your journal as it is and carries on.");
                return Phase.Finish;

            case StepKind.Dive:
                // Below the surface already is arrival; from the surface (or the saddle over
                // it), press the game's own descent bind until the water accepts us.
                if (_world.IsDiving)
                    return Phase.Finish;
                return Phase.Dive;

            case StepKind.WaitForNpcAtPosition:
                return Phase.NpcWait;

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
                    CommandAi(true);
                    return Phase.SoloDutyRun; // already inside (resumed mid-instance)
                }
                // The path data marks the quest battles known not to run unattended — 33 of the
                // 276 in the library. Going in anyway only spends an attempt that is certain to be
                // lost; waiting at the entrance is the same outcome without the wasted fight.
                if (step.DutyEnabled == false)
                {
                    _soloHoldReason = "this solo duty is marked as not runnable unattended";
                    return Phase.SoloDutyHold;
                }
                return step.DataId is not null ? Phase.Interact : Phase.SoloDutyEnter;

            case StepKind.Duty:
                if (_handoffDone)
                    return Phase.Finish;
                if (step.ContentFinderConditionId is not { } cfc)
                {
                    Fail("duty step names no ContentFinderCondition");
                    return Phase.None;
                }
                // A duty that is not ours to run is the player's, and the run waits for them rather
                // than stopping: clearing it moves the quest, and the controller follows the quest.
                // Decided before the in-duty check below, which is for resuming Theseus mid-run —
                // a player inside an alliance raid is not Theseus mid-dungeon.
                if (_dutyByHand)
                    return BeginDutyByHand(cfc, "the Theseus handoff is off");
                // Theseus runs 4-player dungeons. Trials and alliance raids are named and waited on.
                if (_world.DescribeDuty(cfc) is { IsDungeon: false } notDungeon)
                    return BeginDutyByHand(cfc, $"{notDungeon.Name} is an {notDungeon.Kind}, which Odysseus does not run");
                if (_world.InDuty || _world.TheseusBusy)
                    return Phase.DutyRun; // resumed while Theseus is mid-run
                if (!_world.TheseusCanEnterDuty)
                    return BeginDutyByHand(cfc, "Theseus is not loaded, is disabled, or is busy");
                if (!_world.TheseusEnterDuty(cfc))
                {
                    Fail($"Theseus refused duty {cfc} — it may have no route for it. Run it yourself, then Retry");
                    return Phase.None;
                }
                return Phase.DutyEnter;

            case StepKind.Emote:
                if (step.DataId is { } emoteTarget)
                    _world.TryTargetDataId(emoteTarget);
                _world.SendChatCommand(EmoteCommand(step.Emote));
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
                _hqMisses = 0;
                if (step.ItemId is { } named)
                {
                    _craftItem = named;
                    _craftWant = Math.Max(1, step.ItemCount ?? 1);
                    _craftHq = _world.NoteWantsHighQuality(named, step.Comment);
                    if (_craftHq)
                        _world.Log($"The path marks item {named} HQ — only high-quality ones count.");
                    return Phase.Craft;
                }
                // Ten upstream Craft steps name no item — My First Saw (205) among them — and leave
                // it to the player to know. The quest itself says what it takes: its hand-in items.
                if (NextHandInCraft(step) is not { } handIn)
                {
                    if (_world.QuestHandInCrafts(_questId, step.Comment).Count > 0)
                        return Phase.Finish; // everything the quest takes is already in the bag
                    Fail("Craft step names no item, and the quest hands in nothing that can be crafted");
                    return Phase.None;
                }
                (_craftItem, _craftWant, _craftHq) = handIn;
                _world.Log($"The Craft step names no item; quest {_questId} hands in {_craftWant} × item {_craftItem}{(_craftHq ? " HQ" : "")} — making that.");
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
        // A descent with no floor beneath it never lands: hovering against Gyorin's ledge, two
        // yalms under his feet, the dismount fell for thirty seconds and then faulted. Abort a
        // descent that is not landing and fly over the step's own business point — the object,
        // or the mark — to come down where a floor exists.
        if (_world.IsMounted && _world.IsInFlight && !_descentRerouted && _world.CanFlyHere
            && now - _phaseStart > BlockedDescentAfter)
        {
            var goal = step.DataId is { } id && _world.PositionOfDataId(id) is { } objectAt ? objectAt : step.Position;
            if (goal is { } g && Vector3.Distance(g, _world.PlayerPosition) > 0.5f)
            {
                // Come down on a floor the mesh knows, beside the mark, rather than back onto the
                // same rock: a shard node's spawn sat on one, and the reroute over the mark itself
                // spent twenty seconds finding that out again.
                var floor = _world.NearestReachablePoint(g, 8f) ?? g;
                _descentRerouted = true;
                _world.Log($"The descent is not landing (at {Fmt(_world.PlayerPosition)}) — flying over {Fmt(floor)} to come down on its floor.");
                _detourTo = floor;
                _detourThen = Phase.Dismount;
                _detourTolerance = InteractReach - ArrivalSlack;
                _detourNudged = false;
                _detourNeedsShard = false;
                _detourFly = true;
                Enter(Phase.Move);
                return;
            }
        }

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
            Fail($"could not get off the mount (at {Fmt(_world.PlayerPosition)}, "
                 + $"{(_world.IsInFlight ? "in flight" : "mounted on the ground")}, "
                 + $"target {Fmt(step.Position ?? default)})");
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

        // An ability still cooling is not a refusal: First Battlehorn is ninety seconds, and four
        // tries at a second and a half called that "refused" six seconds in.
        var cooling = _world.ActionRecastSeconds(actionId.Value);
        if (cooling > 0f)
        {
            _itemUseTries = 0;
            if (now - _phaseStart > ActionRecastMax)
            {
                Fail($"action \"{step.ActionName}\" is still {cooling:F0}s from ready after "
                    + $"{ActionRecastMax.TotalMinutes:F0} min — cast it yourself, then Retry");
                return;
            }
            if (now - _lastRecastNote > RecastNoteEvery)
            {
                _lastRecastNote = now;
                _world.Log($"Waiting {cooling:F0}s for \"{step.ActionName}\" to come off cooldown.");
            }
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
            // Diving starts while the key is still down: release it now, or the game keeps
            // holding Descend and the character sinks without end (If I Were a Fish, 2881).
            _world.ReleaseDescent();
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
    /// <summary>
    /// Hand a duty to the player and wait. The note names the duty and why it is theirs; the
    /// quest sequence moving on — which clearing it does — is what releases the step.
    /// </summary>
    private Phase BeginDutyByHand(uint cfc, string why)
    {
        var name = _world.DescribeDuty(cfc)?.Name;
        var subject = name is not null && !why.Contains(name, StringComparison.Ordinal) ? $"run {name} yourself" : "run it yourself";
        _byHandNote = $"{why} — {subject}; the quest carries on once you clear it";
        _world.Notify($"Odysseus: {_byHandNote}.");
        return Phase.DutyByHand;
    }

    /// <summary>
    /// The command for an emote name. The name is stored bare — every one of the shipped
    /// library's emote steps is — but a slash typed into the editor made "//pet", which the game
    /// answers with "the command does not exist" and no other clue.
    /// </summary>
    private static string EmoteCommand(string? emote) => "/" + (emote ?? string.Empty).Trim().TrimStart('/');

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

        _world.SendChatCommand(EmoteCommand(step.Emote));
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
            if (NeedsDialogueJoin(step))
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
            _stallAnchor = _world.PlayerPosition;
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
            && !_combatLanded && !_flyFallback && !_wedgeFly && distance <= CombatLandRadius)
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
            // Horizontal reach with a one-sided vertical: hovering ABOVE the object is arrival —
            // the dismount descends onto its floor — but standing UNDER its ledge is not, however
            // close it looks through the rock. Symmetric tolerance cut the ride to Kurobana short
            // a floor below him, with nothing the dismount could do about it.
            var above = _world.PlayerPosition.Y - objectAt.Y;
            arrived = Vector3.Distance(flat, _world.PlayerPosition) <= InteractReach
                      && above >= -2f && above <= HoverAboveObject;
        }
        if (arrived)
        {
            _world.StopMoving();
            // A leg that wedged and still arrived teaches the next visit which way works.
            if (detour is null && _frozenStops > 0)
                _wedgeMemory[WedgeKey(target)] = !_lastIssuedFly;

            var next = detour is null ? Phase.WaitReady : _detourThen;
            _detourTo = null;
            _detourNudged = false;
            _detourNeedsShard = false;
            _detourFly = false;
            Enter(next);
            return;
        }

        // The budget measures progress, not wall time: Thavnair's crossing was killed at 180s
        // with two thirds of it done and the character still closing. Every ten yalms gained
        // buys the clock back; only a leg that stops closing for the whole budget faults.
        if (distance < _bestMoveDistance - 10f)
        {
            _bestMoveDistance = distance;
            _lastMoveProgress = now;
        }
        if (now - _lastMoveProgress > MoveTotal)
        {
            Fail($"no progress toward {Fmt(target)} for {MoveTotal.TotalSeconds:F0}s ({distance:F1}y left)");
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
            // Moving in name only: the pathfollower can wedge against geometry with the position
            // frozen to the yalm — a roofline in Yanxia held one fight's approach at 25.1y for a
            // full minute of hopeful jumping. Frozen means stop and hand the leg to the remedy
            // ladder, which knows about footing, flight and rebuilds; jumping does not.
            // The epsilon must exceed a jump's bob: the stall hop moves the character a yalm
            // and a half, which reset this clock every eight seconds and kept the wedge alive
            // in place — the user broke the loop by flipping Fly off by hand, again.
            if (Vector3.Distance(_world.PlayerPosition, _frozenAt) > 2.5f)
            {
                _frozenAt = _world.PlayerPosition;
                _frozenSince = now;
            }
            else if (_frozenSince != default && now - _frozenSince > FrozenStallLimit)
            {
                // A flown leg that wedges reissues on the ground once: the volume path is what
                // snags on rooflines and branches, and the walk beneath them often just works —
                // the user proved it by flipping Fly off by hand, step after step.
                if (++_frozenStops > MaxFrozenStops)
                {
                    Fail($"the leg keeps wedging near {Fmt(_world.PlayerPosition)} — {_frozenStops} re-paths went "
                         + $"nowhere toward {Fmt(target)}. vnavmesh cannot serve this spot; the step's mark may need moving.");
                    return;
                }
                if (!_wedgeMemoryUsed && detour is null && distance <= 40f
                    && _wedgeMemory.TryGetValue(WedgeKey(target), out var groundWon))
                {
                    _wedgeMemoryUsed = true;
                    if (groundWon)
                    {
                        _groundFallback = true;
                        _wedgeFly = false;
                        _world.Log($"This spot yielded to the ground last time — going straight there.");
                    }
                    else if (_world.CanFlyHere && !_groundOnly)
                    {
                        _wedgeFly = true;
                        _groundFallback = false;
                        _world.Log($"This spot yielded to the air last time — going straight there.");
                    }
                }
                else if (_lastIssuedFly && !_overTop && detour is null && _world.CanFlyHere && !_world.IsRidingVehicle)
                {
                    // A flight to a mark a few yalms away at ground level just presses into the
                    // same wall with wings out. The obstruction is crossed from above: climb to a
                    // point over the mark, and the hover rule lands vertically onto it.
                    _overTop = true;
                    _world.Log($"The flight is wedged at {Fmt(_world.PlayerPosition)} — climbing over to come down on {Fmt(target)} from above.");
                    _world.StopMoving();
                    _detourTo = target + new Vector3(0, OverTopClimb, 0);
                    _detourThen = Phase.Move;
                    _detourTolerance = 2f;
                    _detourNudged = false;
                    _detourNeedsShard = false;
                    _detourFly = true;
                    _frozenSince = now;
                    Enter(_world.IsMounted ? Phase.Move : Phase.Mount);
                    return;
                }
                if (_lastIssuedFly && !_groundFallback)
                {
                    _groundFallback = true;
                    if (_overTop && detour is not null && _detourFly)
                    {
                        // The climb detour is an air point; the ground retry goes to the mark.
                        _detourTo = null;
                        _detourFly = false;
                    }
                    _world.Log($"The flight is wedged at {Fmt(_world.PlayerPosition)} — trying this leg on the ground.");
                }
                else if (!_lastIssuedFly && !_wedgeFly && _world.CanFlyHere && !_groundOnly && !_world.IsRidingVehicle)
                {
                    // A wedged walk takes to the air even when the step never asked to fly: the
                    // author's Fly is a preference, and the crystal's spawn-position lottery in
                    // Rak'tika wedges some spawns behind geometry no ground path escapes.
                    _wedgeFly = true;
                    _world.Log($"The walk is wedged at {Fmt(_world.PlayerPosition)} — trying this leg in the air.");
                }
                else if (_groundFallback && _frozenStops >= 3)
                {
                    // The ground wedges too: alternate back to the air for another look.
                    _groundFallback = false;
                    _world.Log($"The ground wedges as well at {Fmt(_world.PlayerPosition)} — back to the air.");
                }
                else
                    _world.Log($"Moving without moving for {(now - _frozenSince).TotalSeconds:F0}s at {Fmt(_world.PlayerPosition)} — stopping and re-pathing.");
                _world.StopMoving();
                _frozenSince = now;
                _lastMoveIssue = default; // the not-moving flow may reissue immediately
                // An air rung on foot flies nowhere: the fly move needs the saddle first. The
                // Mount phase returns to Move on its own.
                if ((_wedgeFly || _flyFallback) && !_groundFallback && !_world.IsMounted && _world.CanFlyHere && !_world.IsRidingVehicle)
                    Enter(Phase.Mount);
                return;
            }
            _lastMoveIssue = now;

            // No mount allowed here: Sprint is the fastest thing going. Asked again as it runs out.
            if (!_world.IsMounted && !_world.CanMountHere && distance > SprintWorthDistance
                && now - _lastSprintTry > SprintRetry)
            {
                _lastSprintTry = now;
                _world.Sprint();
            }

            // Moving but not getting anywhere: running into scenery the mesh thinks is passable, or
            // caught on the lip of something. A jump clears most of it, and it is what a person does
            // without thinking. Once per stall, with a long gap, so a genuinely slow leg is not
            // turned into a pogo stick.
            // Progress is getting closer *or* getting anywhere: a route round a building, or back to a
            // waypoint the follower overshot, leaves the straight-line distance where it was while the
            // character walks the whole time — and every such bend was a hop through town.
            var moved = Vector3.Distance(_world.PlayerPosition, _stallAnchor) > StallMoveProgress;
            if (distance < _closestSeen - StallProgress || moved)
            {
                if (distance < _closestSeen) _closestSeen = distance;
                _stallAnchor = _world.PlayerPosition;
                _stalledSince = now;
                _stallJumps = 0;
            }
            else if (now - _stalledSince > StallJumpAfter && now - _lastStallJump > StallJumpGap
                     && !(step.DataId is not null && distance <= 10f) && !_world.IsInFlight
                     && _world.CanMountHere)
            {
                // (Not in a zone that allows no mount: the cities. Nothing there wants a hop, and
                // what looked like a snag was the follower turning back — a hop per waypoint.)
                // (No hopping from the saddle in the air either — a flying mount cannot jump,
                // and the attempt only jitters the position under the frozen detector.)
                // Close quarters to an interact target are exempt: the game refuses commands
                // mid-jump, and the hop was eating the very press that would finish the step.
                _lastStallJump = now;
                _stalledSince = now;
                if (++_stallJumps >= 3)
                {
                    // Three hops and the distance still wobbles in place: hopping is not the
                    // answer here. Stop and let the re-path ladder have it — the wobble defeats
                    // the frozen detector's epsilon, so this is its way in.
                    _stallJumps = 0;
                    _moveRetries++;
                    _world.Log($"Three hops gained nothing toward {Fmt(target)} ({distance:F1}y) — stopping and re-pathing.");
                    _world.StopMoving();
                    _lastMoveIssue = default;
                    return;
                }
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
        if (detour is not null && !_detourNudged && distance <= DetourNudgeDistance && !_detourFly)
        {
            // (A flown detour skips the walk-the-rest nudge outright — the whole point of the
            // flight is that walking the rest is what kept failing.)
            _detourNudged = true;
            _lastMoveIssue = now;
            _world.Log($"Mesh path to {Fmt(target)} ended {distance:F1}y short; walking the rest directly.");
            _world.MoveDirectTo(target, false);
            return;
        }

        // A long leg flies even when the path says walk: the straight line is a floor on the
        // walked distance, and the air is over three times the speed. Sticky once taken, so a
        // shrinking distance does not land the flight short of the mark.
        if (!_farFly && !step.Fly && detour is null && distance > FlyWorthDistance
            && _world.CanFlyHere && !_groundOnly && !_groundFallback && !_combatLanded && !_world.IsRidingVehicle)
        {
            _farFly = true;
            _world.Log($"The leg to {Fmt(target)} is {distance:F0}y — about {distance / WalkSpeed:F0}s walking, "
                + $"{distance / AirSpeed:F0}s flying — taking the air.");
            if (!_world.IsMounted)
            {
                Enter(Phase.Mount);
                return;
            }
        }

        // While diving, every move is a volume move — the ground mesh has nothing down here.
        // A flown detour (the ledge escape) flies regardless of what the step says.
        // A fly move needs the saddle: issued on foot it is an air path the follower cannot
        // walk, and the character stands still until the frozen ladder gives it the ground. A
        // 29-yalm hop between Cedarwood spawns did exactly that, dismounted from the last node.
        // Mounted where flying is unlocked, every leg is a flight — not only those the path marks or
        // the very long ones. Riding a leg the air would halve was the slow way round; where the
        // ground truly is quicker, the pathfinder answers with the ground route and it is ridden.
        var fly = (((step.Fly || _wedgeFly || _farFly || FlyByDefault) && _world.CanFlyHere && (!_groundOnly || _flyFallback) && !_combatLanded && !_groundFallback && !_world.IsRidingVehicle)
                   || (_detourFly && detour is not null))
                  && _world.IsMounted
                  || _world.IsDiving;
        _lastIssuedFly = fly;

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
            if (_world.IsInFlight && !_landedToFinish && detour is null && distance <= OffMeshDirectMax)
            {
                // Only within the last stretch: landing fifty yalms out just trades a flight
                // problem for a longer ground one — it did, on Kurobana's hill.
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
            // A Combat mark is the same kind of waypoint: the fight measures itself by the mobs
            // it targets, not by the mark. Hearts as One faulted 2.9y from a spot the mesh could
            // not serve, with the enemies in plain reach. But an arrival-spawn may want the exact
            // mark, so where flight is possible the air gets its turn first — a combat walk only
            // settles once the fly escape has been spent or was never available.
            var combatSettles = step.Kind == StepKind.Combat && (_flyFallback || _wedgeFly || !_world.CanFlyHere);
            if ((step.Kind == StepKind.WalkTo || combatSettles) && detour is null && distance <= WalkToNearEnough)
            {
                _world.Log($"Ended {distance:F1}y short of the mark {Fmt(target)} and can get no closer — near enough for a waypoint. "
                    + $"(standing at {Fmt(_world.PlayerPosition)}, {(_world.IsInFlight ? "in flight" : _world.IsMounted ? "mounted" : "on foot")})");
                if (_frozenStops > 0)
                    _wedgeMemory[WedgeKey(target)] = !_lastIssuedFly;
                _world.StopMoving();
                Enter(Phase.WaitReady);
                return;
            }
            // A step with an object gives its last stretch to the interact machinery rather than
            // burning a mesh rebuild on a porch step: the press has its own walk-to and, past
            // that, the fly-to-the-object escape. Amber Alert's accept sat three yalms from
            // Fukudo rebuilding a mesh that was never wrong.
            if (step.DataId is not null && detour is null && distance <= 10f)
            {
                _world.Log($"The move gave up {distance:F1}y from the mark — handing the last stretch to the interact.");
                if (_frozenStops > 0)
                    _wedgeMemory[WedgeKey(target)] = !_lastIssuedFly;
                _world.StopMoving();
                Enter(Phase.WaitReady);
                return;
            }
            // The ground has no route and the path itself says to fly. The ground-only rule for
            // allied-society runs in old zones is a preference; a leg that cannot be walked at
            // all — a fenced camp, a cave with a doorway the mesh does not span — yields to the
            // path's own answer. One leg, not the run.
            if (!_flyFallback && !_wedgeFly && _world.CanFlyHere && !_world.IsRidingVehicle)
            {
                _flyFallback = true;
                _wedgeFly = true;
                _world.Log($"The ground mesh has no route to {Fmt(target)} — flying this leg.");
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
    /// <summary>Attune what is passed within this reach, before the step goes on (Settings can turn it off).</summary>
    public bool AttuneInPassing { get; set; } = true;
    private const float AttunePassingRange = 35f;

    /// <summary>How near the object an interact is pressed; how long the whole attune may take.</summary>
    private const float AttuneReach = 4.5f;
    private static readonly TimeSpan AttuneMax = TimeSpan.FromSeconds(90);

    /// <summary>This step's hop was dropped for an unattuned shard — do not choose it again.</summary>
    private bool _hopSkipped;

    private uint _attuneId;
    private string _attuneName = string.Empty;
    private Vector3 _attuneAt;
    /// <summary>The attune was a stop on the way: the step itself runs after it.</summary>
    private bool _attuneThenStep;
    private DateTime _lastAttunePress;
    private DateTime _lastAttuneMove;

    private void BeginAttuneStep(QuestStep step)
    {
        var id = step.AttuneId ?? (step.AttuneName is { Length: > 0 } named ? _world.AttunableId(named) : null);
        if (id is not { } which)
        {
            _world.Log($"This attune step names {(step.AttuneName is { Length: > 0 } n ? $"\"{n}\", which" : "no aetheryte, and nothing")} Odysseus can place — passing it.");
            Enter(Phase.Finish);
            return;
        }
        if (_world.IsAttuned(which))
        {
            Enter(Phase.Finish);
            return;
        }
        if (_world.AttunableAt(which) is not { } place)
        {
            Fail($"aetheryte {which} ({step.AttuneName}) is not on any map Odysseus reads — attune it yourself, then Retry");
            return;
        }
        if (place.TerritoryId != _world.TerritoryId)
        {
            Fail($"{place.Name} is in territory {place.TerritoryId} and you are in {_world.TerritoryId} — go there, then Retry");
            return;
        }
        StartAttune(which, place.Name, place.At, thenStep: false);
    }

    private void StartAttune(uint id, string name, Vector3 at, bool thenStep)
    {
        _attuneId = id;
        _attuneName = name;
        _attuneAt = at;
        _attuneThenStep = thenStep;
        _lastAttunePress = default;
        _lastAttuneMove = default;
        var far = Vector2.Distance(new Vector2(_world.PlayerPosition.X, _world.PlayerPosition.Z), new Vector2(at.X, at.Z));
        if (far > 40f)
        {
            // The long way is the ordinary walk (mounted or flown as any leg is), to the map's spot.
            _detourTo = at;
            _detourThen = Phase.Attune;
            _detourTolerance = 15f;
            _detourNeedsShard = false;
            Enter(!_world.IsMounted && _world.CanMountHere && far > MountWorthDistance ? Phase.Mount : Phase.Move);
            return;
        }
        Enter(Phase.Attune);
    }

    /// <summary>
    /// Walk up to the aetheryte or shard itself and interact until the game says it is attuned;
    /// then shut whatever menu that opened. An attune on the way hands back to its step; a failed
    /// one on the way is said and passed, never the end of the quest.
    /// </summary>
    private void TickAttune(QuestStep step, DateTime now)
    {
        if (_world.IsAttuned(_attuneId))
        {
            _world.CloseTravelMenus();
            _world.Log($"Attuned to {_attuneName}.");
            AfterAttune(step);
            return;
        }
        if (now - _phaseStart > AttuneMax)
        {
            _world.CloseTravelMenus();
            if (_attuneThenStep)
            {
                _world.Log($"Could not attune {_attuneName} in {AttuneMax.TotalSeconds:F0}s — carrying on with the step.");
                AfterAttune(step);
            }
            else
                Fail($"could not attune {_attuneName} in {AttuneMax.TotalSeconds:F0}s — attune it yourself, then Retry");
            return;
        }
        if (_world.IsOccupied || !_world.IsReady)
            return;   // the attune animation, or the menu it opened

        var obj = _world.NearestAttuneObject(_attuneAt, 40f);
        var goal = obj ?? _attuneAt;
        if (obj is null || Vector3.Distance(_world.PlayerPosition, goal) > AttuneReach)
        {
            if (now - _lastAttuneMove > TimeSpan.FromSeconds(2) && !_world.IsMoving)
            {
                _lastAttuneMove = now;
                _world.MoveTo(goal, false);
            }
            return;
        }
        if (_world.IsMoving)
        {
            _world.StopMoving();
            return;
        }
        if (_world.IsMounted)
        {
            _world.Dismount();
            return;
        }
        if (now - _lastAttunePress > TimeSpan.FromSeconds(2))
        {
            _lastAttunePress = now;
            _world.InteractAttuneObject(obj.Value);
        }
    }

    // ── Beastmaster battlehorns ──

    private static readonly string[] Ordinals = ["First", "Second", "Third"];
    private static readonly System.Text.RegularExpressions.Regex BattlehornWords = new(
        @"assign\s+(?<pet>.+?)\s+to\s+(?:the\s+)?(?<slot>first|second|third)\s+battlehorn",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private (int Slot, string Pet)? _battlehorn;
    private bool _battlehornAsked;
    private bool _battlehornReady;
    private DateTime _battlehornSummonedAt;
    private DateTime _lastBattlehornTry;

    /// <summary>"Assign Cu Sith to first battlehorn" → (1, "Cu Sith"); null when a comment asks nothing of the kind.</summary>
    internal static (int Slot, string Pet)? BattlehornAsk(string? comment)
    {
        if (comment is null || BattlehornWords.Match(comment) is not { Success: true } m)
            return null;
        var slot = Array.FindIndex(Ordinals, o => o.Equals(m.Groups["slot"].Value, StringComparison.OrdinalIgnoreCase)) + 1;
        return (slot, m.Groups["pet"].Value.Trim());
    }

    /// <summary>
    /// The pet onto its battlehorn (unless it is there already — picking it again would free it),
    /// then the battlehorn's summon, then a moment for the familiar to appear, then the interact.
    /// </summary>
    private void TickBattlehorn(DateTime now)
    {
        var (slot, pet) = _battlehorn!.Value;
        var horn = $"{Ordinals[slot - 1]} Battlehorn";

        if (!_battlehornReady)
        {
            if (_world.BattlehornBusy)
                return;
            if (string.Equals(_world.BattlehornPet(slot), pet, StringComparison.OrdinalIgnoreCase))
            {
                _battlehornReady = true;
                _itemUseTries = 0;
                _phaseStart = now;
                return;
            }
            if (_battlehornAsked)
            {
                Fail($"could not put {pet} on the {horn.ToLowerInvariant()} — {_world.BattlehornMessage} Assign it in the Master's Bestiary, then Retry");
                return;
            }
            if (_world.IsOccupied || !_world.IsReady)
                return;
            _world.Log(_world.AssignBattlehorn(slot, pet));
            _battlehornAsked = true;
            return;
        }

        if (_battlehornSummonedAt == default)
        {
            if (now - _lastBattlehornTry < TimeSpan.FromSeconds(1.5))
                return;
            _lastBattlehornTry = now;
            if (_world.ResolveAction(horn) is not { } actionId)
            {
                Fail($"action \"{horn}\" is not in the Action sheet");
                return;
            }
            if (_world.UseAction(actionId, null))
            {
                _world.Log($"Summoned {pet} with {horn}.");
                _battlehornSummonedAt = now;
                return;
            }
            var cooling = _world.ActionRecastSeconds(actionId);
            if (cooling > 0f)
            {
                _itemUseTries = 0;
                if (now - _phaseStart > ActionRecastMax)
                    Fail($"{horn} is still {cooling:F0}s from ready — summon {pet} yourself, then Retry");
                else if (now - _lastRecastNote > RecastNoteEvery)
                {
                    _lastRecastNote = now;
                    _world.Log($"Waiting {cooling:F0}s for {horn} to come off cooldown.");
                }
                return;
            }
            if (++_itemUseTries >= MaxItemUseTries)
                Fail($"{horn} was refused — see the log for the game's reason");
            return;
        }

        if (now - _battlehornSummonedAt < TimeSpan.FromSeconds(3))
            return;   // the familiar appearing
        _battlehorn = null;
        Enter(Phase.Interact);
    }

    // ── Doors into zones with no aetheryte ──

    private static readonly TimeSpan DoorMax = TimeSpan.FromSeconds(60);
    private const float DoorReach = 3.5f;
    private Travel.Doorways.Door? _door;
    private DateTime _lastDoorPress;
    private DateTime _lastDoorMove;

    private Phase BeginDoor(Travel.Doorways.Door door)
    {
        _door = door;
        _lastDoorPress = default;
        _lastDoorMove = default;
        _world.Log($"Going through the door into territory {door.Into}.");
        var far = Vector2.Distance(new Vector2(_world.PlayerPosition.X, _world.PlayerPosition.Z), new Vector2(door.At.X, door.At.Z));
        if (far > 40f)
        {
            _detourTo = door.At;
            _detourThen = Phase.Door;
            _detourTolerance = 15f;
            _detourNeedsShard = false;
            return !_world.IsMounted && _world.CanMountHere && far > MountWorthDistance ? Phase.Mount : Phase.Move;
        }
        return Phase.Door;
    }

    /// <summary>Walk up to the door, interact until the zone changes; its yes/no is answered on the way.</summary>
    private void TickDoor(DateTime now)
    {
        var door = _door!;
        if (_world.TerritoryId == door.Into)
        {
            if (!_world.IsReady || _world.IsOccupied)
                return;
            _world.Log($"Through the door into territory {door.Into}.");
            Enter(NextAfterTravel());
            return;
        }
        if (now - _phaseStart > DoorMax)
        {
            Fail($"could not get through the door into territory {door.Into} in {DoorMax.TotalSeconds:F0}s — go in yourself, then Retry");
            return;
        }
        if (_world.IsOccupied || !_world.IsReady)
            return;   // the question, or the loading screen

        if (Vector3.Distance(_world.PlayerPosition, door.At) > DoorReach)
        {
            if (now - _lastDoorMove > TimeSpan.FromSeconds(2) && !_world.IsMoving)
            {
                _lastDoorMove = now;
                _world.MoveTo(door.At, false);
            }
            return;
        }
        if (_world.IsMoving)
        {
            _world.StopMoving();
            return;
        }
        if (_world.IsMounted)
        {
            _world.Dismount();
            return;
        }
        if (now - _lastDoorPress > TimeSpan.FromSeconds(3))
        {
            _lastDoorPress = now;
            _world.TryInteractWithDataId(door.DataId);
        }
    }

    // ── City lifts ──

    private static readonly TimeSpan LiftMax = TimeSpan.FromSeconds(90);
    private Travel.Lifts.Stop? _liftBoard;
    private Travel.Lifts.Stop? _liftAlight;
    private uint _liftFromTerritory;
    private float _liftFromY;
    private bool _liftAnswered;
    private DateTime _lastLiftPress;
    private DateTime _lastLiftMove;

    private void StartLift(Travel.Lifts.Stop board, Travel.Lifts.Stop alight)
    {
        _liftBoard = board;
        _liftAlight = alight;
        _liftFromTerritory = _world.TerritoryId;
        _liftFromY = _world.PlayerPosition.Y;
        _liftAnswered = false;
        _lastLiftPress = default;
        _lastLiftMove = default;
        var far = Vector2.Distance(new Vector2(_world.PlayerPosition.X, _world.PlayerPosition.Z), new Vector2(board.At.X, board.At.Z));
        if (far > 40f)
        {
            _detourTo = board.At;
            _detourThen = Phase.Lift;
            _detourTolerance = 15f;
            _detourNeedsShard = false;
            Enter(!_world.IsMounted && _world.CanMountHere && far > MountWorthDistance ? Phase.Mount : Phase.Move);
            return;
        }
        Enter(Phase.Lift);
    }

    /// <summary>
    /// Walk up to the attendant, talk, pick the stop from the menu, and wait to be on the other
    /// level. A lift that never takes is said and left for the walk — never the end of the quest.
    /// </summary>
    private void TickLift(DateTime now)
    {
        var board = _liftBoard!;
        var alight = _liftAlight!;
        var where = alight.Name ?? $"the {alight.City} lift's other level";

        if (_world.TerritoryId != _liftFromTerritory || MathF.Abs(_world.PlayerPosition.Y - _liftFromY) > LevelChange)
        {
            if (!_world.IsReady || _world.IsOccupied)
                return;
            _world.Log($"Took the lift to {where}.");
            Enter(NextAfterTravel());
            return;
        }
        if (now - _phaseStart > LiftMax)
        {
            _world.Log($"The lift to {where} did not take in {LiftMax.TotalSeconds:F0}s — walking instead.");
            Enter(NextAfterTravel());
            return;
        }
        if (_world.IsAddonVisible("SelectIconString"))
        {
            if (_liftAnswered)
                return;
            var entries = _world.SelectIconStringEntries();
            if (entries.Count == 0)
                return;
            var index = Travel.Lifts.EntryFor(entries, alight);
            if (index < 0)
            {
                _world.Log($"The lift menu does not offer {where} — [{string.Join(" | ", entries)}]; walking instead.");
                _world.SelectIconStringIndex(entries.Count - 1);
                Enter(NextAfterTravel());
                return;
            }
            _world.Log($"Lift: \"{entries[index]}\".");
            _world.SelectIconStringIndex(index);
            _liftAnswered = true;
            return;
        }
        if (_world.IsOccupied || !_world.IsReady)
            return;   // the ride itself
        _liftAnswered = false;

        if (Vector3.Distance(_world.PlayerPosition, board.At) > AttuneReach)
        {
            if (now - _lastLiftMove > TimeSpan.FromSeconds(2) && !_world.IsMoving)
            {
                _lastLiftMove = now;
                _world.MoveTo(board.At, false);
            }
            return;
        }
        if (_world.IsMoving)
        {
            _world.StopMoving();
            return;
        }
        if (_world.IsMounted)
        {
            _world.Dismount();
            return;
        }
        if (now - _lastLiftPress > TimeSpan.FromSeconds(3))
        {
            _lastLiftPress = now;
            _world.TryInteractWithDataId(board.AttendantId);
        }
    }

    private void AfterAttune(QuestStep step)
    {
        if (!_attuneThenStep)
        {
            Enter(Phase.Finish);
            return;
        }
        _attuneThenStep = false;
        Enter(step.DelaySecondsAtStart is > 0 ? Phase.Delay : NextAfterDelay());
    }

    /// <summary>This step is a sniping section, run as the interact that starts it.</summary>
    private bool _sniping;

    /// <summary>The snipe skip was switched on for this step and goes back off after it.</summary>
    private bool _autoSnipeSwitched;

    private void ReleaseAutoSnipe()
    {
        if (!_autoSnipeSwitched)
            return;
        _autoSnipeSwitched = false;
        _world.SetAutoSnipe(false);
    }

    /// <summary>The item this Craft step is making: the one it names, else the quest's hand-in in hand.</summary>
    private uint _craftItem;
    private int _craftWant = 1;

    /// <summary>The quest takes only high-quality ones: normal-quality copies do not count toward <see cref="_craftWant"/>.</summary>
    private bool _craftHq;

    /// <summary>HQ copies held when the last ask went in, to tell "it made one" from "it made a normal one".</summary>
    private int _hqAtAsk;

    /// <summary>Crafts in a row that came out normal quality when HQ was wanted.</summary>
    private int _hqMisses;

    /// <summary>Normal-quality results in a row before the step stops rather than burn more materials.</summary>
    private const int MaxHqMisses = 2;

    private CraftNote.Meld? _meld;
    private uint _meldItem;
    /// <summary>0: the item is next to pick; 1: the materia is.</summary>
    private int _meldStage;
    private DateTime _meldBeat;

    /// <summary>One click a beat — each one changes the window, and the next is read off the change.</summary>
    private static readonly TimeSpan MeldBeat = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MeldMax = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Meld the materia the quest wants into the item just made — the clicks the game's own window
    /// sends (recorded 2026-09-27): open Materia Melding, pick the item by name, pick a materia the
    /// requirement takes, and press Meld only when the confirmation names that item and that materia.
    /// Done when the item carries it; back to the craft step, which moves on from there.
    /// </summary>
    private void TickMeld(DateTime now)
    {
        var meld = _meld!;
        var item = _meldItem;

        if (_world.HoldsMelded(item, meld))
        {
            _world.CloseMelding();
            _world.Log($"Item {item} now carries {DescribeMeld(meld)}.");
            Enter(Phase.Craft);
            return;
        }
        if (now - _phaseStart > MeldMax)
        {
            _world.CloseMelding();
            Fail($"melding {DescribeMeld(meld)} into item {item} did not finish in {MeldMax.TotalSeconds:F0}s — meld it yourself, then Retry");
            return;
        }
        if (_meldBeat != default && now - _meldBeat < MeldBeat)
            return;
        _meldBeat = now;

        switch (_world.MeldDialogIsFor(item, meld))
        {
            case true:
                _world.ConfirmMeld();
                return;
            case false:
                _world.CloseMelding();
                Fail($"the melding confirmation named something other than {DescribeMeld(meld)} into item {item} — nothing was melded; meld it yourself, then Retry");
                return;
        }

        if (!_world.MeldingOpen)
        {
            if (!_world.MeldingUnlocked)
            {
                Fail($"the quest takes item {item} with {DescribeMeld(meld)} melded, and Materia Melding is not learned yet — " +
                     "learn it (the quest \"Forging the Spirit\"), then Retry");
                return;
            }
            _world.OpenMelding();
            _meldStage = 0;
            return;
        }

        if (_meldStage == 0)
        {
            var itemIndex = _world.MeldItemIndex(item);
            if (itemIndex < 0)
            {
                _world.CloseMelding();
                Fail($"item {item} is not in the melding window's list — move it to the bags, then Retry");
                return;
            }
            _world.MeldSelectItem(itemIndex);
            _meldStage = 1;
            return;
        }

        var materiaIndex = _world.MeldMateriaIndex(meld);
        if (materiaIndex < 0)
        {
            _world.CloseMelding();
            Fail($"no {DescribeMeld(meld)} in the bags to meld into item {item} — get one, then Retry");
            return;
        }
        _world.MeldSelectMateria(materiaIndex);
        _meldStage = 0;   // after a meld the window is back at its lists; pick again if more are wanted
    }

    private static string DescribeMeld(CraftNote.Meld meld)
    {
        var what = meld.Materia ?? (meld.Grade is { } g ? $"materia of grade {g} (no higher)" : "any materia");
        return meld.Count > 1 ? $"{meld.Count} × {what}" : what;
    }

    /// <summary>What counts toward the target: HQ copies only, when that is what the quest takes.</summary>
    private int CraftHave(uint item, bool hq) => hq ? _world.ItemCountHq(item) : _world.ItemCount(item);

    /// <summary>The quest's craftable hand-in items not yet all in the bag, in the quest's own order — the first of them.</summary>
    private (uint ItemId, int Count, bool HighQuality)? NextHandInCraft(QuestStep step)
    {
        foreach (var (item, count, hq) in _world.QuestHandInCrafts(_questId, step.Comment))
            if (CraftHave(item, hq) < count)
                return (item, count, hq);
        return null;
    }

    private void TickCraft(QuestStep step, DateTime now)
    {
        var item = _craftItem;
        var want = _craftWant;
        var have = CraftHave(item, _craftHq);
        // The bag total to reach: normal-quality copies already held do not count, so they are
        // added on top — asking the crafter for "want" in total would stop at the NQ ones.
        var target = want + (_world.ItemCount(item) - have);

        if (have >= want)
        {
            if (_craftAsked != 0 && _world.IsCrafting)
                _world.StopCrafting();
            // Made — but some quests take it only with materia melded in (The Lance's Lesson, Saving
            // Captain Gairhard). Carrying it over unmelded ends at a hand-in that never fills.
            if (_world.NoteWantsMeld(item, step.Comment) is { } meld && !_world.HoldsMelded(item, meld))
            {
                _meld = meld;
                _meldItem = item;
                _meldStage = 0;
                _meldBeat = default;
                _world.Log($"Item {item} is made; the quest takes it only with {DescribeMeld(meld)} melded — melding it.");
                Enter(Phase.Meld);
                return;
            }
            // A step with no item of its own makes every hand-in the quest wants, one after another.
            if (step.ItemId is null && NextHandInCraft(step) is { } following)
            {
                (_craftItem, _craftWant, _craftHq) = following;
                _hqMisses = 0;
                _craftAsked = 0;
                _craftHeldAtAsk = 0;
                _phaseStart = now;
                _world.Log($"Item {item} made; quest {_questId} also hands in {_craftWant} × item {_craftItem} — making that.");
                return;
            }
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
            Fail($"{want - have} × item {item}{(_craftHq ? " HQ" : "")} needs crafting and {_world.CrafterName} is not loaded — " +
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
            if (TryBuyMaterials(item, target))
                return;

            var short_ = want - have;
            var missing = _world.CraftShortfall(item, short_);
            // No shortfall means the materials are all there and something else stopped it — the
            // recipe's level, or Artisan not being able to reach the log. Saying "stock up" there
            // would send you looking for materials you already have.
            Fail($"{_world.CrafterName} stopped with {short_} × item {item} still to make" +
                 (missing.Count > 0
                     ? $" — short of {Describe(missing)}. Get those, then Retry"
                     : $", and the materials are all there — check the recipe's level and that {_world.CrafterName} can craft it"));
            return;
        }

        // Nothing craftable is left to try: the item has no recipe, or what it is short of has to
        // be bought or gathered rather than made.
        // Something is already in flight; leave it alone until the grace above has run out.
        if (_craftAsked != 0 && now - _phaseStart <= CraftStartGrace)
            return;

        // The last ask for this item came back, and not as HQ. Try again — a craft is a roll — but
        // not forever: every miss spends a full set of materials.
        if (_craftHq && _craftAsked == item && _world.ItemCount(item) > _craftHeldAtAsk && have <= _hqAtAsk)
        {
            _craftAsked = 0;
            if (++_hqMisses >= MaxHqMisses)
            {
                Fail($"{_world.CrafterName} made item {item} at normal quality {_hqMisses} times, and this quest takes only HQ — " +
                     "better gear or food, or make one HQ yourself, then Retry");
                return;
            }
            _world.Log($"Item {item} came out normal quality and the quest takes only HQ — crafting again ({_hqMisses}/{MaxHqMisses}).");
        }

        if (_world.NextCraft(item, target) is not { } next)
        {
            if (TryBuyMaterials(item, target))
                return;
            var missing = _world.CraftShortfall(item, want - have);
            Fail($"no recipe for item {item}, or its materials cannot be crafted" +
                 (missing.Count > 0 ? $" — short of {Describe(missing)}" : "") + ". Buy or gather the rest, then Retry");
            return;
        }

        // Sampled before the ask, not after: this is the baseline that answers "did anything
        // actually arrive", and reading it afterwards would compare the result against itself.
        var heldBefore = _world.ItemCount(next.ItemId);
        if (_world.StartCraft(next.ItemId, next.Count) is not { } job)
        {
            Fail($"no recipe for item {next.ItemId}, or {_world.CrafterName} would not take the craft");
            return;
        }
        _craftAsked = next.ItemId;
        _craftHeldAtAsk = heldBefore;
        _hqAtAsk = have;
        _phaseStart = now;
        _world.Log(next.ItemId == item
            ? $"Asked {_world.CrafterName} for {next.Count} × item {item} as {job}."
            : $"Asked {_world.CrafterName} for {next.Count} × item {next.ItemId} first — item {item} is made from it.");
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

        // Our own gathering first, when it is wired in, switched on, and knows the item: the
        // GatherBuddy handoff needs the item hand-kept on an auto-gather list, which is the
        // failure it kept ending in. Bench modes (probe, dry-run) leave quest steps alone.
        var own = OwnGatherer;
        if (own is not null && own.Enabled && !own.ProbeOnly && !own.DryRun
            && (_ownGatherAsked || own.CanGather(outstanding.ItemId, step.TerritoryId)))
        {
            if (!_ownGatherAsked)
            {
                var remaining = outstanding.ItemCount - _world.ItemCount(outstanding.ItemId);
                if (own.Start(outstanding.ItemId, remaining, 0, step.TerritoryId))
                {
                    _ownGatherAsked = true;
                    _world.Log($"Gathering {remaining} × item {outstanding.ItemId} ourselves.");
                }
            }
            if (_ownGatherAsked)
            {
                own.Tick();
                if (own.Faulted)
                {
                    _ownGatherAsked = false;
                    Fail($"gathering item {outstanding.ItemId} gave up: {own.Status}");
                    return;
                }
                if (!own.Busy)
                    _ownGatherAsked = false; // finished this ask; the count check above judges it
                return;
            }
        }

        // Reached with the own gatherer on but declining: say which link is missing, once,
        // so the field explains itself instead of silently handing to GatherBuddy.
        if (own is not null && own.Enabled && !own.ProbeOnly && !own.DryRun && !_ownGatherDeclineSaid)
        {
            _ownGatherDeclineSaid = true;
            _world.Log($"Own gathering declined: {own.WhyNot(outstanding.ItemId, step.TerritoryId)} — handing to GatherBuddy.");
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
            if (_world.IsOccupied && NeedsDialogueJoin(step))
            {
                _sawOccupied = true;
                Enter(Phase.Dialogue);
                return;
            }
            HoldClockForCutscene(now);
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
                Fail($"could not get off the mount to interact with {dataId} (at {Fmt(_world.PlayerPosition)}, "
                     + $"{(_world.IsInFlight ? "in flight" : "mounted on the ground")})");
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

        if (_world.IsJumping)
            return; // the game refuses commands mid-jump — the press would be eaten silently

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
            // Sniping by hand takes as long as it takes; the section ending is what ends the step.
            if (_sniping)
                _phaseStart = now;
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

        // A yes/no the step does not name. Nothing else in the stack answers one of these:
        // TextAdvance's executors cover Talk, quest accept and complete, hand-ins and the
        // skip-cutscene list, and it has no generic yes/no at all (read from its source,
        // reference/TextAdvance/Executors). Questionable answers them from its own path data, the
        // same as this does — so a prompt the data missed is a prompt nobody answers, and the step
        // used to sit there until it failed with "dialogue never ended".
        //
        // Answering it blind is not the fix: a yes/no is how a run takes up a class or a first DoH
        // quest it was never asked to take. So the question is repeated to the player, by name, and
        // the clock stops while it stands.
        if (_world.IsAddonVisible("SelectYesno"))
        {
            if (TryAnswerYesNo(step, now))
                return;

            // "Duty calls — begin?" is the step's whole purpose, the way the offer window is an
            // AcceptQuest step's: Questionable answers it Yes for the same reason. Without this the
            // undeclared-question hold would stop every solo duty at the door.
            if (step.Kind == StepKind.SinglePlayerDuty)
            {
                if (now - _lastDutyCallsYes > TimeSpan.FromSeconds(1.5))
                {
                    _lastDutyCallsYes = now;
                    _world.SelectYesNo(true);
                }
                _sawOccupied = true;
                _phaseStart = now;
                return;
            }

            // A crossing the path names — TargetTerritoryId — asked the game's own way: the gate
            // guard's "Leave the Ala Mhigan Quarter?" (The Mad King's Trove, 2964). Questionable
            // answers these Yes for the same reason; the match is against the Warp sheet's
            // questions into the step's destination, so no other question is taken for one.
            if (AnswerTravelQuestion(step, now))
            {
                _sawOccupied = true;
                _phaseStart = now;
                return;
            }

            if (_yesNoOpenedAt == default)
                _yesNoOpenedAt = now;
            if (now - _yesNoOpenedAt > UndeclaredYesNoGrace)
            {
                if (!_yesNoReported)
                {
                    _yesNoReported = true;
                    var prompt = _world.YesNoPrompt();
                    var asking = prompt.Length > 0 ? $"\"{prompt}\"" : "a yes/no question";
                    _world.Log($"Quest {_questId} names no answer for {asking} — waiting for you.");
                    _world.Notify($"Odysseus: the game is asking {asking} — answer it and the run carries on.");
                }
                _sawOccupied = true;
                _phaseStart = now;   // a question only a human can answer does not run the clock down
            }
            return; // nothing settles while it stands
        }
        _yesNoOpenedAt = default;
        _yesNoReported = false;
        _yesNoAnswered = false;

        // The quest offer itself: TextAdvance's accept function may be off (it is a global
        // toggle over there), and an AcceptQuest step's whole purpose is this window. Press its
        // own Accept — the same press the society accept loop makes.
        if (step.Kind == StepKind.AcceptQuest && _world.IsAddonVisible("JournalAccept")
            && now - _lastOfferAccept > TimeSpan.FromSeconds(1) && _world.AcceptOfferedQuest())
        {
            _lastOfferAccept = now;
            _sawOccupied = true;
            _phaseStart = now;
            _world.Log("Quest offer accepted.");
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
                    // A pick-up step is taking a DIFFERENT quest from this NPC — the path detours to
                    // collect it on the way. Answering the menu with the running quest's name found
                    // nothing, and the step sat with the menu up until it faulted.
                    var wanted = step.PickUpQuestId ?? _questId;
                    var name = _world.QuestName(wanted);
                    var index = name is null ? -1 : FindEntry(entries, name);

                    // Not a quest menu: some NPCs ask their own question in this window — Ul'dah's
                    // lift attendant ("Ride Lift to the Airship Landing") is one. The step's List
                    // choice answers it just as it would the plain menu.
                    if (index < 0 && step.DialogueChoices is { } choices && System.Linq.Enumerable.FirstOrDefault(choices, c => c.Type.Equals("List", StringComparison.OrdinalIgnoreCase)) is { Answer: { } key }
                        && _texts?.Resolve(_questId, key) is { } answer)
                    {
                        index = FindEntry(entries, answer);
                        name = answer;
                    }

                    // A lift attendant the path talks to without naming a stop — Nanahomi, at
                    // Ul'dah's Airship Landing, carries When the Dust Settles (4063) down into the
                    // Steps of Thal. The zone it crosses into says which stop.
                    if (index < 0 && Travel.Lifts.IsLiftMenu(entries) && step.TargetTerritoryId is { } into && into != _world.TerritoryId
                        && Travel.Lifts.StopFor(into, step.Position ?? _world.PlayerPosition) is { } stop)
                    {
                        index = Travel.Lifts.EntryFor(entries, stop);
                        name = stop.Name ?? name;
                    }
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
                        _world.Log($"The quest menu does not list \"{name ?? wanted.ToString()}\" — " +
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
                _detourFly = false;
                Enter(Phase.Move);
                return;
            }

            _world.Log($"Nothing opened after interacting with {target} — asking again ({_interactRetries}/{MaxInteractRetries}).");
            Enter(Phase.Interact);
            return;
        }

        // The universal ledge escape: retries spent, nothing ever opened, and the object is
        // simply somewhere the ground approach cannot serve — up a lip, across a mesh gap, at
        // the wrong angle. Fly to the thing itself; the landing machinery puts us on its floor,
        // and the press happens from where a person would stand. One flight, then the verdict.
        if (!_sawOccupied && step.DataId is { } flyTarget && !_interactFlew && _world.CanFlyHere
            && _world.PositionOfDataId(flyTarget) is { } flyWhere)
        {
            _interactFlew = true;
            _interactRetries = 0;
            _world.Log($"The ground cannot reach {flyTarget} — flying to it.");
            _detourTo = flyWhere;
            _detourThen = Phase.Interact;
            _detourTolerance = InteractReach - ArrivalSlack;
            _detourNudged = false;
            _detourNeedsShard = false;
            _detourFly = true;
            Enter(_world.IsMounted ? Phase.Move : Phase.Mount);
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
                if (_world.InDuty) CommandAi(true);
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
    /// <summary>
    /// Answer the yes/no on screen, if anything in this quest's data says what to answer.
    ///
    /// <para>
    /// The step's own choices are asked first, then every step of the sequence it belongs to. That
    /// second look is what the old code was missing: the data attaches the choice to the step that
    /// <i>provokes</i> the question, and the window regularly opens while a neighbouring step is
    /// running, which left a perfectly well-recorded answer unused.
    /// </para>
    ///
    /// <para>
    /// The prompt is matched, not merely counted. 501 of the 510 yes/no choices in the shipped
    /// library name the prompt as one of the quest's own text keys, and the old code ignored it
    /// outright — so a step that declared "answer No to this one" answered No to whatever box
    /// happened to be up. Where the key will not resolve, or the data names no prompt at all (the
    /// other 9), the step's own choice is still trusted; another step's is not.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether the solo duty moved the quest. Anything changed — sequence, variables, or the quest
    /// leaving the journal altogether — counts: a win does not always advance the sequence, but it
    /// always leaves a mark, and a loss leaves none. With nothing to compare against, this cannot
    /// tell, and says won rather than retrying a duty that may well be done.
    /// </summary>
    private bool SoloDutyWon()
    {
        var before = _soloProgressAtStart;
        if (!before.IsAvailable)
            return true;
        var now = _world.QuestState(_questId);
        return !now.IsAvailable
               || now.Sequence != before.Sequence
               || !now.Variables.Span.SequenceEqual(before.Variables.Span);
    }

    /// <summary>
    /// The game's own travel question on the way to where the step is: into the zone the step
    /// crosses to, or — the path marking no crossing — into the zone the step is in. Answered yes,
    /// in any phase: the door's question comes up while the walk or the interact is still going,
    /// and "Enter the Ruby Bazaar offices?" sat waiting there for a click. True when it answered.
    /// </summary>
    private bool AnswerTravelQuestion(QuestStep step, DateTime now)
    {
        if (!_world.IsAddonVisible("SelectYesno"))
            return false;
        uint? bound = _phase == Phase.Door && _door is { } door
            ? door.Into
            : step.TargetTerritoryId
              ?? (step.TerritoryId != 0 && step.TerritoryId != _world.TerritoryId ? step.TerritoryId : null);
        if (bound is not { } into || !IsTravelPromptInto(into))
            return false;
        if (now - _lastTravelYes > TimeSpan.FromSeconds(1.5))
        {
            if (_lastTravelYes == default)
                _world.Log($"Answering yes to \"{_world.YesNoPrompt()}\" — the way into territory {into}, where this step goes.");
            _lastTravelYes = now;
            _world.SelectYesNo(true);
        }
        return true;
    }

    private bool IsTravelPromptInto(uint territoryId)
    {
        var asked = Squash(_world.YesNoPrompt());
        if (asked.Length == 0)
            return false;
        foreach (var prompt in _world.TravelPrompts(territoryId))
            if (Squash(prompt) == asked)
                return true;
        return false;
    }

    private bool TryAnswerYesNo(QuestStep step, DateTime now)
    {
        var asked = Squash(_world.YesNoPrompt());
        var choice = PickYesNo(step.DialogueChoices, asked, trustUnmatched: true)
                     ?? (asked.Length > 0 ? PickYesNo(SequenceChoices, asked, trustUnmatched: false) : null);
        if (choice is null)
            return false;

        var yes = choice.Yes ?? true;
        if (!_yesNoAnswered)
        {
            _yesNoAnswered = true;
            _sawOccupied = true;
            _phaseStart = now;   // an answer is progress; the dialogue budget starts again
            _world.Log($"Answering {(yes ? "yes" : "no")} to \"{_world.YesNoPrompt()}\" "
                + $"(quest {_questId} names it as {choice.Prompt ?? "an unnamed prompt"}).");
            _lastYesNoPress = now;
            _world.SelectYesNo(yes);
            return true;
        }

        // Answered, and the window is still standing. Press again on a throttle — but the clock
        // keeps running now, so a press that never takes ends as an honest timeout rather than a
        // silent forever-hold.
        if (now - _lastYesNoPress > YesNoRetry)
        {
            _lastYesNoPress = now;
            _world.SelectYesNo(yes);
        }
        return true;
    }

    private DialogueChoice? PickYesNo(IReadOnlyList<DialogueChoice>? choices, string asked, bool trustUnmatched)
    {
        if (choices is null)
            return null;
        foreach (var choice in choices)
        {
            if (!choice.Type.Equals("YesNo", StringComparison.OrdinalIgnoreCase))
                continue;
            var wanted = choice.Prompt is { Length: > 0 } key ? Squash(_texts?.Resolve(_questId, key) ?? string.Empty) : string.Empty;
            if (wanted.Length == 0 || asked.Length == 0)
            {
                if (trustUnmatched)
                    return choice;   // nothing to compare against — the step's own data is still the best we have
                continue;
            }
            if (asked.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(asked, StringComparison.Ordinal))
                return choice;
        }
        return null;
    }

    /// <summary>
    /// Both sides of a prompt comparison, reduced to bare letters and digits. The live window drops
    /// the line-break payloads the sheet renders as control characters, so the sheet text is cut at
    /// its first control character — the first sentence is the stable part — and neither side's
    /// whitespace or punctuation is trusted. Same reasoning as the overcap matcher in GameStepWorld.
    /// </summary>
    internal static string Squash(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c))
                break;
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private void AnswerDialogue(QuestStep step, DateTime now)
    {
        DialogueChoice? listChoice = null;
        foreach (var choice in (System.Collections.Generic.IEnumerable<DialogueChoice>?)step.DialogueChoices ?? Array.Empty<DialogueChoice>())
        {
            if (choice.Type.Equals("List", StringComparison.OrdinalIgnoreCase))
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
            // The same menu back again: it is the NPC's chat menu, which reopens after every topic
            // until the last line ("Nothing.") is picked. Might Made Right (648) asked Severian
            // "What do you do here?" every three seconds for minutes — straight after a hand-in he
            // answers with chat, and offers the next quest a moment later. So leave the talk; the
            // run asks again, and the replay limit stops it with a reason if nothing ever comes.
            var shown = string.Join(" | ", entries);
            if (_unnamedListTaken == shown)
            {
                _world.Log($"The same menu came back — leaving the conversation with \"{entries[^1]}\".");
                _world.SelectStringIndex(entries.Count - 1);
                _listAnswered = true;
                return;
            }
            _world.Log($"A list choice is open that quest {_questId} does not name — [{shown}]; taking the first option.");
            _world.SelectStringIndex(0);
            _listAnswered = true;
            _unnamedListTaken = shown;
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

    /// <summary>
    /// A quest item to use on the mob once it is ready for it — "weaken it, then use the net".
    ///
    /// <para>
    /// Daedalus does the fighting, and at level 100 one GCD kills a level-65 mob from full: for a
    /// health threshold the mob never sits below it long enough to take the item. So for the kinds
    /// where it can die first — a health threshold, a missing status — Daedalus is asked to hold its
    /// actions for the whole fight. Auto-attack is the game's and keeps swinging, a few percent a hit,
    /// which is exactly the controlled damage the quest wants. An incapacitated mob cannot die, so
    /// there Daedalus fights freely and is held only while the item goes on.
    /// </para>
    ///
    /// <para>
    /// The hold is a lease on Daedalus's side, so it is renewed here every second while wanted and
    /// simply lapses if Odysseus stops asking — a crash cannot leave the character standing idle.
    /// </para>
    /// </summary>
    /// <returns>True when this tick was spent on the item and nothing else should happen.</returns>
    private bool TickCombatItem(CombatItemUse use, System.Collections.Generic.IReadOnlyCollection<uint> enemies, DateTime now)
    {
        if (use.Condition == CombatItemCondition.Unknown)
        {
            if (!_combatItemUnknownSaid)
            {
                _combatItemUnknownSaid = true;
                _world.Log($"This step wants item {use.ItemId} used on the mob under a condition this build does not know — fighting it as an ordinary fight.");
            }
            return false;
        }

        var target = _world.CombatTarget(enemies);
        var ready = target is { } t && CombatItemReady(use, t);
        var holdAllFight = HoldsAllFight(use);
        SetCombatHold(holdAllFight || ready, now);

        // The mob's health on its way down, every ten points, so a fight that ends without the
        // item says how: one hit from above the line to dead reads differently from a mob that
        // never got near it.
        if (target is { } seen && use.Condition == CombatItemCondition.HealthPercent
            && (_combatItemLoggedHealth < 0 || Math.Abs(_combatItemLoggedHealth - seen.HealthPercent) >= 10))
        {
            _combatItemLoggedHealth = seen.HealthPercent;
            _world.Log($"Mob {seen.DataId} at {seen.HealthPercent:F0}% (item {use.ItemId} goes on under {use.Value}%).");
        }

        // Held, in a fight, and not facing the mob the item is for: nobody else will pick it up —
        // Daedalus is holding — so engage it, and auto-attack takes it from there.
        if (holdAllFight && target is null && _world.InCombat)
        {
            _world.AttackNearestEnemy(enemies, CombatSearchRadius);
            return true;
        }

        if (!ready || now - _combatItemUsedAt < CombatItemRetry || _world.IsTravelBusy)
            return false;

        _combatItemUsedAt = now;
        _world.Log(use.Condition switch
        {
            CombatItemCondition.HealthPercent => $"The mob is at {target!.HealthPercent:F0}% — under {use.Value}%; using item {use.ItemId} on it.",
            CombatItemCondition.Incapacitated => $"The mob is down on one knee; using item {use.ItemId} on it.",
            _ => $"The mob no longer has status {use.Value}; using item {use.ItemId} on it.",
        });
        _world.UseItem(use.ItemId);
        return true;
    }

    /// <summary>The kinds where the mob can die before it is ready: Daedalus is held for all of it.</summary>
    private static bool HoldsAllFight(CombatItemUse use)
        => use.Condition is CombatItemCondition.HealthPercent or CombatItemCondition.MissingStatus;

    /// <summary>Whether the mob is ready for the item. Pure, so the three conditions are pinned by tests.</summary>
    internal static bool CombatItemReady(CombatItemUse use, CombatTargetReading target) => use.Condition switch
    {
        CombatItemCondition.HealthPercent => target.HealthPercent < use.Value,
        CombatItemCondition.Incapacitated => target.Incapacitated,
        CombatItemCondition.MissingStatus => !System.Linq.Enumerable.Contains(target.StatusIds, (uint)use.Value),
        _ => false,
    };

    private void SetCombatHold(bool hold, DateTime now)
    {
        if (!hold)
        {
            ReleaseCombatHold();
            return;
        }
        if (_combatHoldOn && now - _combatHoldAsserted < CombatHoldRenew)
            return;
        if (!_combatHoldOn)
            _world.Log("Holding Daedalus's actions for this fight — auto-attack will bring the mob down for the item.");
        if (!_world.HoldCombatActions(true) && !_combatHoldRefusedSaid)
        {
            _combatHoldRefusedSaid = true;
            _world.Log("Daedalus did not take the hold (not loaded, older than v0.1.87, or held by another plugin) — it may kill the mob before the item goes on.");
        }
        _combatHoldOn = true;
        _combatHoldAsserted = now;
    }

    private void ReleaseCombatHold()
    {
        if (!_combatHoldOn)
            return;
        _world.HoldCombatActions(false);
        _combatHoldOn = false;
    }

    private void TickCombat(QuestStep step, DateTime now)
    {
        CommandAi(true);
        var enemies = (System.Collections.Generic.IReadOnlyCollection<uint>?)step.KillEnemyDataIds ?? Array.Empty<uint>();

        if (step.CombatItemUse is { } use && TickCombatItem(use, enemies, now))
            return;

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

        // Daedalus switched off: no one is going to fight, so do not pull. The pull re-targets every
        // tick, and with nothing ever entering combat it took the player's target for good — a
        // disabled healer could not target anything (reported 2026-09-26). The clock holds with it,
        // the way a gearset build holds a move, so turning Daedalus back on carries straight on.
        if (_world.DaedalusDisabledByUser)
        {
            _stepStart = now;
            if (!_daedalusOffSaid)
            {
                _daedalusOffSaid = true;
                _world.Log("Daedalus is switched off — not pulling. Enable it to carry on.");
            }
            return;
        }
        _daedalusOffSaid = false;

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
            {
                if (step.CombatItemUse is { } unused && _combatItemUsedAt == default)
                    _world.Log($"The fight ended without item {unused.ItemId} going on"
                        + (_combatItemLoggedHealth >= 0 ? $" — the mob was last seen at {_combatItemLoggedHealth:F0}%." : " — the mob was never seen targeted."));
                Enter(Phase.Finish);
            }
            return;
        }

        // Never fought. Enemies that spawn on arrival can take a few seconds — and their trigger
        // is keyed to where the path author stood, while arrival settles four-and-a-half yalms
        // short of the mark. Before concluding nothing is coming, stand exactly on it: the
        // Cowardly Lupin's ambush wanted the last few steps.
        if (step.EnemySpawnType == EnemySpawnType.AutoOnEnterArea && !_creptToMark
            && step.Position is { } spawnMark && now - _phaseStart > CombatCreepAfter
            && Vector3.Distance(_world.PlayerPosition, spawnMark) > 1.5f)
        {
            _creptToMark = true;
            _world.Log($"Nothing spawned {Vector3.Distance(_world.PlayerPosition, spawnMark):F1}y from the mark — stepping exactly onto it.");
            _world.MoveDirectTo(spawnMark, false);
            _phaseStart = now; // a fresh spawn wait, from the spot itself
            return;
        }
        // Enemies that were meant to be found may simply not be here (already dead, or the flags
        // are already set). Optional combat has nothing to wait for: if the leftovers were here,
        // the pull above would have taken them.
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
            _bestMoveDistance = float.MaxValue;
            _lastMoveProgress = _world.UtcNow;
            _wedgeMemoryUsed = false;
            _stallJumps = 0;
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
        ReleaseCombatHold();
        _world.ReleaseDescent();
        ReleaseAutoSnipe();
        FailReason = reason;
        Status = StepStatus.Failed;
        return Status;
    }

    private static float StopDistanceFor(QuestStep step)
        => step.StopDistance ?? (step.Kind == StepKind.WalkTo ? WalkToStopDistance : DefaultStopDistance);

    private static string Fmt(Vector3 v) => $"({v.X:F0},{v.Y:F0},{v.Z:F0})";
}
