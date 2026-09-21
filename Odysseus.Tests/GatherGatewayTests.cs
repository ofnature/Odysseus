using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

public class GatherGatewayTests
{
    /// <summary>A gatherer that fills the bag one item per tick, as GatherListRunnerTests' does.</summary>
    private sealed class Own : IOwnGatherer
    {
        public Dictionary<uint, int> Bag { get; } = new();
        private uint _item;
        private int _left;

        public bool Enabled { get; set; } = true;
        public bool CanGather(uint itemId, uint territoryHint = 0) => true;
        public string WhyNot(uint itemId, uint territoryHint = 0) => string.Empty;
        public uint? ZoneOf(uint itemId) => 1;
        public string Where(uint itemId) => string.Empty;
        public bool Start(uint itemId, int count, int collectability, uint territoryHint = 0)
        {
            _item = itemId; _left = count; Busy = true; return true;
        }
        public void Tick()
        {
            if (!Busy) return;
            Bag[_item] = Bag.GetValueOrDefault(_item) + 1;
            if (--_left <= 0) Busy = false;
        }
        public bool Busy { get; private set; }
        public bool Faulted => false;
        public string Status => string.Empty;
        public bool DryRun { get; set; }
        public bool ProbeOnly { get; set; }
        public void Stop() { Busy = false; }
    }

    private sealed class Fixture
    {
        public Own Gatherer { get; } = new();
        public List<string> Log { get; } = [];
        public HashSet<uint> Ungatherable { get; } = [];
        public bool Busy { get; set; }
        public List<GatherList> Lists { get; } = [];
        public int Saves { get; private set; }
        public GatherListRunner Runner { get; }
        public GatherGateway Gateway { get; }

        public Fixture()
        {
            Runner = new GatherListRunner(Gatherer, id => Gatherer.Bag.GetValueOrDefault(id),
                id => $"item {id}", Log.Add);
            Gateway = new GatherGateway(Runner, Lists, () => Saves++,
                id => !Ungatherable.Contains(id),
                id => $"item {id} is not something this character can gather",
                id => Gatherer.Bag.GetValueOrDefault(id),
                () => Busy,
                Log.Add);
        }

        public void Run(int ticks = 200)
        {
            for (var i = 0; i < ticks && Runner.State == GatherListRunState.Running; i++)
            {
                Runner.Tick();
                Gateway.Tick();
            }
            Gateway.Tick();
        }
    }

    private const string TwoItems = """
        { "requestedBy": "Hephaestus", "items": [ { "itemId": 5111, "targetCount": 3 },
                                                  { "itemId": 5106, "targetCount": 2 } ] }
        """;

    [Fact]
    public void A_request_runs_and_ends_by_itself()
    {
        var f = new Fixture();
        Assert.True(f.Gateway.Start(TwoItems));
        Assert.True(f.Gateway.IsRunning);           // true the moment Start returns, as the gate promises
        Assert.Equal("Hephaestus", f.Gateway.RequestedBy);

        f.Run();

        Assert.Equal(3, f.Gatherer.Bag[5111]);
        Assert.Equal(2, f.Gatherer.Bag[5106]);
        Assert.False(f.Gateway.IsRunning);          // and falls when the run ends, with nothing to poll for
    }

    /// <summary>
    /// The caller's work lives in a list of its own, shown beside the player's: named after it,
    /// "Remove completed" on, so it holds exactly what is still owed and can be finished by hand.
    /// </summary>
    [Fact]
    public void A_request_is_kept_in_a_list_of_its_own_named_after_the_caller()
    {
        var f = new Fixture();
        Assert.True(f.Gateway.Start(TwoItems));

        var list = Assert.Single(f.Lists);
        Assert.Equal("Hephaestus", list.Name);
        Assert.True(list.RemoveCompleted);
        Assert.True(list.Enabled);
        Assert.Equal([5111u, 5106u], list.Items.Select(i => i.ItemId));
        Assert.True(f.Saves > 0);
    }

    /// <summary>
    /// "Volatile": one list, reused. The caller re-reads its bags and replans every time, so a new
    /// request is the whole of what it needs now — it replaces the list, it does not pile onto it.
    /// </summary>
    [Fact]
    public void A_second_request_reuses_the_list_and_replaces_what_it_holds()
    {
        var f = new Fixture();
        Assert.True(f.Gateway.Start(TwoItems));
        f.Run();

        Assert.True(f.Gateway.Start("""{ "requestedBy": "hephaestus", "items": [ { "itemId": 5120, "targetCount": 1 } ] }"""));

        var list = Assert.Single(f.Lists);            // matched however the name is cased
        Assert.Equal([5120u], list.Items.Select(i => i.ItemId));
    }

    [Fact]
    public void A_list_the_player_deleted_is_made_again_on_the_next_request()
    {
        var f = new Fixture();
        f.Gateway.Start(TwoItems);
        f.Run();
        f.Lists.Clear();

        Assert.True(f.Gateway.Start("{ \"requestedBy\": \"Hephaestus\", \"items\": [ { \"itemId\": 5120, \"targetCount\": 1 } ] }"));
        Assert.Equal("Hephaestus", Assert.Single(f.Lists).Name);
    }

    /// <summary>
    /// The list holds what is owed and nothing else. A row the bags already cover is left out, and
    /// a request that is wholly covered starts nothing — and leaves the list empty rather than
    /// holding rows no run will ever come along to prune.
    /// </summary>
    [Fact]
    public void Rows_the_bags_already_cover_are_never_put_on_the_list()
    {
        var f = new Fixture();
        f.Gatherer.Bag[5106] = 50;                      // already have plenty

        Assert.True(f.Gateway.Start(TwoItems));
        Assert.Equal([5111u], Assert.Single(f.Lists).Items.Select(i => i.ItemId));

        f.Run();
        Assert.False(f.Gateway.Start(TwoItems));        // everything covered now
        Assert.Empty(Assert.Single(f.Lists).Items);
        Assert.Contains(f.Log, m => m.Contains("already hold all of them"));
    }

    /// <summary>
    /// A crafting plugin asking for ore must not also send the character off after the player's
    /// crystals list — and must not change it.
    /// </summary>
    [Fact]
    public void A_request_runs_only_its_own_list_and_leaves_the_players_alone()
    {
        var f = new Fixture();
        var crystals = new GatherList
        {
            Name = "crystals", Enabled = true,
            Items = [new GatherListItem { ItemId = 8, TargetCount = 300 }],
        };
        f.Lists.Add(crystals);

        Assert.True(f.Gateway.Start(TwoItems));
        f.Run();

        Assert.False(f.Gatherer.Bag.ContainsKey(8));   // not gathered
        Assert.True(crystals.Enabled);                  // not touched
        Assert.Equal(300, Assert.Single(crystals.Items).TargetCount);
    }

    /// <summary>
    /// The player's "Afterwards" is for the player's own runs. Applied to a request it sent a
    /// crafter to the FC estate between two crafts; the caller says for itself.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Whether_to_go_home_comes_from_the_request(bool asked)
    {
        var f = new Fixture();
        var json = "{ \"requestedBy\": \"Hephaestus\", \"returnHome\": " + (asked ? "true" : "false")
                   + ", \"items\": [ { \"itemId\": 5111, \"targetCount\": 1 } ] }";
        Assert.True(f.Gateway.Start(json));
        f.Run();

        Assert.True(f.Gateway.TakeEndedRequest(out var goHome));
        Assert.Equal(asked, goHome);
        Assert.False(f.Gateway.TakeEndedRequest(out _));   // read once, then cleared
    }

    [Fact]
    public void A_run_the_player_started_is_not_mistaken_for_a_request()
    {
        var f = new Fixture();
        f.Runner.Begin([new GatherList { Name = "mine", Items = [new GatherListItem { ItemId = 5111, TargetCount = 1 }] }]);
        f.Run();
        Assert.False(f.Gateway.TakeEndedRequest(out _));
    }

    /// <summary>
    /// All-or-nothing: Start returning true has to mean "it began", so one row this character
    /// cannot gather refuses the whole request. A run that quietly dropped a row would leave the
    /// caller waiting for a material that was never coming.
    /// </summary>
    [Fact]
    public void One_item_that_cannot_be_gathered_refuses_the_whole_request_and_says_which()
    {
        var f = new Fixture();
        f.Ungatherable.Add(5106);

        Assert.False(f.Gateway.Start(TwoItems));
        Assert.False(f.Gateway.IsRunning);
        Assert.Empty(f.Gatherer.Bag);               // not even the row that was fine
        Assert.Contains(f.Log, m => m.Contains("5106") && m.Contains("Refused"));
    }

    [Fact]
    public void A_request_while_Odysseus_is_busy_is_refused()
    {
        var f = new Fixture { Busy = true };
        Assert.False(f.Gateway.Start(TwoItems));
        Assert.Contains(f.Log, m => m.Contains("already busy"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{ "requestedBy": "Hephaestus", "items": [] }""")]
    [InlineData("""{ "items": [ { "itemId": 0, "targetCount": 3 } ] }""")]
    [InlineData("""{ "items": [ { "itemId": 5111 } ] }""")]
    public void A_request_that_says_nothing_usable_is_refused_rather_than_guessed_at(string json)
    {
        var f = new Fixture();
        Assert.False(f.Gateway.Start(json));
        Assert.False(f.Gateway.IsRunning);
    }

    /// <summary>The friendlier second field: "this many more" on top of what is already held.</summary>
    [Fact]
    public void Quantity_is_counted_on_top_of_the_bag_when_no_target_is_given()
    {
        var f = new Fixture();
        f.Gatherer.Bag[5111] = 10;

        Assert.True(f.Gateway.Start("""{ "items": [ { "itemId": 5111, "quantity": 4 } ] }"""));
        f.Run();

        Assert.Equal(14, f.Gatherer.Bag[5111]);
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_names_are_case_blind()
    {
        var f = new Fixture();
        Assert.True(f.Gateway.Start("""
            { "RequestedBy": "Hephaestus", "returnHome": false, "somethingNew": 42,
              "ITEMS": [ { "ItemID": 5111, "TARGETCOUNT": 1 } ] }
            """));
    }

    [Fact]
    public void Stop_ends_a_run_this_gate_started()
    {
        var f = new Fixture();
        Assert.True(f.Gateway.Start(TwoItems));
        f.Runner.Tick();

        f.Gateway.Stop();

        Assert.False(f.Gateway.IsRunning);
        Assert.Equal(GatherListRunState.Idle, f.Runner.State);
    }

    /// <summary>The gate is not a remote stop button for the player's own gather window.</summary>
    [Fact]
    public void Stop_leaves_a_run_the_player_started_alone()
    {
        var f = new Fixture();
        f.Runner.Begin([new GatherList { Name = "mine", Items = [new GatherListItem { ItemId = 5111, TargetCount = 5 }] }]);
        Assert.Equal(GatherListRunState.Running, f.Runner.State);

        f.Gateway.Stop();

        Assert.Equal(GatherListRunState.Running, f.Runner.State);
    }

    [Fact]
    public void The_status_carries_what_the_caller_needs_and_never_throws()
    {
        var f = new Fixture();
        f.Gateway.Start(TwoItems);
        f.Runner.Tick();

        var json = f.Gateway.StatusJson();
        Assert.Contains("\"state\":\"Running\"", json);
        Assert.Contains("\"external\":true", json);
        Assert.Contains("\"currentItemId\":", json);

        f.Run();
        var done = f.Gateway.StatusJson();
        Assert.Contains("\"outcomes\":", done);
        Assert.Contains("\"reached\":true", done);
    }
}
