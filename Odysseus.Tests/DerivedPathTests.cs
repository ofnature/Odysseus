using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;

namespace Odysseus.Tests;

public class DerivedPathTests
{
    private const uint Npc = 1_024_791;      // an ENpcResident, as the quest sheets number them
    private const uint Obj = 2_009_597;      // an EObj
    private const uint Enclave = 759;

    private static QuestMark Mark(float x, uint on = 0, uint territory = Enclave)
        => new(territory, new Vector3(x, 0, 0), on);

    private static QuestGeometry Lighting(params (byte Sequence, QuestMark[] Marks)[] objectives) => new(
        3153, "Lighting the Way",
        Accept: Mark(32, Npc), AcceptNpc: Npc,
        TurnIn: Mark(32, Npc), TurnInNpc: Npc,
        Objectives: objectives.Select(o => (o.Sequence, (IReadOnlyList<QuestMark>)o.Marks)).ToList());

    /// <summary>
    /// The shape that prompted this: one zone, two people to talk to, one thing to touch, back to
    /// the giver — and no recorded path anywhere.
    /// </summary>
    [Fact]
    public void A_talk_and_touch_quest_derives_end_to_end()
    {
        var path = DerivedPath.Build(Lighting(
            (1, [Mark(65, 1_025_757), Mark(-92, 1_026_314)]),
            (2, [Mark(19, Obj)])));

        Assert.NotNull(path);
        Assert.True(path!.IsDerived);
        Assert.Equal(3153, path.QuestId);

        var accept = Assert.Single(path.Block(0)!.Steps);
        Assert.Equal(StepKind.AcceptQuest, accept.Kind);
        Assert.Equal(Npc, accept.DataId);
        Assert.Equal(Enclave, accept.TerritoryId);

        // Both of a sequence's marks become steps, in the order the sheet gives them.
        var talks = path.Block(1)!.Steps;
        Assert.Equal(2, talks.Count);
        Assert.All(talks, s => Assert.Equal(StepKind.Interact, s.Kind));
        Assert.Equal([1_025_757u, 1_026_314u], talks.Select(s => s.DataId!.Value));

        var touch = Assert.Single(path.Block(2)!.Steps);
        Assert.Equal(StepKind.Interact, touch.Kind);
        Assert.Equal(Obj, touch.DataId);

        var turnIn = Assert.Single(path.Block(255)!.Steps);
        Assert.Equal(StepKind.CompleteQuest, turnIn.Kind);
        Assert.Equal(Npc, turnIn.DataId);
    }

    /// <summary>
    /// A mark with nothing placed on it is a place to stand, not something to talk to — and an id
    /// in neither the NPC nor the object range (an enemy, a region) is the same: walk there and let
    /// the step machinery say what it cannot do.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(42u)]            // a BNpcName — an enemy, not an interaction
    [InlineData(4_000_000u)]     // past the object range entirely
    public void A_mark_with_nothing_to_interact_with_is_walked_to(uint placed)
    {
        var path = DerivedPath.Build(Lighting((1, [Mark(65, placed)])));
        var step = Assert.Single(path!.Block(1)!.Steps);
        Assert.Equal(StepKind.WalkTo, step.Kind);
        Assert.Null(step.DataId);
        Assert.Equal(new Vector3(65, 0, 0), step.Position);
    }

    /// <summary>
    /// Without a turn-in there is no quest to finish, only one to take — which would strand the run
    /// worse than not starting. The sheets place a turn-in for 99.9% of quests; the rest are left
    /// to the recorder.
    /// </summary>
    [Fact]
    public void A_quest_with_nowhere_to_hand_it_in_derives_nothing()
    {
        var geometry = Lighting((1, [Mark(65, Npc)])) with { TurnIn = null };
        Assert.Null(DerivedPath.Build(geometry));
    }

    /// <summary>An accept and a turn-in alone still run: plenty of quests are one conversation.</summary>
    [Fact]
    public void A_quest_that_is_one_conversation_still_derives()
    {
        var path = DerivedPath.Build(Lighting());
        Assert.NotNull(path);
        Assert.Equal([0, 255], path!.Sequences.Select(s => s.Sequence));
    }

    /// <summary>
    /// A to-do claiming sequence 0 or 255 is not an objective — those two are the accept and the
    /// turn-in, and taking the sheet at its word there produced two accept steps.
    /// </summary>
    [Fact]
    public void To_dos_claiming_the_ends_do_not_become_extra_steps()
    {
        var path = DerivedPath.Build(Lighting((255, [Mark(99, Npc)]), (1, [Mark(65, Npc)])));
        Assert.Equal([0, 1, 255], path!.Sequences.Select(s => s.Sequence));
        Assert.Equal(StepKind.CompleteQuest, Assert.Single(path.Block(255)!.Steps).Kind);
    }

    [Fact]
    public void The_setting_being_off_means_there_is_no_derived_path_at_all()
    {
        var reads = 0;
        var sheets = new Sheets(id => { reads++; return Lighting((1, [Mark(65, Npc)])); });

        var off = new DerivedPaths(sheets, () => false);
        Assert.Null(off.ForQuest(3153));
        Assert.False(off.Has(3153));
        Assert.Equal(0, reads);

        var on = new DerivedPaths(sheets, () => true);
        Assert.NotNull(on.ForQuest(3153));
        Assert.True(on.Has(3153));
        on.ForQuest(3153);
        Assert.Equal(1, reads);   // read once, then remembered
    }

    [Fact]
    public void A_quest_the_sheets_do_not_know_derives_nothing()
    {
        var paths = new DerivedPaths(new Sheets(_ => null));
        Assert.Null(paths.ForQuest(9999));
        Assert.False(paths.Has(9999));
    }

    /// <summary>
    /// A mark with nothing on it is the quest map's search circle, not a point — In the Dark of
    /// Night's fight is a 204-yalm area whose centre the mesh cannot reach, and the walk faulted
    /// twice eleven yalms out with the fight already in reach. It is entered near the middle.
    /// </summary>
    [Theory]
    [InlineData(204f, DerivedPath.MaxArrival)]   // a search area: near the middle is there
    [InlineData(8f, 8f)]                         // a small area: its own edge
    [InlineData(1f, DerivedPath.MinArrival)]     // a point: close enough to stand on
    [InlineData(0f, DerivedPath.MinArrival)]     // unknown: as for a point
    public void A_walk_to_an_area_arrives_anywhere_near_its_middle(float radius, float expected)
    {
        var path = DerivedPath.Build(Lighting((3, [new QuestMark(Enclave, new Vector3(-200, 4, 29), 0, radius)])));
        var walk = Assert.Single(path!.Block(3)!.Steps);
        Assert.Equal(StepKind.WalkTo, walk.Kind);
        Assert.Equal(expected, walk.StopDistance);
    }

    [Fact]
    public void An_interaction_keeps_the_ordinary_reach()
    {
        var path = DerivedPath.Build(Lighting((1, [new QuestMark(Enclave, Vector3.Zero, 1_025_757, 1)])));
        Assert.Null(Assert.Single(path!.Block(1)!.Steps).StopDistance);
    }

    /// <summary>
    /// The wiring, not just the shape: a quest with nothing in the path store starts anyway, on a
    /// path the controller asked the sheets for.
    /// </summary>
    [Fact]
    public void The_controller_starts_a_quest_that_has_no_stored_path()
    {
        var directory = Path.Combine(Path.GetTempPath(), "odysseus-derived-" + Guid.NewGuid().ToString("N"));
        try
        {
            var world = new FakeStepWorld { ArriveOnMove = true };
            var store = new Odysseus.Services.Paths.PathStore(directory);
            var log = new List<string>();
            var derived = new DerivedPaths(new Sheets(_ => Lighting((1, [Mark(65, 1_025_757)]))));
            var controller = new Odysseus.Services.Run.QuestController(
                new FakeQuestStateReader(), store, new Odysseus.Services.Run.StepExecutor(world), world, world,
                new Policy(), _ => null, _ => 0,
                new Odysseus.Services.Run.RunLog(null), log.Add, derived: derived);

            Assert.False(store.Has(3153));
            Assert.True(controller.Start(3153));
            Assert.Contains(log, m => m.Contains("worked out from the game's own journal data"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* a temp dir is not worth a failure */ }
        }
    }

    private sealed class Policy : Odysseus.Services.Run.IRunPolicy
    {
        public bool HandOffSoloDuties => true;
        public bool HandOffDuties => true;
        public bool ContinueToNextQuest => false;
        public int StopAtLevel => 0;
        public bool ConfirmBeforeResume => false;
    }

    private sealed class Sheets(Func<ushort, QuestGeometry?> read) : IQuestGeometry
    {
        public QuestGeometry? Read(ushort questId) => read(questId);
    }
}
