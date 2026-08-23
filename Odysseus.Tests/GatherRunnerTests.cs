using System.Numerics;
using Odysseus.Services.Gathering;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class GatherRunnerTests
{
    /// <summary>A node that yields one collectable per pass and then moves on.</summary>
    private sealed class World : IGatherWorld
    {
        public DateTime UtcNow { get; set; } = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        public uint TerritoryId { get; set; } = 1187;
        public Vector3 PlayerPosition { get; set; }
        public bool IsOccupied { get; set; }
        public List<string> Log { get; } = [];
        public uint CurrentClassJob { get; set; } = 16;
        public HashSet<uint> HasGearsetFor { get; } = [16, 17];
        public int Switches { get; private set; }
        public bool IsMounted { get; set; }
        public int Dismounts { get; private set; }

        public bool EquipGearsetFor(uint classJobId, out string reason)
        {
            if (!HasGearsetFor.Contains(classJobId))
            {
                reason = $"no gearset for class {classJobId}";
                return false;
            }
            Switches++;
            CurrentClassJob = classJobId;
            reason = string.Empty;
            return true;
        }

        public void Dismount() { Dismounts++; IsMounted = false; }

        public HashSet<uint> Spawned { get; } = [];
        public int Held { get; set; }
        public bool NodeOpen { get; private set; }

        /// <summary>The list takes a moment to appear after gathering begins, as it does in game.</summary>
        public TimeSpan WindowDelay { get; set; } = TimeSpan.Zero;
        private DateTime _openedAt;
        public bool ItemListOpen => NodeOpen && UtcNow - _openedAt >= WindowDelay;
        public bool ExecutingAction { get; set; }
        public CollectableState? Collectable { get; private set; }
        public List<uint> Actions { get; } = [];

        /// <summary>Attempts left before this node is worked out.</summary>
        public int Integrity { get; set; } = 4;

        /// <summary>What the window prints under the action buttons.</summary>
        public int ScourYield { get; set; } = 450;
        public int MeticulousYield { get; set; } = 400;

        public int CollectableCount(uint itemId, int minimumCollectability) => Held;
        public bool IsDataIdSpawned(uint dataId) => Spawned.Contains(dataId);

        /// <summary>Where the node really is. Unset means it sits on the recorded spot.</summary>
        public Dictionary<uint, Vector3> NodeAt { get; } = new();
        public Vector3? PositionOfDataId(uint dataId)
            => Spawned.Contains(dataId) ? NodeAt.GetValueOrDefault(dataId, PlayerPosition) : null;

        /// <summary>Nodes that are up. Spawned but not here means loaded and spent.</summary>
        public HashSet<uint> Up { get; } = [];

        public (uint NodeId, Vector3 Position, float Distance)? NearestLiveNode(IReadOnlyCollection<uint> nodeIds)
        {
            (uint Id, Vector3 Pos, float D)? best = null;
            foreach (var id in nodeIds)
            {
                if (!Up.Contains(id) || PositionOfDataId(id) is not { } at) continue;
                var d = Vector3.Distance(at, PlayerPosition);
                if (best is null || d < best.Value.D) best = (id, at, d);
            }
            return best is { } b ? (b.Id, b.Pos, b.D) : null;
        }
        public float? DistanceToDataId(uint dataId)
            => PositionOfDataId(dataId) is { } where ? Vector3.Distance(where, PlayerPosition) : null;
        public void FaceDataId(uint dataId) => Log.Add($"face {dataId}");

        /// <summary>Where the node genuinely is; a leftover elsewhere will not open.</summary>
        public Vector3? OpensAt { get; set; }

        /// <summary>The window stays up after the last collect, as a real node's does.</summary>
        public bool HoldWindowOpen { get; set; }

        public bool TryInteractWithDataId(uint dataId)
        {
            if (!Spawned.Contains(dataId)) return false;
            if (OpensAt is { } real && Vector3.Distance(real, PlayerPosition) > 5f) return false;
            NodeOpen = true;
            _openedAt = UtcNow;
            // Only once the list has had time to appear, as the real one does.
            if (WindowDelay == TimeSpan.Zero)
                Collectable = new CollectableState(0, 600, Integrity, 800, false, 0, ScourYield, MeticulousYield);
            return true;
        }

        public int SlotPicks { get; private set; }

        public bool SelectSlotFor(uint itemId)
        {
            if (!ItemListOpen) return false;
            SlotPicks++;
            Collectable = new CollectableState(0, 600, Integrity, 800, false, 0, ScourYield, MeticulousYield);
            return true;
        }

        public bool UseAction(uint actionId)
        {
            Actions.Add(actionId);
            var state = Collectable!.Value;
            if (actionId is 240 or 815) // Collect
            {
                Held++;
                Integrity--;
                if (Integrity <= 0 && !HoldWindowOpen) { NodeOpen = false; Collectable = null; Spawned.Clear(); }
                else Collectable = state with { Collectability = 0, IntegrityLeft = Integrity, ScrutinyUsed = false };
                return true;
            }
            if (actionId is 22185 or 22189) // Scrutiny
            {
                Collectable = state with { ScrutinyUsed = true, Gp = state.Gp - 200 };
                return true;
            }
            Integrity--;
            var raise = actionId is 22184 or 22188 ? MeticulousYield : ScourYield;
            Collectable = state with { Collectability = state.Collectability + raise, IntegrityLeft = Integrity };
            return true;
        }

        public int Stops { get; private set; }
        public void StopMoving() => Stops++;
        public string Conditions => IsMounted ? "Mounted" : "NormalConditions";
        public int Described { get; private set; }
        public void DescribeOpenWindow() => Described++;
        public void ForgetSlotAttempts() { }
        public void CloseNode()
        {
            if (HoldWindowOpen) return; // the game refuses while an action is mid-play
            NodeOpen = false;
            Collectable = null;
        }
        void IGatherWorld.Log(string message) => Log.Add(message);
        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }

    private static GatheringTarget Target(params Vector3[] spawns)
        => new(ItemId: 44850, NodeId: 34838, TerritoryId: 1187, ClassJobId: 16, Level: 90, Spawns: spawns);

    private static (GatherRunner Runner, World World) Ready(GatheringTarget target)
    {
        var world = new World();
        var fake = new FakeStepWorld { TerritoryId = 1187, ArriveOnMove = true };
        var runner = new GatherRunner(world, new StepExecutor(fake));
        world.PlayerPosition = target.Spawns[0];
        fake.PlayerPosition = target.Spawns[0];
        world.Spawned.Add(target.NodeId);
        world.Up.Add(target.NodeId);
        return (runner, world);
    }

    private static void Run(GatherRunner runner, World world, int ticks = 200)
    {
        for (var i = 0; i < ticks && runner.State is not (GatherRunState.Done or GatherRunState.Faulted); i++)
        {
            runner.Tick();
            world.Advance(1.5);
        }
    }

    [Fact]
    public void It_works_a_node_until_the_bag_holds_enough()
    {
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        runner.Begin(target, count: 1, minimumCollectability: 600);
        Run(runner, world);

        Assert.Equal(GatherRunState.Done, runner.State);
        Assert.True(world.Held >= 1);

        // GatherBuddy's shape: Scrutiny opens the push, Meticulous raises and finishes, and the
        // Collect banks it the moment the bar clears.
        Assert.Equal(22185u, world.Actions[0]);
        Assert.Contains(22184u, world.Actions);
        Assert.Contains(240u, world.Actions);
    }

    [Fact]
    public void A_spent_node_sends_the_run_to_the_next_spot_rather_than_failing()
    {
        // Both spots at the same place, so the walk is not what is under test: the node running
        // out and the run carrying on to the next spot is.
        var here = new Vector3(10, 0, 10);
        var target = Target(here, here);
        var (runner, world) = Ready(target);
        world.Integrity = 1; // one swing and it is worked out

        runner.Begin(target, count: 3, minimumCollectability: 600);
        for (var i = 0; i < 40 && runner.State != GatherRunState.Faulted; i++)
        {
            runner.Tick();
            world.Advance(1.5);
            // It re-appears at the next spot, which is where we are already standing.
            if (world is { NodeOpen: false, Spawned.Count: 0 }) { world.Spawned.Add(target.NodeId); world.Integrity = 1; }
        }

        Assert.NotEqual(GatherRunState.Faulted, runner.State);
        Assert.True(world.Held >= 3, $"held {world.Held}");
        Assert.Contains(world.Log, m => m.Contains("trying the next of 2"));
    }

    [Fact]
    public void Twice_round_every_spot_with_nothing_to_show_is_a_fault_not_a_loop()
    {
        var target = Target(new Vector3(10, 0, 10), new Vector3(20, 0, 20));
        var (runner, world) = Ready(target);
        world.Spawned.Clear(); // the node is nowhere

        runner.Begin(target, count: 1, minimumCollectability: 600);
        Run(runner, world, ticks: 400);

        Assert.Equal(GatherRunState.Faulted, runner.State);
        Assert.Contains("twice without filling the bag", runner.FailReason);
    }

    [Fact]
    public void Every_node_that_yields_the_item_is_worked_not_just_the_first()
    {
        // Glass Eye is offered by eleven nodes in one zone, nineteen stops between them. Working
        // one node's spots and giving up walks a short circuit past everything else that has it.
        var a = new GatheringTarget(44850, NodeId: 100, TerritoryId: 1187, ClassJobId: 16, Level: 90,
            Spawns: [new Vector3(0, 0, 0)]);
        // All at our feet, so the walking is not what is under test: which stops exist is.
        var b = new GatheringTarget(44850, NodeId: 200, TerritoryId: 1187, ClassJobId: 16, Level: 90,
            Spawns: [new Vector3(0, 0, 0), new Vector3(0, 0, 0)]);
        // A different zone, and a Botanist's: neither belongs on this trip.
        var elsewhere = new GatheringTarget(44850, NodeId: 300, TerritoryId: 999, ClassJobId: 16, Level: 90,
            Spawns: [new Vector3(0, 0, 0)]);
        var wrongClass = new GatheringTarget(44850, NodeId: 400, TerritoryId: 1187, ClassJobId: 17, Level: 90,
            Spawns: [new Vector3(0, 0, 0)]);

        var world = new World();
        var fake = new FakeStepWorld { TerritoryId = 1187, ArriveOnMove = true };
        var runner = new GatherRunner(world, new StepExecutor(fake));
        runner.Begin([a, b, elsewhere, wrongClass], count: 1, minimumCollectability: 600);

        // Nothing is anywhere, so it walks the whole circuit and says how many stops that was.
        for (var i = 0; i < 400 && runner.State != GatherRunState.Faulted; i++)
        {
            runner.Tick();
            world.Advance(1.5);
        }
        Assert.Contains(world.Log, l => l.Contains("trying the next of 3"));
        Assert.Contains("all 3 stops", runner.FailReason);
    }

    [Fact]
    public void Movement_is_stopped_before_a_node_is_touched()
    {
        // The game refuses an interaction from someone still being moved, and refuses silently —
        // which is indistinguishable from one that worked.
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        runner.Begin(target, count: 1, minimumCollectability: 600);
        Run(runner, world);

        var stopped = world.Stops;
        Assert.True(stopped > 0, "never stopped before interacting");
        Assert.Contains(world.Log, l => l.StartsWith("Opening node"));
    }

    [Fact]
    public void A_dry_run_walks_the_whole_thing_and_touches_nothing()
    {
        var target = Target(new Vector3(10, 0, 10), new Vector3(20, 0, 20));
        var (runner, world) = Ready(target);
        runner.DryRun = true;

        runner.Begin(target, count: 1, minimumCollectability: 600);
        for (var i = 0; i < 200 && runner.State is not (GatherRunState.Done or GatherRunState.Faulted); i++)
        {
            runner.Tick();
            world.Advance(1.5);
        }

        Assert.Contains(world.Log, l => l.StartsWith("Dry run: would open node"));
        Assert.False(world.NodeOpen);
        Assert.Empty(world.Actions);
        Assert.Equal(0, world.Held);

        // Reported once and stopped, rather than burning the circuit and calling it a fault.
        Assert.Equal(GatherRunState.Done, runner.State);
        Assert.Single(world.Log, l => l.StartsWith("Dry run: would open node"));
        Assert.DoesNotContain(world.Log, l => l.Contains("trying the next"));
    }

    [Fact]
    public void Nothing_is_fired_into_the_window_while_an_action_is_still_playing()
    {
        // Opening a node plays a reveal animation that sets ExecutingGatheringAction, and a
        // callback fired during it is swallowed silently — the row never highlights and the
        // collectable window never comes.
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        world.WindowDelay = TimeSpan.FromSeconds(1);
        world.ExecutingAction = true;

        runner.Begin(target, count: 1, minimumCollectability: 600);
        for (var i = 0; i < 20; i++) { runner.Tick(); world.Advance(0.5); }

        Assert.Equal(0, world.SlotPicks);      // the animation never ended, so nothing was asked

        world.ExecutingAction = false;
        Run(runner, world);
        Assert.Equal(GatherRunState.Done, runner.State);
        Assert.True(world.SlotPicks > 0);
    }

    [Fact]
    public void Gathering_that_has_begun_is_waited_out_rather_than_walked_away_from()
    {
        // The game reports gathering before it puts a window up. Deciding "no window, nothing to
        // pick" in that gap and leaving is what strands the character in an interaction that never
        // finishes — the state that needs the client killed.
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        world.WindowDelay = TimeSpan.FromSeconds(3);

        runner.Begin(target, count: 1, minimumCollectability: 600);
        for (var i = 0; i < 8; i++) { runner.Tick(); world.Advance(0.25); }

        // Two seconds in: gathering has begun, no window yet, and nothing has been abandoned.
        Assert.True(world.NodeOpen);
        Assert.Equal(0, world.SlotPicks);
        Assert.DoesNotContain(world.Log, l => l.Contains("trying the next"));

        Run(runner, world);
        Assert.Equal(GatherRunState.Done, runner.State);
        Assert.True(world.SlotPicks > 0, "never picked the item once the list appeared");
    }

    [Fact]
    public void A_node_is_not_interacted_with_over_and_over_while_one_is_in_flight()
    {
        // Firing the interaction again into the second it takes the game to report gathering wedged
        // a client. Few tries, well spaced, and the phase clock ends it rather than another press.
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        world.OpensAt = new Vector3(999, 0, 999);   // it will never open, whatever we do

        runner.Begin(target, count: 1, minimumCollectability: 600);

        // Twelve seconds of trying to open the same node — one phase's worth.
        for (var i = 0; i < 24 && runner.State is not (GatherRunState.Done or GatherRunState.Faulted); i++)
        {
            runner.Tick();
            world.Advance(0.5);
        }

        // Three, spaced four seconds apart, rather than one a second for the whole phase.
        var attempts = world.Log.Count(l => l == $"face {target.NodeId}");
        Assert.InRange(attempts, 1, 3);
    }

    [Fact]
    public void A_node_that_is_up_is_gone_to_even_when_the_circuit_points_elsewhere()
    {
        // The spent one is right where we stand and the live one is across the field. Walking the
        // circuit in order, or to "the nearest object with this id", both go to the wrong one.
        var spent = new GatheringTarget(44850, NodeId: 100, TerritoryId: 1187, ClassJobId: 16, Level: 90,
            Spawns: [new Vector3(0, 0, 0)]);
        var upOne = new GatheringTarget(44850, NodeId: 200, TerritoryId: 1187, ClassJobId: 16, Level: 90,
            Spawns: [new Vector3(50, 0, 0)]);

        var world = new World();
        var fake = new FakeStepWorld { TerritoryId = 1187, ArriveOnMove = true };
        var runner = new GatherRunner(world, new StepExecutor(fake));

        world.PlayerPosition = new Vector3(0, 0, 0);
        world.Spawned.Add(100);                       // loaded, worked out, not targetable
        world.Spawned.Add(200);
        world.Up.Add(200);                            // the only one that is up
        world.NodeAt[100] = new Vector3(0, 0, 0);
        world.NodeAt[200] = new Vector3(50, 0, 0);

        runner.Begin([spent, upOne], count: 1, minimumCollectability: 600);

        for (var i = 0; i < 60 && runner.State is not (GatherRunState.Done or GatherRunState.Faulted); i++)
        {
            runner.Tick();
            world.Advance(1.5);
            if (runner.State == GatherRunState.Walking)
                world.PlayerPosition = new Vector3(50, 0, 0);   // the walk gets us there
        }

        Assert.Equal(GatherRunState.Done, runner.State);
        Assert.Contains(world.Log, l => l == "face 200");
        Assert.DoesNotContain(world.Log, l => l == "face 100");
    }

    [Fact]
    public void Done_is_not_reported_while_the_window_is_still_open()
    {
        // The teleport that follows a finished gather is refused while the gathering window
        // stands — which is exactly how a full bag turned into "teleport to aetheryte 75 was
        // refused". Finished means the window has shut.
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        world.HoldWindowOpen = true;   // the collect leaves the window up, as a real node does

        runner.Begin(target, count: 1, minimumCollectability: 600);
        for (var i = 0; i < 10 && world.Held < 1; i++) { runner.Tick(); world.Advance(1.5); }

        runner.Tick();
        Assert.Equal(GatherRunState.Closing, runner.State);
        Assert.True(world.NodeOpen);

        world.HoldWindowOpen = false;  // the close lands
        for (var i = 0; i < 10 && runner.State != GatherRunState.Done; i++) { runner.Tick(); world.Advance(1.5); }
        Assert.Equal(GatherRunState.Done, runner.State);
        Assert.False(world.NodeOpen);
    }

    [Fact]
    public void The_gathering_class_is_switched_to_before_anything_moves()
    {
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        world.CurrentClassJob = 34; // a Samurai, which is nobody's idea of a Miner

        runner.Begin(target, count: 1, minimumCollectability: 600);
        runner.Tick();

        Assert.Equal(1, world.Switches);
        Assert.Equal(16u, world.CurrentClassJob);
        Run(runner, world);
        Assert.Equal(GatherRunState.Done, runner.State);
    }

    [Fact]
    public void No_gearset_for_the_class_is_said_plainly_rather_than_travelled_to()
    {
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        world.CurrentClassJob = 34;
        world.HasGearsetFor.Clear();

        runner.Begin(target, count: 1, minimumCollectability: 600);
        runner.Tick();

        Assert.Equal(GatherRunState.Faulted, runner.State);
        Assert.Contains("no gearset", runner.FailReason);
        Assert.Empty(world.Actions); // nothing was attempted at a node
    }

    [Fact]
    public void A_node_is_not_worked_from_the_saddle()
    {
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        world.IsMounted = true;

        runner.Begin(target, count: 1, minimumCollectability: 600);
        Run(runner, world);

        Assert.True(world.Dismounts > 0, "never got off the mount");
        var dismounted = world.Log.FindIndex(l => l.StartsWith("face"));
        Assert.True(dismounted >= 0, "never interacted");
        Assert.Equal(GatherRunState.Done, runner.State);
    }

    [Fact]
    public void A_spent_node_left_lying_at_the_first_spot_does_not_hold_the_run_there()
    {
        // The node was worked out at spot 1 and has moved up to spot 3, but an object with its id
        // is still sitting at spot 1. Walking to "the nearest one with this id" goes back to the
        // spent one for ever; the run has to carry on round the spots.
        var first = new Vector3(0, 0, 0);
        var second = new Vector3(60, 0, 0);
        var third = new Vector3(0, 40, 60);
        var target = Target(first, second, third);
        var (runner, world) = Ready(target);

        world.PlayerPosition = first;
        world.NodeAt[target.NodeId] = first;   // the leftover, right where we stand
        world.OpensAt = third;                 // only the one up there actually opens

        runner.Begin(target, count: 1, minimumCollectability: 600);
        for (var i = 0; i < 400 && runner.State is not (GatherRunState.Done or GatherRunState.Faulted); i++)
        {
            runner.Tick();
            world.Advance(1.5);
            // Walking moves us, and the live node is wherever it really is.
            if (runner.State == GatherRunState.Walking && world.Log.Count(l => l.Contains("trying the next")) >= 2)
            {
                world.PlayerPosition = third;
                world.NodeAt[target.NodeId] = third;
            }
        }

        Assert.Contains(world.Log, l => l.Contains("trying the next of 3"));
        Assert.Equal(GatherRunState.Done, runner.State);
    }

    [Fact]
    public void A_node_within_reach_is_worked_even_when_its_recorded_spot_cannot_be_stood_on()
    {
        // The coordinate is up a cliff the mesh will not climb; the node itself is two yalms away.
        // Waiting to stand on the coordinate is how a run ends up circling it.
        var unreachable = new Vector3(0, 20, 0);              // up a face the mesh will not climb
        var target = Target(unreachable);
        var (runner, world) = Ready(target);
        world.PlayerPosition = new Vector3(0, 8, 0);           // as close as the walk can get
        world.NodeAt[target.NodeId] = new Vector3(0, 10, 0);   // the node, two yalms off and reachable

        runner.Begin(target, count: 1, minimumCollectability: 600);
        Run(runner, world);

        Assert.Equal(GatherRunState.Done, runner.State);
        Assert.Contains(world.Log, l => l.StartsWith("face"));
    }

    [Fact]
    public void The_ask_is_a_shortfall_so_stock_already_held_does_not_satisfy_it()
    {
        // The delivery says "get one more". Three already in the bag is why it says that, not a
        // reason to do nothing — judging the ask against the whole bag ended a run in twenty
        // milliseconds while the turn-in still counted itself short.
        var target = Target(new Vector3(10, 0, 10));
        var (runner, world) = Ready(target);
        world.Held = 3;

        runner.Begin(target, count: 1, minimumCollectability: 600);
        Assert.NotEqual(GatherRunState.Done, runner.Tick());

        Run(runner, world);
        Assert.Equal(GatherRunState.Done, runner.State);
        Assert.Equal(4, world.Held);
    }
}
