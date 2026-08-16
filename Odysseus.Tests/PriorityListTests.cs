using Odysseus.Services.Quest;

namespace Odysseus.Tests;

public class PriorityListTests
{
    private static readonly QuestCatalog Catalog = new(
    [
        new QuestListing(100, "Alpha", 10, 0, false, [], 0),
        new QuestListing(200, "Beta", 50, 0, false, [100], 1),
        new QuestListing(300, "Gamma", 10, 0, false, [], 0),
        new QuestListing(400, "Delta", 10, 0, false, [], 0),
    ]);

    private sealed class World : IPriorityWorld
    {
        public HashSet<ushort> Complete { get; } = [];
        public HashSet<ushort> Accepted { get; } = [];
        public HashSet<ushort> Paths { get; } = [100, 200, 300, 400];
        public int PlayerLevel { get; set; } = 20;
        public CharacterFacts Character => CharacterFacts.Unknown;
        public bool IsComplete(ushort id) => Complete.Contains(id);
        public bool IsAccepted(ushort id) => Accepted.Contains(id);
        public bool HasPath(ushort id) => Paths.Contains(id);
    }

    [Fact]
    public void Statuses_and_next_ready_follow_the_rules()
    {
        var w = new World();
        var list = new PriorityList(Catalog, [200, 300, 400, 999], persist: true, save: null);
        w.Paths.Remove(400);

        var e = list.Entries(w).ToDictionary(x => x.QuestId, x => x.Status);
        Assert.Equal(PriorityStatus.Locked, e[200]);        // needs 100
        Assert.Equal(PriorityStatus.Ready, e[300]);
        Assert.Equal(PriorityStatus.NoPath, e[400]);
        Assert.Equal(PriorityStatus.UnknownQuest, e[999]);
        Assert.Equal((ushort)300, list.NextReady(w));

        w.Complete.Add(100);
        w.PlayerLevel = 30;
        Assert.Equal(PriorityStatus.LevelTooLow, list.Entries(w).First(x => x.QuestId == 200).Status);
        w.PlayerLevel = 50;
        Assert.Equal((ushort)200, list.NextReady(w));       // first ready in order now
    }

    [Fact]
    public void An_accepted_entry_runs_before_anything_else()
    {
        var w = new World();
        var list = new PriorityList(Catalog, [300, 400], persist: true, save: null);
        w.Accepted.Add(400);
        Assert.Equal((ushort)400, list.NextReady(w));
    }

    [Fact]
    public void Persist_toggle_controls_saving_and_initial_load()
    {
        List<ushort>? saved = null;
        var list = new PriorityList(Catalog, [100, 200], persist: false, save: ids => saved = ids.ToList());
        Assert.Empty(list.Ids);                              // not persisting: saved ids ignored
        list.Add(300);
        Assert.Null(saved);                                  // and nothing written

        list.SetPersist(true);
        Assert.Equal(new ushort[] { 300 }, saved!);          // turning it on writes the current list
        list.Add(400);
        Assert.Equal(new ushort[] { 300, 400 }, saved!);

        list.SetPersist(false);
        Assert.Empty(saved!);                                // turning it off clears the saved copy
        list.Add(100);
        Assert.Empty(saved!);                                // and stays cleared

        var reloaded = new PriorityList(Catalog, [300, 400], persist: true, save: null);
        Assert.Equal(new ushort[] { 300, 400 }, reloaded.Ids);
    }

    [Fact]
    public void Auto_remove_prunes_completed_entries_only_when_on()
    {
        var w = new World();
        var list = new PriorityList(Catalog, [100, 300], persist: true, save: null);
        w.Complete.Add(100);
        Assert.Equal(0, list.Prune(w.IsComplete));
        Assert.Equal(2, list.Count);
        list.AutoRemoveCompleted = true;
        Assert.Equal(1, list.Prune(w.IsComplete));
        Assert.Equal(new ushort[] { 300 }, list.Ids);
    }

    [Fact]
    public void Move_and_remove_keep_order_sane()
    {
        var list = new PriorityList(Catalog, [100, 200, 300], persist: false, save: null);
        list.Add(100); list.Add(200); list.Add(300);
        Assert.True(list.Move(300, -1));
        Assert.Equal(new ushort[] { 100, 300, 200 }, list.Ids);
        Assert.False(list.Move(100, -1));                    // already first
        Assert.True(list.Remove(300));
        Assert.False(list.Add(200));                         // no duplicates
        Assert.Equal(new ushort[] { 100, 200 }, list.Ids);
    }
}
