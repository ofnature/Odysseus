using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class ChocoboKeeperTests
{
    private sealed class World : IChocoboWorld
    {
        public DateTime UtcNow { get; set; } = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        public float CompanionTimeLeft { get; set; }
        public bool CanSummonHere { get; set; } = true;
        public bool IsOccupied { get; set; }
        public Dictionary<uint, int> Bag { get; } = new() { [ChocoboKeeper.GysahlGreens] = 10 };
        public List<string> Log { get; } = [];
        public int Used { get; private set; }

        public int ItemCount(uint itemId) => Bag.GetValueOrDefault(itemId);

        /// <summary>False models the game quietly refusing the summon — nothing comes out.</summary>
        public bool SummonWorks { get; set; } = true;

        public bool UseItem(uint itemId)
        {
            Used++;
            if (SummonWorks) CompanionTimeLeft = 30 * 60;
            Bag[itemId] = Bag.GetValueOrDefault(itemId) - 1;
            return true;
        }

        void IChocoboWorld.Log(string message) => Log.Add(message);
        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private static ChocoboKeeper Keeper(World w, bool enabled = true, bool unlocked = true)
        => new(w, () => enabled, () => unlocked);

    [Fact]
    public void It_feeds_the_bird_when_the_summon_runs_low_and_then_leaves_it_alone()
    {
        var w = new World();
        var keeper = Keeper(w);

        keeper.Tick();
        Assert.Equal(1, w.Used); // nothing out at all

        for (var i = 0; i < 50; i++) { w.Advance(TimeSpan.FromMinutes(1)); keeper.Tick(); }
        Assert.Equal(1, w.Used); // half an hour of quiet, no second green

        w.CompanionTimeLeft = 60; // a minute left
        w.Advance(TimeSpan.FromMinutes(1));
        keeper.Tick();
        Assert.Equal(2, w.Used);
    }

    [Fact]
    public void A_green_is_never_spent_where_a_companion_is_refused()
    {
        var w = new World { CanSummonHere = false };
        var keeper = Keeper(w);
        keeper.Tick();
        Assert.Equal(0, w.Used);

        w.CanSummonHere = true;
        w.IsOccupied = true; // mid-cutscene: it would be swallowed
        keeper.Tick();
        Assert.Equal(0, w.Used);

        w.IsOccupied = false;
        keeper.Tick();
        Assert.Equal(1, w.Used);
    }

    [Fact]
    public void A_refused_summon_is_retried_slowly_rather_than_eating_the_bag()
    {
        var w = new World { SummonWorks = false }; // the game refuses; nothing comes out
        var keeper = Keeper(w);
        keeper.Tick();

        for (var i = 0; i < 60; i++) { w.Advance(TimeSpan.FromSeconds(1)); keeper.Tick(); }
        Assert.Equal(3, w.Used); // once every thirty seconds, not once a frame
    }

    [Fact]
    public void Locked_and_empty_bags_each_say_so_once()
    {
        var locked = new World();
        Keeper(locked, unlocked: false).Tick();
        Keeper(locked, unlocked: false).Tick();
        var lockedKeeper = Keeper(locked, unlocked: false);
        lockedKeeper.Tick();
        lockedKeeper.Tick();
        Assert.Equal(0, locked.Used);
        Assert.Contains(locked.Log, m => m.Contains("My Little Chocobo"));

        var empty = new World();
        empty.Bag[ChocoboKeeper.GysahlGreens] = 0;
        var keeper = Keeper(empty);
        keeper.Tick();
        empty.Advance(TimeSpan.FromMinutes(1));
        keeper.Tick();
        Assert.Equal(0, empty.Used);
        Assert.Single(empty.Log, m => m.Contains("no Gysahl Greens"));
    }

    [Fact]
    public void Off_means_off()
    {
        var w = new World();
        Keeper(w, enabled: false).Tick();
        Assert.Equal(0, w.Used);
        Assert.Empty(w.Log);
    }

    [Fact]
    public void The_unlock_quest_follows_the_grand_company()
    {
        Assert.Equal(701, ChocoboKeeper.UnlockQuestFor(1)); // Maelstrom
        Assert.Equal(700, ChocoboKeeper.UnlockQuestFor(2)); // Twin Adder
        Assert.Equal(702, ChocoboKeeper.UnlockQuestFor(3)); // Immortal Flames
        Assert.Equal(0, ChocoboKeeper.UnlockQuestFor(0));   // not joined one yet
    }
}
