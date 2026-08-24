using System;
using System.Collections.Generic;
using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Services.Gathering;

/// <summary>What the runner needs of the game beyond travel, which it borrows from the executor.</summary>
public interface IGatherWorld
{
    DateTime UtcNow { get; }
    uint TerritoryId { get; }
    Vector3 PlayerPosition { get; }
    bool IsOccupied { get; }

    /// <summary>The class being played. A node is worked as a Miner or a Botanist and no one else.</summary>
    uint CurrentClassJob { get; }

    /// <summary>Equip a gearset for this class. False when the character has none, with a reason.</summary>
    bool EquipGearsetFor(uint classJobId, out string reason);

    bool IsMounted { get; }

    /// <summary>Get off. A node cannot be worked from the saddle.</summary>
    void Dismount();

    /// <summary>How many of the item are held at or above a collectability.</summary>
    int CollectableCount(uint itemId, int minimumCollectability);

    /// <summary>The node object, if it has spawned at the spot we walked to.</summary>
    bool IsDataIdSpawned(uint dataId);

    /// <summary>How far the node actually is, or null when it is not loaded.</summary>
    float? DistanceToDataId(uint dataId);

    /// <summary>Where the node actually is, which beats where it was recorded.</summary>
    Vector3? PositionOfDataId(uint dataId);

    /// <summary>
    /// The nearest node among these that is <i>up</i> — loaded and targetable — or null when none
    /// is. Targetable is what tells a live node from one that has been worked out and is waiting to
    /// move, which is the difference between walking to something and walking past it. An empty set
    /// means any node at all, which is what a diagnostic wants.
    /// </summary>
    (uint NodeId, Vector3 Position, float Distance)? NearestLiveNode(IReadOnlyCollection<uint> nodeIds);
    void FaceDataId(uint dataId);
    bool TryInteractWithDataId(uint dataId);

    /// <summary>Gathering has begun — the game says so before either window appears.</summary>
    bool NodeOpen { get; }

    /// <summary>The item list is actually on screen, so a slot can be chosen.</summary>
    bool ItemListOpen { get; }

    /// <summary>
    /// A gathering action — or the reveal animation when a node first opens — is still playing.
    /// Anything fired into the window while this is set is swallowed without a word.
    /// </summary>
    bool ExecutingAction { get; }

    /// <summary>The window's own numbers, or null when it is not open or not a collectable node.</summary>
    CollectableState? Collectable { get; }

    /// <summary>Choose the slot yielding this item, so the node's other offerings are left alone.</summary>
    bool SelectSlotFor(uint itemId);

    bool UseAction(uint actionId);

    /// <summary>Stop dead. Interacting while still being moved is refused by the game.</summary>
    void StopMoving();

    /// <summary>The condition flags, for the log — what the game thought was true at the time.</summary>
    string Conditions { get; }

    /// <summary>Write what the open window contains to the log, without touching it.</summary>
    void DescribeOpenWindow();

    /// <summary>Starting on a fresh node: whatever was tried on the last one does not carry over.</summary>
    void ForgetSlotAttempts();

    void CloseNode();
    void Log(string message);
}

/// <summary>Where a run has got to.</summary>
public enum GatherRunState
{
    Idle, SwitchingJob, Travelling, Walking, Opening, Working,
    /// <summary>Finished or told to stop, and seeing the gathering window shut before saying so.</summary>
    Closing,
    Done, Faulted,
}

/// <summary>
/// Gathers a number of one collectable.
///
/// <para>
/// The shape is: go to the zone, walk to a spot the node spawns at, open it, and work it with
/// <see cref="CollectableRotation"/> until the bag holds enough at the collectability wanted. A node
/// is spent after a few attempts and re-appears at one of its <i>other</i> fixed spots, so running
/// out is the normal case rather than a failure: the runner walks to the next spot on the list and
/// carries on, cycling round.
/// </para>
///
/// <para>
/// Travel is the executor's, not a second implementation of it. Aetherytes, mounts, the navmesh and
/// every fix that went into them come along for free.
/// </para>
/// </summary>
public sealed class GatherRunner
{
    /// <summary>
    /// How far a loaded node may be from a recorded spot and still count as being at it. The spots
    /// are distinct places a node moves between, so this only has to cover the difference between
    /// where it was measured and where it stands, not the distance between spots.
    /// </summary>
    private const float SpotRadius = 12f;

    private static readonly TimeSpan JobWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DismountRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(12);

    /// <summary>
    /// How long to leave an interaction alone before trying again, and how few times to try.
    ///
    /// <para>
    /// Opening a node is not something to repeat while one is already in flight: it takes up to a
    /// second for the game to report gathering has begun, and firing again into that gap wedged a
    /// client. GatherBuddy allows one interaction and then keeps its hands off for five seconds.
    /// </para>
    /// </summary>
    private static readonly TimeSpan OpenRetry = TimeSpan.FromSeconds(4);

    /// <summary>How long the game gets to put a window up once gathering has begun.</summary>
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(8);

    /// <summary>And how long it gets to take one down before we stop holding everyone up.</summary>
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(15);
    /// <summary>One ask per visit, as GatherBuddy does. Repeats are what wedge a client.</summary>
    private const int MaxOpens = 1;

    /// <summary>How level with a node you have to be. GatherBuddy uses three.</summary>
    private const float VerticalReach = 3f;

    /// <summary>How long to let a stop land before interacting. A frame would do; this is safe.</summary>
    private static readonly TimeSpan SettleAfterStopping = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MoveWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ActionGap = TimeSpan.FromSeconds(1);

    /// <summary>Leave this much GP so the next node is not started empty.</summary>
    public int GpReserve { get; set; }

    /// <summary>
    /// Walk the whole run and touch nothing. Everything happens except the interaction itself: the
    /// class switch, the travel, finding what is up, getting level with it — and then a line in the
    /// log saying what it would have done. For when the thing being tested has been costing a
    /// client per attempt and the movement is what needs proving.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Open one node, write down everything the window says, close it, and stop. The step between
    /// "walk to it" and "work it": it proves the interaction on its own, without firing the slot
    /// callback — which is the one part still built on a guess about the window's layout, and the
    /// likeliest thing to leave a window that never populates.
    /// </summary>
    public bool ProbeOnly { get; set; }

    private readonly IGatherWorld _world;
    private readonly StepExecutor _travel;

    /// <summary>One place to stand, and the node that stands there.</summary>
    private readonly record struct Stop(uint NodeId, Vector3 Position);

    private readonly List<Stop> _stops = [];
    private GatheringTarget? _target;
    private uint _itemId;
    private int _wanted;
    private int _baseline;
    private int _collectability;
    private int _spawn;
    private int _visited;
    private DateTime _phaseStart;
    private DateTime _lastAction;
    private bool _scrutinyUsed;
    private int _opens;
    private uint _currentNode;
    private Vector3 _travelTo;
    private readonly List<uint> _nodeIds = [];
    private bool _lastOpenAccepted;
    private DateTime _stoppedAt;
    private DateTime _slotChosenAt;
    private GatherRunState _afterClose = GatherRunState.Done;

    public GatherRunner(IGatherWorld world, StepExecutor travel)
    {
        _world = world;
        _travel = travel;
    }

    public GatherRunState State { get; private set; } = GatherRunState.Idle;
    public string Status { get; private set; } = string.Empty;
    public string FailReason { get; private set; } = string.Empty;

    /// <summary>How many are held at the collectability asked for.</summary>
    public int Held => _target is null ? 0 : _world.CollectableCount(_itemId, _collectability);

    /// <summary>
    /// How many this run has added. The ask is a <i>shortfall</i> — "get N more" — and judging it
    /// against the whole bag ends a run before it moves: three already held against an ask of one
    /// read as finished, while the delivery still counted itself short.
    /// </summary>
    public int Gathered => Held - _baseline;

    /// <summary>Work one node's spots. Kept for a caller that has already chosen.</summary>
    public void Begin(GatheringTarget target, int count, int minimumCollectability)
        => Begin([target], count, minimumCollectability);

    /// <summary>
    /// Work every node that yields the item, not just the best one.
    ///
    /// <para>
    /// An item is usually offered by several nodes in the same zone — Glass Eye by eleven of them,
    /// nineteen spots between them — and only some are up at any moment. Working one node's spots
    /// and giving up means walking a three-stop circuit past eight other nodes that have what you
    /// came for.
    /// </para>
    /// </summary>
    public void Begin(IReadOnlyList<GatheringTarget> targets, int count, int minimumCollectability)
    {
        var target = targets[0];
        _stops.Clear();
        _nodeIds.Clear();
        foreach (var t in targets)
        {
            if (t.TerritoryId != target.TerritoryId || t.ClassJobId != target.ClassJobId)
                continue; // one trip, one class
            _nodeIds.Add(t.NodeId);
            foreach (var spawn in t.Spawns)
                _stops.Add(new Stop(t.NodeId, spawn));
        }

        _target = target;
        _itemId = target.ItemId;
        _wanted = count;
        _collectability = minimumCollectability;
        _spawn = 0;
        _visited = 0;
        _baseline = _world.CollectableCount(target.ItemId, minimumCollectability);
        _scrutinyUsed = false;
        _currentNode = target.NodeId;
        _travelTo = default;
        FailReason = string.Empty;
        Enter(GatherRunState.SwitchingJob);
    }

    public void Cancel()
    {
        _travel.Cancel();
        _target = null;

        // A window still open — or an action still playing — outlives the wish to stop, and the
        // caller's next move is usually a teleport the game refuses while it is up. So stopping
        // with a node open is a state, not an instant: Busy holds until the window is shut.
        if (_world.NodeOpen || _world.ExecutingAction)
        {
            _afterClose = GatherRunState.Idle;
            Enter(GatherRunState.Closing);
            return;
        }

        State = GatherRunState.Idle;
        Status = string.Empty;
    }

    public GatherRunState Tick()
    {
        // Closing runs even with no target: a cancelled run still owns its open window.
        if (State == GatherRunState.Closing)
        {
            TickClosing();
            return State;
        }

        if (_target is null || State is GatherRunState.Idle or GatherRunState.Done or GatherRunState.Faulted)
            return State;

        // What this run has gathered is the only thing that decides it is finished — but finished
        // still means seeing the window shut, or the teleport that follows is refused into it.
        if (State != GatherRunState.Closing && Gathered >= _wanted)
        {
            _travel.Cancel();
            Status = $"Gathered {_wanted} more of item {_itemId} at {_collectability}+ — closing up.";
            _afterClose = GatherRunState.Done;
            Enter(GatherRunState.Closing);
        }

        if (State == GatherRunState.Closing)
            return State;

        switch (State)
        {
            case GatherRunState.SwitchingJob: TickJob(); break;
            case GatherRunState.Travelling: TickTravel(); break;
            case GatherRunState.Walking: TickWalk(); break;
            case GatherRunState.Opening: TickOpen(); break;
            case GatherRunState.Working: TickWork(); break;
        }
        return State;
    }

    /// <summary>
    /// A node is worked as a Miner or a Botanist. Travelling first and noticing afterwards wastes
    /// the trip, so the class is settled before anything moves.
    /// </summary>
    private void TickJob()
    {
        var target = _target!;
        if (_world.CurrentClassJob == target.ClassJobId)
        {
            Enter(GatherRunState.Travelling);
            return;
        }

        if (_world.UtcNow - _phaseStart > JobWait)
        {
            Fault($"could not switch to class {target.ClassJobId} to work node {target.NodeId}");
            return;
        }

        // Asked once; the rest of the wait is for the game to catch up.
        if (_lastAction != default)
            return;

        _lastAction = _world.UtcNow;
        Status = $"Switching to class {target.ClassJobId}";
        if (!_world.EquipGearsetFor(target.ClassJobId, out var reason))
            Fault(reason.Length > 0 ? reason : $"no gearset for class {target.ClassJobId}");
    }

    private void TickTravel()
    {
        var target = _target!;
        if (_world.TerritoryId == target.TerritoryId)
        {
            _travel.Cancel();
            Enter(GatherRunState.Walking);
            return;
        }

        Status = $"Travelling to territory {target.TerritoryId}";
        if (_travel.Status != StepStatus.Running)
            _travel.Begin(Walk(target.Spawns[0], target.TerritoryId));

        if (_travel.Tick() == StepStatus.Failed)
            Fault($"could not reach territory {target.TerritoryId}: {_travel.FailReason}");
    }

    private void TickWalk()
    {
        var target = _target!;

        // What is actually up, of everything that yields this item. Any live node beats every
        // recorded coordinate: the coordinates only say where nodes *can* be, and most of them are
        // not there at any given moment.
        if (_world.NearestLiveNode(_nodeIds) is { } live)
        {
            _currentNode = live.NodeId;
            Status = $"Node {live.NodeId}, {live.Distance:F0}y away";

            if (live.Distance <= StepExecutor.InteractReach)
            {
                _travel.Cancel();
                _opens = 0;
                _stoppedAt = default;
                _slotChosenAt = default;
                _world.ForgetSlotAttempts();
                Enter(GatherRunState.Opening);
                return;
            }

            if (_travel.Status != StepStatus.Running || _travelTo != live.Position)
            {
                _travelTo = live.Position;
                _travel.Begin(Walk(live.Position, target.TerritoryId));
            }

            if (_travel.Tick() == StepStatus.Failed)
                NextSpot($"node {live.NodeId} could not be reached ({_travel.FailReason})");
            else if (_world.UtcNow - _phaseStart > MoveWait)
                NextSpot($"node {live.NodeId} took too long to reach");
            return;
        }

        // Nothing up within sight. Walk the circuit of recorded spots to bring more into view.
        var stop = _stops[_spawn % _stops.Count];
        Status = $"Nothing up nearby — stop {_spawn % _stops.Count + 1} of {_stops.Count}";

        if (Vector3.Distance(_world.PlayerPosition, stop.Position) <= StepExecutor.DefaultStopDistance + StepExecutor.ArrivalSlack)
        {
            _travel.Cancel();
            NextSpot("nothing up here");
            return;
        }

        if (_travel.Status != StepStatus.Running || _travelTo != stop.Position)
        {
            _travelTo = stop.Position;
            _travel.Begin(Walk(stop.Position, target.TerritoryId));
        }

        if (_travel.Tick() == StepStatus.Failed)
        {
            NextSpot($"could not be reached ({_travel.FailReason})");
            return;
        }

        if (_world.UtcNow - _phaseStart > MoveWait)
            NextSpot("took too long to reach");
    }

    private void TickOpen()
    {
        if (_world.NodeOpen)
        {
            Enter(GatherRunState.Working);
            return;
        }

        if (_world.UtcNow - _phaseStart > OpenWait)
        {
            var reach = _world.DistanceToDataId(_currentNode);
            NextSpot($"would not open after {_opens} attempt(s) — " +
                     $"{(reach is { } d ? $"{d:F1}y away" : "not in the object table")}, " +
                     $"mounted={_world.IsMounted}, last interact returned {_lastOpenAccepted}");
            return;
        }

        // A node cannot be worked from the saddle, and the walk here will have mounted for anything
        // over thirty yalms. Ask, and keep asking while it comes down — a dismount in the air is a
        // descent and you stay mounted the whole way.
        if (_world.IsMounted)
        {
            if (_world.UtcNow - _lastAction > DismountRetry)
            {
                _lastAction = _world.UtcNow;
                _world.Dismount();
            }
            return;
        }

        // Level with it. GatherBuddy requires under three yalms of vertical separation before it
        // will interact, and a node reached from above or below is one the game refuses — which
        // looks from here like an interaction that did nothing.
        if (_world.PositionOfDataId(_currentNode) is { } where
            && Math.Abs(where.Y - _world.PlayerPosition.Y) >= VerticalReach)
        {
            if (_travel.Status != StepStatus.Running)
                _travel.Begin(Walk(where, _target!.TerritoryId));
            _travel.Tick();
            return;
        }

        if (_world.IsOccupied || _world.UtcNow - _lastAction < OpenRetry)
            return;

        if (_opens >= MaxOpens)
            return; // out of tries; the phase clock ends it rather than another keypress

        // Stop, and let the stop land before asking. GatherBuddy is explicit about this — it
        // enqueues the interaction a frame after stopping navigation, "to avoid the 'Unable to
        // execute command while in flight' error" — and stopping and interacting in the same frame
        // is what we were doing instead.
        if (_stoppedAt == default)
        {
            _stoppedAt = _world.UtcNow;
            _world.StopMoving();
            _travel.Cancel();
            return;
        }

        if (_world.UtcNow - _stoppedAt < SettleAfterStopping)
            return;

        _lastAction = _world.UtcNow;
        _opens++;
        _world.FaceDataId(_currentNode);

        if (DryRun)
        {
            // Report and stop. Cycling on would burn the whole circuit in a tenth of a second and
            // end in a fault, which says nothing about the one thing being checked.
            var away = _world.DistanceToDataId(_currentNode);
            var level = _world.PositionOfDataId(_currentNode) is { } at
                ? $"{Math.Abs(at.Y - _world.PlayerPosition.Y):F1}y vertically"
                : "height unknown";
            Status = $"Dry run: node {_currentNode} is reachable and would be opened now.";
            _world.Log($"Dry run: would open node {_currentNode} — " +
                       $"{(away is { } a ? $"{a:F1}y away" : "distance unknown")}, {level}, " +
                       $"mounted={_world.IsMounted}, conditions: {_world.Conditions}");
            _travel.Cancel();
            State = GatherRunState.Done;
            return;
        }

        _world.Log($"Opening node {_currentNode}: {_world.Conditions}");
        _lastOpenAccepted = _world.TryInteractWithDataId(_currentNode);
    }

    private void TickWork()
    {
        // Opened, which is all this was asked to prove. Write down what the window says and leave.
        if (ProbeOnly && _world.NodeOpen)
        {
            if (!_world.ItemListOpen && _world.Collectable is null)
            {
                if (_world.UtcNow - _phaseStart > WindowWait)
                {
                    _world.Log($"Probe: node {_currentNode} began gathering but no window appeared in " +
                               $"{WindowWait.TotalSeconds:F0}s — {_world.Conditions}");
                    _world.CloseNode();
                    State = GatherRunState.Done;
                }
                return;
            }

            _world.Log($"Probe: node {_currentNode} opened — {_world.Conditions}");
            _world.DescribeOpenWindow();
            _world.CloseNode();
            Status = $"Probe: node {_currentNode} opened and was left alone.";
            State = GatherRunState.Done;
            return;
        }

        if (!_world.NodeOpen)
        {
            // Worked out, or closed under us. Either way this node has moved on.
            NextSpot("spent");
            return;
        }

        // The collectable window is up: work it.
        if (_world.Collectable is { } reported)
        {
            if (_world.ExecutingAction || _world.UtcNow - _lastAction < ActionGap)
                return;

            // Target and floor are both the band asked for — GatherBuddy's own delivery rule:
            // never bank below it, never spend integrity chasing past it.
            var state = reported with { Target = _collectability, Minimum = _collectability, GpReserve = GpReserve, ScrutinyUsed = _scrutinyUsed };
            var move = CollectableRotation.Next(state);
            Status = $"Node {_currentNode}: {state.Collectability} of {_collectability}, {state.IntegrityLeft} left, {move}";

            _lastAction = _world.UtcNow;
            // The window does not say whether Scrutiny is up, so remember it. It improves the *next*
            // raise and is spent by it, so only Scrutiny itself leaves it standing.
            _scrutinyUsed = move == GatherMove.Scrutiny;

            var action = CollectableRotation.ActionId(move, _target!.ClassJobId);
            if (action != 0 && !_world.UseAction(action))
                _world.Log($"Gathering action {action} ({move}) was refused.");
            return;
        }

        // Gathering has begun and no window is up yet. The game takes a moment over this, and the
        // one thing that must not happen is walking away in the middle of it: the character is left
        // in a gathering that never finishes, which needs the client killed to escape.
        if (!_world.ItemListOpen)
        {
            if (_world.UtcNow - _phaseStart > WindowWait)
            {
                _world.CloseNode();
                NextSpot($"gathering began but no window appeared in {WindowWait.TotalSeconds:F0}s");
            }
            return;
        }

        // A plain item: each press of its row gathers one, and no collectable window will
        // ever open. Keep pressing while the list stands and the count is short; the node
        // closes itself when its attempts are spent, and the outer check closes up when the
        // bag holds enough.
        if (_collectability <= 0)
        {
            if (_world.ExecutingAction || _world.UtcNow - _lastAction < ActionGap)
                return;
            _lastAction = _world.UtcNow;
            if (_world.SelectSlotFor(_itemId))
                Status = $"Node {_currentNode}: {Gathered} of {_wanted} gathered.";
            else
            {
                _world.CloseNode();
                NextSpot($"has no item {_itemId} to gather");
            }
            return;
        }

        // Already asked for the row: the collectable window is what says whether it took.
        if (_slotChosenAt != default)
        {
            if (_world.UtcNow - _slotChosenAt > WindowWait)
            {
                _world.CloseNode();
                NextSpot($"chose the row for item {_itemId} but the collectable window did not open");
            }
            return;
        }

        // The reveal animation when a node opens sets ExecutingGatheringAction, and a callback
        // fired into it is swallowed silently — which is exactly what the probe logged every time:
        // "opened — Gathering ExecutingGatheringAction". The one build that got through fired so
        // often that a late attempt happened to land after the animation. Wait it out instead.
        if (_world.ExecutingAction || _world.UtcNow - _lastAction < ActionGap)
            return;

        _lastAction = _world.UtcNow;
        if (_world.SelectSlotFor(_itemId))
            _slotChosenAt = _world.UtcNow;
        else
        {
            _world.CloseNode();
            NextSpot($"has no item {_itemId} to collect");
        }
    }

    /// <summary>
    /// See the gathering window shut before reporting anything. The game refuses a close while an
    /// action plays and refuses a teleport while the window stands, so the order is: wait out the
    /// action, ask the window to close, wait until it has.
    /// </summary>
    private void TickClosing()
    {
        if (!_world.NodeOpen && !_world.ExecutingAction && !_world.IsOccupied)
        {
            State = _afterClose;
            if (_afterClose == GatherRunState.Done)
                Status = $"Gathered {_wanted} more of item {_itemId} at {_collectability}+ — done.";
            return;
        }

        // Not forever: past the wait, report anyway and let the caller's travel say what it hits.
        if (_world.UtcNow - _phaseStart > CloseWait)
        {
            _world.Log("The gathering window would not close; carrying on regardless.");
            State = _afterClose;
            return;
        }

        if (_world.ExecutingAction)
            return;

        if (_world.NodeOpen && _world.UtcNow - _lastAction > DismountRetry)
        {
            _lastAction = _world.UtcNow;
            _world.CloseNode();
        }
    }

    /// <summary>Nodes move as they are worked, so the next spot is expected, not a failure.</summary>
    private void NextSpot(string why)
    {
        _travel.Cancel();
        var was = _stops[_spawn % _stops.Count];
        var index = _spawn % _stops.Count + 1;
        _spawn++;
        _visited++;
        _world.Log($"Node {was.NodeId}, stop {index}: {why}; trying the next of {_stops.Count}.");

        // Twice round every stop with nothing to show is not something more walking will fix.
        if (_visited > _stops.Count * 2)
        {
            Fault($"worked all {_stops.Count} stops for item {_itemId} twice without filling the bag");
            return;
        }
        Enter(GatherRunState.Walking);
    }

    /// <summary>
    /// A leg of the circuit. Fly set, because the executor only flies where the zone's aether
    /// currents are done — <c>CanFlyHere</c> gates it — so this is "fly if you can, walk if you
    /// cannot", and the node circuit is exactly the travel it pays off on. Arriving mounted is
    /// already handled: the opening phase dismounts and waits for the ground.
    /// </summary>
    private static QuestStep Walk(Vector3 to, uint territory) => new()
    {
        Kind = StepKind.WalkTo,
        KindName = "WalkTo",
        Position = to,
        TerritoryId = territory,
        Fly = true,
    };

    private void Enter(GatherRunState state)
    {
        State = state;
        _phaseStart = _world.UtcNow;
        _lastAction = default;
    }

    private void Fault(string reason)
    {
        _travel.Cancel();
        FailReason = reason;
        Status = reason;
        State = GatherRunState.Faulted;
    }
}
