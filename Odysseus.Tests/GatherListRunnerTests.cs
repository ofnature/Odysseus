using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

public class GatherListRunnerTests
{
    /// <summary>A gatherer that puts one of the item in the bag per tick, or faults on cue.</summary>
    private sealed class Own : IOwnGatherer
    {
        public Dictionary<uint, int> Bag { get; } = new();
        public HashSet<uint> Unplaceable { get; } = [];
        public HashSet<uint> Faults { get; } = [];
        public Dictionary<uint, uint> Zones { get; } = new();
        public List<(uint Item, int Count)> Starts { get; } = [];
        public int Stops { get; private set; }
        private uint _item;
        private int _left;

        public bool Enabled { get; set; } = true;
        public bool CanGather(uint itemId, uint territoryHint = 0) => !Unplaceable.Contains(itemId);
        public string WhyNot(uint itemId, uint territoryHint = 0) => "the sheets name no gathering point";
        public uint? ZoneOf(uint itemId) => Zones.TryGetValue(itemId, out var z) ? z : null;
        public string Where(uint itemId) => string.Empty;
        public bool Start(uint itemId, int count, int collectability, uint territoryHint = 0)
        {
            Starts.Add((itemId, count));
            _item = itemId; _left = count; Busy = true; Faulted = false;
            return true;
        }
        public void Tick()
        {
            if (!Busy) return;
            if (Faults.Contains(_item)) { Faulted = true; Busy = false; Status = "the node would not open"; return; }
            Bag[_item] = Bag.GetValueOrDefault(_item) + 1;
            if (--_left <= 0 || Cut(_item)) Busy = false;
        }
        public bool Busy { get; private set; }
        public bool Faulted { get; private set; }
        public string Status { get; set; } = string.Empty;
        public bool DryRun { get; set; }
        public bool ProbeOnly { get; set; }
        public void Stop() { Stops++; Busy = false; }

        /// <summary>Item → when its timed node is next up; absent means not timed.</summary>
        public Dictionary<uint, DateTime> UpAt { get; } = new();
        /// <summary>Items whose window closes on them: the gather stops after one each time.</summary>
        public HashSet<uint> WindowCloses { get; } = [];
        public DateTime Now { get; set; } = new(2026, 9, 27, 21, 0, 0, DateTimeKind.Utc);
        public TimeSpan? WaitFor(uint itemId, DateTime utc)
        {
            if (!UpAt.TryGetValue(itemId, out var up)) return null;
            if (WindowEnds.TryGetValue(itemId, out var end) && utc >= end) return up.AddHours(1) - utc;   // the next one
            return up <= utc ? TimeSpan.Zero : up - utc;
        }
        /// <summary>Item → when its current window ends.</summary>
        public Dictionary<uint, DateTime> WindowEnds { get; } = new();
        public TimeSpan? UpFor(uint itemId, DateTime utc)
            => WindowEnds.TryGetValue(itemId, out var end) && WaitFor(itemId, utc) == TimeSpan.Zero ? end - utc : null;
        public bool Cut(uint item) => WindowCloses.Contains(item);
        public bool AtNode { get; set; }
        public uint Gathering => Busy ? _item : 0;
    }

    private static GatherList List(string name, params (uint Item, int Target)[] items) => new()
    {
        Name = name,
        Items = items.Select(i => new GatherListItem { ItemId = i.Item, TargetCount = i.Target }).ToList(),
    };

    private static (GatherListRunner Runner, Own Own, List<string> Log) Make()
    {
        var own = new Own();
        var log = new List<string>();
        var runner = new GatherListRunner(own, id => own.Bag.GetValueOrDefault(id), id => $"item {id}", log.Add, now: () => own.Now);
        return (runner, own, log);
    }

    /// <summary>
    /// Grade 3 Shroud Topsoil grows only on an unspoiled node. The rest of the list is gathered
    /// first; then the run waits, saying for what and how long, and goes the moment it is up.
    /// </summary>
    [Fact]
    public void A_timed_item_waits_for_its_window_while_the_rest_is_gathered()
    {
        var (runner, own, _) = Make();
        own.UpAt[7763] = own.Now.AddMinutes(10);

        Assert.True(runner.Begin([List("granite", (7008, 2), (7763, 3))]));
        Run(runner, 20);

        Assert.Equal(GatherListRunState.Running, runner.State);
        Assert.Equal([(7008u, 2)], own.Starts);                       // the ordinary one, now
        Assert.StartsWith("Waiting for item 7763 — its node is up in 10:00", runner.Status);

        own.Now = own.Now.AddMinutes(10);                             // the window opens
        Run(runner);
        Assert.Equal(GatherListRunState.Done, runner.State);
        Assert.Equal(3, own.Bag[7763]);
        Assert.All(runner.Outcomes, o => Assert.True(o.Reached));
    }

    /// <summary>
    /// The window opens while an ordinary item is being gathered: the node in hand is finished, the
    /// timed one is gathered while it is up, and the ordinary item is taken up again after.
    /// </summary>
    [Fact]
    public void A_timed_node_opening_mid_item_waits_for_this_node_then_comes_first()
    {
        var (runner, own, log) = Make();
        own.UpAt[7763] = own.Now.AddMinutes(10);

        Assert.True(runner.Begin([List("granite", (7008, 50), (7763, 2))]));
        runner.Tick();                                                 // granite starts
        runner.Tick(); runner.Tick();                                  // two granite in
        Assert.Equal(7008u, own.Gathering);

        own.Now = own.Now.AddMinutes(10);                              // the topsoil node opens
        own.AtNode = true;                                             // …mid-node
        runner.Tick();
        Assert.Equal(7008u, own.Gathering);                            // this node is finished first
        Assert.Contains(log, l => l.Contains("finishing this node, then going for it"));

        own.AtNode = false;                                            // node done, walking on
        runner.Tick();
        Assert.Equal(1, own.Stops);
        Run(runner, 20);

        var order = own.Starts.Select(s => s.Item).ToList();
        Assert.Equal([7008u, 7763u, 7008u], order.Take(3));             // granite, topsoil, granite again
        Assert.Equal(2, own.Bag[7763]);
    }

    /// <summary>
    /// Worked, Stop, Gather again in the same window: the node is gone until the next one, so the
    /// item waits rather than sending the run round its three empty spots.
    /// </summary>
    [Fact]
    public void A_timed_item_worked_this_window_waits_for_the_next_even_after_a_restart()
    {
        var (runner, own, _) = Make();
        own.UpAt[7763] = own.Now;
        own.WindowEnds[7763] = own.Now.AddMinutes(8);
        own.WindowCloses.Add(7763);                                    // one per node

        Assert.True(runner.Begin([List("topsoil", (7763, 5))]));
        runner.Tick(); runner.Tick();                                  // one gathered, node spent
        runner.Stop();

        Assert.True(runner.Begin([List("topsoil", (7763, 5))]));
        Run(runner, 10);
        Assert.Single(own.Starts);                                     // not sent again this window
        Assert.StartsWith("Waiting for item 7763", runner.Status);
    }

    [Fact]
    public void A_window_that_closes_mid_gather_is_waited_out_and_tried_again()
    {
        var (runner, own, log) = Make();
        own.UpAt[7763] = own.Now;                                      // up now
        own.WindowCloses.Add(7763);                                    // one per window

        Assert.True(runner.Begin([List("topsoil", (7763, 2))]));
        Run(runner, 20);
        Assert.Equal(2, own.Bag[7763]);                                // a second window finished it
        Assert.Equal(2, own.Starts.Count);
        Assert.Contains(log, l => l.Contains("back in the queue for the next one"));
        Assert.Equal(GatherListRunState.Done, runner.State);
    }

    [Fact]
    public void A_timed_item_that_never_fills_is_given_up_after_three_windows()
    {
        var (runner, own, _) = Make();
        own.UpAt[7763] = own.Now;
        own.Faults.Add(7763);                                          // the node is never found

        Assert.True(runner.Begin([List("topsoil", (7763, 5))]));
        Run(runner, 50);
        Assert.Equal(GatherListRunState.Done, runner.State);
        Assert.Equal(3, own.Starts.Count);
        Assert.False(Assert.Single(runner.Outcomes).Reached);
    }

    private static void Run(GatherListRunner runner, int ticks = 500)
    {
        for (var i = 0; i < ticks && runner.State == GatherListRunState.Running; i++)
            runner.Tick();
    }

    [Fact]
    public void Short_items_are_gathered_zone_by_zone_up_to_their_targets()
    {
        var (runner, own, _) = Make();
        own.Zones[10] = 2; own.Zones[20] = 1;   // the list names 10 first; zone order puts 20 first
        own.Bag[20] = 1;

        Assert.True(runner.Begin([List("workshop", (10, 3), (20, 2))]));
        Run(runner);

        Assert.Equal(GatherListRunState.Done, runner.State);
        Assert.Equal([(20u, 1), (10u, 3)], own.Starts);   // one short, three short
        Assert.Equal(3, own.Bag[10]);
        Assert.Equal(2, own.Bag[20]);
        Assert.All(runner.Outcomes, o => Assert.True(o.Reached));
    }

    [Fact]
    public void Held_items_and_disabled_lists_leave_nothing_to_do()
    {
        var (runner, own, _) = Make();
        own.Bag[10] = 5;
        Assert.False(runner.Begin([List("full", (10, 3))]));
        Assert.Contains("Nothing", runner.Status);

        var off = List("off", (20, 3));
        off.Enabled = false;
        Assert.False(runner.Begin([off]));
        Assert.Empty(own.Starts);
    }

    [Fact]
    public void An_unplaceable_item_is_skipped_with_its_reason_and_the_run_goes_on()
    {
        var (runner, own, log) = Make();
        own.Unplaceable.Add(10);

        Assert.True(runner.Begin([List("l", (10, 2), (20, 2))]));
        Run(runner);

        Assert.Equal(GatherListRunState.Done, runner.State);
        Assert.Equal([(20u, 2)], own.Starts);
        var skipped = Assert.Single(runner.Outcomes, o => o.ItemId == 10);
        Assert.Contains("no gathering point", skipped.Note);
        Assert.Contains(log, m => m.Contains("skipped"));
    }

    [Fact]
    public void A_faulting_item_does_not_end_the_run()
    {
        var (runner, own, _) = Make();
        own.Faults.Add(10);

        Assert.True(runner.Begin([List("l", (10, 2), (20, 2))]));
        Run(runner);

        Assert.Equal(GatherListRunState.Done, runner.State);
        var faulted = Assert.Single(runner.Outcomes, o => o.ItemId == 10);
        Assert.Contains("gave up", faulted.Note);
        Assert.Contains("would not open", faulted.Note);
        Assert.True(Assert.Single(runner.Outcomes, o => o.ItemId == 20).Reached);
    }

    [Fact]
    public void The_same_item_on_two_lists_is_gathered_once_to_the_higher_target()
    {
        var (runner, own, _) = Make();
        Assert.True(runner.Begin([List("a", (10, 2)), List("b", (10, 5))]));
        Run(runner);

        Assert.Equal([(10u, 5)], own.Starts);
        Assert.Equal(5, own.Bag[10]);
    }

    [Fact]
    public void A_full_bag_ends_the_run_with_the_reason()
    {
        var own = new Own();
        var slots = 3;
        var runner = new GatherListRunner(own, id => own.Bag.GetValueOrDefault(id), id => $"item {id}", _ => { },
            freeSlots: () => slots);
        Assert.True(runner.Begin([List("l", (10, 50))]));
        runner.Tick(); runner.Tick();
        Assert.Equal(10u, runner.CurrentItem);

        slots = 0;
        runner.Tick();
        Assert.Equal(GatherListRunState.Done, runner.State);
        Assert.Contains("bag is full", runner.Status);
        Assert.Equal(1, own.Stops);
        Assert.Contains("bag is full", Assert.Single(runner.Outcomes).Note);
    }

    [Fact]
    public void Worn_gear_is_repaired_between_items_before_the_next_starts()
    {
        var own = new Own();
        var world = new FakeStepWorld { LowestGearConditionPercent = 20 };
        var repair = new Odysseus.Services.Run.GearRepair(world, _ => { });
        var runner = new GatherListRunner(own, id => own.Bag.GetValueOrDefault(id), id => $"item {id}", _ => { },
            repair, repairAt: () => 30);
        Assert.True(runner.Begin([List("l", (10, 2))]));

        // The repair runs first; the item does not start until it is done.
        for (var i = 0; i < 12 && own.Starts.Count == 0; i++) { runner.Tick(); world.Advance(0.5); }
        Assert.Contains("RepairAll", world.Calls);
        Assert.Equal(100, world.LowestGearConditionPercent);
        Assert.Single(own.Starts);
        Assert.True(world.Calls.IndexOf("CloseRepair") < world.Calls.Count, "repair window left open");

        Run(runner);
        Assert.Equal(GatherListRunState.Done, runner.State);
        Assert.True(Assert.Single(runner.Outcomes).Reached);
    }

    [Fact]
    public void Stop_stops_the_gatherer_mid_item()
    {
        var (runner, own, _) = Make();
        Assert.True(runner.Begin([List("l", (10, 50))]));
        runner.Tick();   // starts the item
        runner.Tick();   // one in the bag
        Assert.Equal(10u, runner.CurrentItem);

        runner.Stop();
        Assert.Equal(1, own.Stops);
        Assert.Equal(GatherListRunState.Idle, runner.State);
        Assert.Null(runner.CurrentItem);
    }
}
