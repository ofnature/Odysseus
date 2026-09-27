using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class OptionalPickupsTests
{
    private const uint Lochs = 621;
    private const uint Peaks = 620;

    /// <summary>The Key to Victory (2549), sequence 1, as the path has it.</summary>
    private static List<QuestStep> KeyToVictory() =>
    [
        new() { Kind = StepKind.WalkTo, TerritoryId = Lochs, TargetTerritoryId = Peaks, Position = new Vector3(-791, 46, -16) },
        new() { Kind = StepKind.AcceptQuest, TerritoryId = Peaks, DataId = 1023167, PickUpQuestId = 2851 },
        new() { Kind = StepKind.AcceptQuest, TerritoryId = Peaks, DataId = 1020893, PickUpQuestId = 2860 },
        new() { Kind = StepKind.SinglePlayerDuty, TerritoryId = Lochs, DataId = 1021705 },
    ];

    private static readonly Func<ushort, bool> NothingNeeded = _ => false;

    [Fact]
    public void With_the_setting_off_the_pick_ups_and_the_walk_are_all_done()
    {
        var world = new FakeStepWorld();
        var steps = KeyToVictory();
        Assert.False(OptionalPickups.Dropped(steps[1], world, skipOptional: false, NothingNeeded));
        Assert.False(OptionalPickups.LeadsOnlyToDropped(steps, 0, world, skipOptional: false, NothingNeeded));
    }

    /// <summary>
    /// The whole point: leave both pick-ups AND the walk into The Peaks that only existed to reach
    /// them. Skipping the pick-ups while still crossing the zone is the wasted movement itself.
    /// </summary>
    [Fact]
    public void With_the_setting_on_the_pick_ups_and_the_walk_to_them_are_both_left()
    {
        var world = new FakeStepWorld();
        var steps = KeyToVictory();

        Assert.True(OptionalPickups.Dropped(steps[1], world, skipOptional: true, NothingNeeded));
        Assert.True(OptionalPickups.Dropped(steps[2], world, skipOptional: true, NothingNeeded));
        Assert.True(OptionalPickups.LeadsOnlyToDropped(steps, 0, world, skipOptional: true, NothingNeeded));
        Assert.False(OptionalPickups.Dropped(steps[3], world, skipOptional: true, NothingNeeded));  // the duty runs
    }

    /// <summary>
    /// The gap the previous fix left: a character that already has both side quests skipped the
    /// pick-ups but still walked into The Peaks to stand where they would have been. That is true
    /// with the setting off too — there is nothing to pick up either way.
    /// </summary>
    [Fact]
    public void Once_both_are_taken_the_walk_is_left_even_with_the_setting_off()
    {
        var world = new FakeStepWorld();
        world.CompletedQuests.Add(2851);
        world.AcceptedQuests.Add(2860);

        Assert.True(OptionalPickups.LeadsOnlyToDropped(KeyToVictory(), 0, world, skipOptional: false, NothingNeeded));
    }

    [Fact]
    public void If_only_one_is_taken_the_walk_is_still_made_for_the_other()
    {
        var world = new FakeStepWorld();
        world.CompletedQuests.Add(2851);   // 2860 still to get
        Assert.False(OptionalPickups.LeadsOnlyToDropped(KeyToVictory(), 0, world, skipOptional: false, NothingNeeded));
    }

    /// <summary>"Optional" means nothing the run is working towards needs it — followed through the chain.</summary>
    [Fact]
    public void A_pick_up_the_chain_needs_is_kept_whatever_the_setting()
    {
        var world = new FakeStepWorld();
        var steps = KeyToVictory();
        Func<ushort, bool> needs2860 = id => id == 2860;

        Assert.True(OptionalPickups.Dropped(steps[1], world, skipOptional: true, needs2860));   // 2851: optional
        Assert.False(OptionalPickups.Dropped(steps[2], world, skipOptional: true, needs2860));  // 2860: needed
        Assert.False(OptionalPickups.LeadsOnlyToDropped(steps, 0, world, skipOptional: true, needs2860));
    }

    /// <summary>A walk followed by real work in the zone it walks into is still the way there.</summary>
    [Fact]
    public void A_walk_into_a_zone_where_real_work_follows_is_kept()
    {
        var world = new FakeStepWorld();
        List<QuestStep> steps =
        [
            new() { Kind = StepKind.WalkTo, TerritoryId = Lochs, TargetTerritoryId = Peaks },
            new() { Kind = StepKind.AcceptQuest, TerritoryId = Peaks, DataId = 1, PickUpQuestId = 2851 },
            new() { Kind = StepKind.Interact, TerritoryId = Peaks, DataId = 2 },
        ];
        Assert.False(OptionalPickups.LeadsOnlyToDropped(steps, 0, world, skipOptional: true, NothingNeeded));
    }

    [Fact]
    public void An_ordinary_walk_is_never_touched()
    {
        var world = new FakeStepWorld();
        List<QuestStep> steps =
        [
            new() { Kind = StepKind.WalkTo, TerritoryId = Lochs },
            new() { Kind = StepKind.Interact, TerritoryId = Lochs, DataId = 2 },
        ];
        Assert.False(OptionalPickups.LeadsOnlyToDropped(steps, 0, world, skipOptional: true, NothingNeeded));
    }

    /// <summary>The chain is the sheet's own prerequisite links, followed all the way back.</summary>
    [Fact]
    public void Prerequisites_are_followed_however_far_back_they_go()
    {
        var catalog = new QuestCatalog(
        [
            new QuestListing(3, "Story", 50, 0, true, [2], 0),
            new QuestListing(2, "Middle", 40, 0, false, [1], 0),
            new QuestListing(1, "Root", 30, 0, false, [], 0),
            new QuestListing(9, "Unrelated", 30, 0, false, [], 0),
        ]);

        Assert.Equal([(ushort)2, (ushort)1], catalog.PrerequisitesOf([3]).OrderByDescending(x => x));
        Assert.True(catalog.StoryNeeds(1));    // two links back from an MSQ quest
        Assert.True(catalog.StoryNeeds(3));    // the MSQ quest itself
        Assert.False(catalog.StoryNeeds(9));
    }
}
