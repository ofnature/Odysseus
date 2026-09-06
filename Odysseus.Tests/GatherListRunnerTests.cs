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
            if (--_left <= 0) Busy = false;
        }
        public bool Busy { get; private set; }
        public bool Faulted { get; private set; }
        public string Status { get; set; } = string.Empty;
        public bool DryRun { get; set; }
        public bool ProbeOnly { get; set; }
        public void Stop() { Stops++; Busy = false; }
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
        var runner = new GatherListRunner(own, id => own.Bag.GetValueOrDefault(id), id => $"item {id}", log.Add);
        return (runner, own, log);
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
