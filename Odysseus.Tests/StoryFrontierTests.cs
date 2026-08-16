using Odysseus.Services.Quest;

namespace Odysseus.Tests;

public class StoryFrontierTests
{
    private static readonly QuestCatalog Catalog = new(
    [
        new QuestListing(39, "Coming to Gridania", 1, 0, true, [], 0),
        new QuestListing(107, "Coming to Limsa Lominsa", 1, 0, true, [], 0),
        new QuestListing(594, "Coming to Ul'dah", 1, 0, true, [], 0),
        new QuestListing(85, "Close to Home (LNC)", 1, 0, true, [39], 1),
        new QuestListing(123, "Close to Home (ARC)", 1, 0, true, [39], 1),
        new QuestListing(124, "Close to Home (CNJ)", 1, 0, true, [39], 1),
        new QuestListing(86, "Next", 2, 0, true, [85, 123, 124], 2),
        new QuestListing(511, "A Hero in the Making", 20, 0, true, [86], 1),
        new QuestListing(680, "The Company You Keep (Twin Adder)", 20, 0, true, [511], 1, [681, 682], 2),
        new QuestListing(681, "The Company You Keep (Maelstrom)", 20, 0, true, [511], 1, [680, 682], 2),
        new QuestListing(682, "The Company You Keep (Immortal Flames)", 20, 0, true, [511], 1, [680, 681], 2),
        new QuestListing(700, "After GC", 20, 0, true, [680, 681, 682], 2),
    ]);

    private static (StoryFrontier frontier, FakeQuestStateReader reader) Make(byte preferredGc = 0)
    {
        var reader = new FakeQuestStateReader();
        return (new StoryFrontier(reader, Catalog, () => preferredGc), reader);
    }

    [Fact]
    public void Scenario_guide_pointer_wins_when_it_points_at_an_unfinished_msq_quest()
    {
        var (frontier, reader) = Make();
        reader.Complete.UnionWith([39, 85]);
        reader.ScenarioQuest = 511; // the game says 511 even though the chain would say 86
        Assert.Equal((ushort)511, frontier.Current()!.QuestId);
        Assert.Equal("scenario guide", frontier.LastSource);
    }

    [Fact]
    public void Chain_walk_is_the_fallback_when_the_pointer_is_empty_or_stale()
    {
        var (frontier, reader) = Make();
        reader.Complete.UnionWith([39, 85]);
        reader.ScenarioQuest = null;
        Assert.Equal((ushort)86, frontier.Current()!.QuestId);
        Assert.Equal("chain", frontier.LastSource);

        reader.ScenarioQuest = 85; // already complete → ignored
        Assert.Equal((ushort)86, frontier.Current()!.QuestId);
    }

    [Fact]
    public void Next_after_completion_ignores_a_pointer_still_on_the_completed_quest()
    {
        var (frontier, reader) = Make();
        reader.Complete.UnionWith([39, 85]);
        reader.ScenarioQuest = 85; // agent lags a frame
        Assert.Equal((ushort)86, frontier.Next(85));
    }

    [Fact]
    public void Start_town_picks_the_root_for_a_new_character()
    {
        var (frontier, reader) = Make();
        reader.Facts = new CharacterFacts(StartTown: 3, FirstClass: 0, GrandCompany: 0, PreferredGrandCompany: 0);
        Assert.Equal((ushort)594, frontier.Current()!.QuestId);
    }

    [Fact]
    public void First_class_picks_the_close_to_home_variant()
    {
        var (frontier, reader) = Make();
        reader.Complete.Add(39);
        reader.Facts = new CharacterFacts(2, FirstClass: 6 /* CNJ */, 0, 0);
        Assert.Equal((ushort)124, frontier.Current()!.QuestId);
    }

    [Fact]
    public void Grand_company_fork_follows_the_character_then_the_setting_then_locks()
    {
        var (frontier, reader) = Make(preferredGc: 3);
        reader.Complete.UnionWith([39, 85, 86, 511]);

        // Not joined yet: the setting decides.
        reader.Facts = new CharacterFacts(2, 4, GrandCompany: 0, 0);
        Assert.Equal((ushort)682, frontier.Current()!.QuestId);

        // Joined Maelstrom: the character overrides the setting.
        reader.Facts = new CharacterFacts(2, 4, GrandCompany: 1, 0);
        Assert.Equal((ushort)681, frontier.Current()!.QuestId);

        // Twin Adder done: the other two are locked out, so the frontier moves on.
        reader.Complete.Add(680);
        reader.Facts = new CharacterFacts(2, 4, GrandCompany: 2, 0);
        Assert.Equal((ushort)700, frontier.Current()!.QuestId);
    }

    [Fact]
    public void Quest_lock_join_semantics()
    {
        var any = new QuestListing(1, "x", 1, 0, true, [], 0, [10, 11], 2);
        var all = new QuestListing(2, "x", 1, 0, true, [], 0, [10, 11], 1);
        Func<ushort, bool> only10 = id => id == 10;
        Assert.True(any.IsLockedOutBy(only10));
        Assert.False(all.IsLockedOutBy(only10));
    }
}
