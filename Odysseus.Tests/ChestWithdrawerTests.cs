using Odysseus.Services.Quest;

namespace Odysseus.Tests;

/// <summary>
/// Fetching what a line is short of out of the FC chest. Whole stacks, one at a time, verified by
/// the slot emptying — the constraints are the game's, not choices.
/// </summary>
public class ChestWithdrawerTests
{
    private const uint Ore = 5106;
    private const uint Leather = 5275;

    private sealed class Fake : IChestWorld
    {
        public DateTime UtcNow { get; set; } = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        public bool ChestOpen { get; set; } = true;
        public Dictionary<uint, int> Bag { get; } = new();
        /// <summary>Item → the stacks sitting on loaded chest pages.</summary>
        public Dictionary<uint, List<int>> Chest { get; } = new();
        public List<string> Calls { get; } = [];
        public bool WithdrawAccepted { get; set; } = true;
        /// <summary>A submitted move that never lands — the bags were full, or the server said no.</summary>
        public bool MoveLands { get; set; } = true;

        private readonly HashSet<(int, short)> _gone = [];

        public IReadOnlyList<ChestStack> ChestStacks(uint itemId)
            => Chest.TryGetValue(itemId, out var stacks)
                ? stacks.Select((q, i) => new ChestStack(1, (short)i, itemId, q))
                    .Where(s => !_gone.Contains((s.Container, s.Slot)))
                    .ToList()
                : [];

        public int Held(uint itemId) => Bag.GetValueOrDefault(itemId);

        public bool Withdraw(ChestStack stack)
        {
            Calls.Add($"Take {stack.Quantity} x {stack.ItemId}");
            if (!WithdrawAccepted) return false;
            if (!MoveLands) return true;   // accepted, then quietly does nothing
            _gone.Add((stack.Container, stack.Slot));
            Bag[stack.ItemId] = Bag.GetValueOrDefault(stack.ItemId) + stack.Quantity;
            return true;
        }

        public bool HasLeft(ChestStack stack) => _gone.Contains((stack.Container, stack.Slot));

        public void Log(string message) => Calls.Add("Log " + message);

        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }

    private static void Run(ChestWithdrawer w, Fake world, int ticks = 60)
    {
        for (var i = 0; i < ticks && w.Busy; i++) { w.Tick(); world.Advance(0.3); }
    }

    [Fact]
    public void It_brings_back_a_stack_that_covers_the_shortfall()
    {
        var world = new Fake();
        world.Chest[Ore] = [30];
        var w = new ChestWithdrawer(world);

        Assert.Equal(1, w.Start([(Ore, 30)]));
        Run(w, world);

        Assert.Contains($"Take 30 x {Ore}", world.Calls);
        Assert.Equal(30, world.Bag[Ore]);
        Assert.Equal(1, w.Last!.Moved);
    }

    /// <summary>
    /// MoveItemSlot has no quantity parameter, so a stack is all-or-nothing. Asking for 6 out of a
    /// stack of 99 brings all 99 — stated rather than worked around.
    /// </summary>
    [Fact]
    public void A_partial_need_still_takes_the_whole_stack()
    {
        var world = new Fake();
        world.Chest[Ore] = [99];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 6)]);
        Run(w, world);

        Assert.Equal(99, world.Bag[Ore]);
    }

    /// <summary>Given a choice, take the smallest stack that still covers it — least excess carried.</summary>
    [Fact]
    public void The_smallest_covering_stack_is_preferred()
    {
        var world = new Fake();
        world.Chest[Ore] = [99, 20, 50];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 15)]);
        Run(w, world);

        Assert.Contains($"Take 20 x {Ore}", world.Calls);
        Assert.Equal(20, world.Bag[Ore]);
    }

    /// <summary>No single stack covers it, so it keeps taking until it does.</summary>
    [Fact]
    public void Several_stacks_are_taken_until_the_need_is_met()
    {
        var world = new Fake();
        world.Chest[Ore] = [10, 10, 10];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 25)]);
        Run(w, world);

        Assert.Equal(30, world.Bag[Ore]);
        Assert.Equal(3, w.Last!.Moved);
    }

    [Fact]
    public void What_is_already_held_is_not_fetched_again()
    {
        var world = new Fake();
        world.Bag[Ore] = 30;
        world.Chest[Ore] = [99];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 30)]);
        Run(w, world);

        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Take"));
        Assert.Equal(1, w.Last!.Covered);
    }

    /// <summary>
    /// An unviewed page is unreadable, not empty. The wording must never claim the chest does not
    /// hold something when all we know is that we cannot see it.
    /// </summary>
    [Fact]
    public void An_item_on_no_loaded_page_is_reported_as_not_found_not_as_absent()
    {
        var world = new Fake();
        var w = new ChestWithdrawer(world);

        w.Start([(Leather, 3)]);
        Run(w, world);

        Assert.Equal(1, w.Last!.Short);
        Assert.Contains(world.Calls, c => c.Contains("none on the loaded chest pages"));
        Assert.Contains("not found on a loaded page", w.Status);
    }

    /// <summary>The chest window is the transfer session; without it nothing can move at all.</summary>
    [Fact]
    public void It_refuses_to_start_with_the_chest_shut()
    {
        var world = new Fake { ChestOpen = false };
        world.Chest[Ore] = [30];
        var w = new ChestWithdrawer(world);

        Assert.Equal(0, w.Start([(Ore, 30)]));
        Assert.False(w.Busy);
        Assert.Contains("not open", w.Status);
    }

    [Fact]
    public void Closing_the_chest_mid_run_stops_cleanly()
    {
        var world = new Fake();
        world.Chest[Ore] = [10, 10, 10];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 25)]);
        w.Tick();
        world.Advance(0.3);
        world.ChestOpen = false;
        Run(w, world);

        Assert.False(w.Busy);
        Assert.Contains("chest closed", w.Status);
    }

    /// <summary>
    /// MoveItemSlot's return code cannot be trusted — it comes back 6 on moves that worked — so a
    /// move is only counted once the source slot has actually emptied.
    /// </summary>
    [Fact]
    public void A_move_that_never_lands_is_not_counted()
    {
        var world = new Fake { MoveLands = false };
        world.Chest[Ore] = [30];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 30)]);
        Run(w, world);

        Assert.Equal(0, world.Bag.GetValueOrDefault(Ore));
        Assert.Equal(0, w.Last!.Moved);
    }

    [Fact]
    public void Nothing_missing_is_a_no_op()
    {
        var world = new Fake();
        var w = new ChestWithdrawer(world);

        Assert.Equal(0, w.Start([(Ore, 0)]));
        Assert.False(w.Busy);
        Assert.Contains("nothing missing", w.Status);
    }
}
