using Odysseus.Services.Quest;

namespace Odysseus.Tests;

/// <summary>
/// Fetching what a line is short of out of the FC chest — exactly what is short, a stack split
/// through the game's "how many?" prompt when it holds more; one move at a time, verified by the
/// slot emptying or shrinking.
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
        /// <summary>The split move raises its "how many?" prompt.</summary>
        public bool PromptAppears { get; set; } = true;

        private ChestStack? _asking;

        public IReadOnlyList<ChestStack> ChestStacks(uint itemId)
            => Chest.TryGetValue(itemId, out var stacks)
                ? stacks.Select((q, i) => new ChestStack(1, (short)i, itemId, q)).Where(s => s.Quantity > 0).ToList()
                : [];

        public int Held(uint itemId) => Bag.GetValueOrDefault(itemId);

        public bool Withdraw(ChestStack stack)
        {
            Calls.Add($"Take {stack.Quantity} x {stack.ItemId}");
            if (!WithdrawAccepted) return false;
            if (!MoveLands) return true;   // accepted, then quietly does nothing
            Move(stack, stack.Quantity);
            return true;
        }

        public bool WithdrawSome(ChestStack stack, int amount)
        {
            Calls.Add($"Split {stack.Quantity} x {stack.ItemId}");
            if (!WithdrawAccepted) return false;
            if (PromptAppears) _asking = stack;
            return true;
        }

        public bool QuantityPromptOpen => _asking is not null;

        public void AnswerQuantity(int amount)
        {
            Calls.Add($"Answer {amount}");
            if (MoveLands) Move(_asking!, amount);
            _asking = null;
        }

        public int QuantityAt(ChestStack stack) => Chest[stack.ItemId][stack.Slot];

        private void Move(ChestStack stack, int amount)
        {
            Chest[stack.ItemId][stack.Slot] -= amount;
            Bag[stack.ItemId] = Bag.GetValueOrDefault(stack.ItemId) + amount;
        }

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
    /// Six out of a stack of 99 brings six: the stack is split through the "how many?" prompt
    /// (recorded 2026-09-30), and the 93 stay in the chest for the rest of the fleet.
    /// </summary>
    [Fact]
    public void A_partial_need_takes_exactly_what_is_short()
    {
        var world = new Fake();
        world.Chest[Ore] = [99];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 6)]);
        Run(w, world);

        Assert.Contains($"Split 99 x {Ore}", world.Calls);
        Assert.Contains("Answer 6", world.Calls);
        Assert.Equal(6, world.Bag[Ore]);
        Assert.Equal(93, world.Chest[Ore][0]);
        Assert.Contains("brought 6 item(s)", w.Status);
    }

    /// <summary>Given a choice, split the smallest stack that still covers it.</summary>
    [Fact]
    public void The_smallest_covering_stack_is_preferred()
    {
        var world = new Fake();
        world.Chest[Ore] = [99, 20, 50];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 15)]);
        Run(w, world);

        Assert.Contains($"Split 20 x {Ore}", world.Calls);
        Assert.Equal(15, world.Bag[Ore]);
        Assert.Equal(5, world.Chest[Ore][1]);
    }

    /// <summary>No single stack covers it: whole stacks until one would, then split that one.</summary>
    [Fact]
    public void Several_stacks_are_taken_until_the_need_is_met()
    {
        var world = new Fake();
        world.Chest[Ore] = [10, 10, 10];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 25)]);
        Run(w, world);

        Assert.Equal(25, world.Bag[Ore]);
        Assert.Equal(3, w.Last!.Moved);
        Assert.Contains("Answer 5", world.Calls);
    }

    [Fact]
    public void An_exact_stack_is_moved_whole_with_no_prompt()
    {
        var world = new Fake();
        world.Chest[Ore] = [30];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 30)]);
        Run(w, world);

        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Split") || c.StartsWith("Answer"));
    }

    /// <summary>The split asked, but the game never put up the prompt: give that line up, say so.</summary>
    [Fact]
    public void A_split_with_no_prompt_is_given_up_and_said()
    {
        var world = new Fake { PromptAppears = false };
        world.Chest[Ore] = [99];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 6)]);
        Run(w, world);

        Assert.False(w.Busy);
        Assert.Equal(1, w.Last!.Short);
        Assert.Contains(world.Calls, c => c.Contains("never asked how many"));
        Assert.Equal(99, world.Chest[Ore][0]);
    }

    /// <summary>
    /// The need is the shortfall on top of what is held. Read as a total to hold, 2 held and 3 short
    /// fetched 1 — hidden while whole stacks brought far too many anyway.
    /// </summary>
    [Fact]
    public void The_shortfall_is_fetched_on_top_of_what_is_held()
    {
        var world = new Fake();
        world.Bag[Ore] = 2;
        world.Chest[Ore] = [99];
        var w = new ChestWithdrawer(world);

        w.Start([(Ore, 3)]);
        Run(w, world);

        Assert.Contains("Answer 3", world.Calls);
        Assert.Equal(5, world.Bag[Ore]);
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
