using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>Scriptable quest state: the test sets what the "game" says.</summary>
public sealed class FakeQuestStateReader : IQuestStateReader
{
    public Dictionary<ushort, QuestSnapshot> Accepted { get; } = new();
    public HashSet<ushort> Complete { get; } = [];

    public void Set(ushort id, byte sequence, params byte[] vars)
        => Accepted[id] = new QuestSnapshot(id, sequence, vars.Length == 0 ? new byte[6] : vars);

    public QuestSnapshot Read(ushort questId) => Accepted.TryGetValue(questId, out var s) ? s : QuestSnapshot.Unavailable;
    public IReadOnlyList<QuestSnapshot> ReadAccepted() => Accepted.Values.ToList();
    public bool IsComplete(ushort questId) => Complete.Contains(questId);
    public bool IsAccepted(ushort questId) => Accepted.ContainsKey(questId);
}

public class QuestControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeStepWorld _world = new() { ArriveOnMove = true };
    private readonly FakeQuestStateReader _quests = new();
    private readonly PathStore _store;
    private readonly List<string> _log = [];
    private readonly QuestController _controller;

    private sealed class Policy : IRunPolicy
    {
        public bool HandOffSoloDuties { get; set; } = true;
        public bool HandOffDuties { get; set; } = true;
    }

    private readonly Policy _policy = new();

    public QuestControllerTests()
    {
        _store = new PathStore(_dir);
        _controller = new QuestController(_quests, _store, new StepExecutor(_world), _world, _world, _policy, _log.Add);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static QuestStep Interact(uint dataId, byte?[]? flags = null) => new()
    {
        Kind = StepKind.Interact, KindName = "Interact", DataId = dataId, TerritoryId = 400,
        Position = Vector3.Zero, CompletionQuestVariablesFlags = flags,
    };

    private QuestPath StorePath(params QuestSequence[] blocks)
    {
        var path = new QuestPath { QuestId = 1622, Name = "Mogwin's Trial", Category = "3.x/MSQ", Sequences = blocks.ToList() };
        _store.Save(path);
        return path;
    }

    private void Ticks(int n, double seconds = 0.5)
    {
        for (var i = 0; i < n; i++) { _controller.Tick(); _world.Advance(seconds); }
    }

    // ── SelectResumeIndex ──

    [Fact]
    public void Resume_picks_the_first_unsatisfied_tagged_step()
    {
        var block = new QuestSequence
        {
            Sequence = 1,
            Steps = [Interact(1, [null, null, null, null, null, 32]), Interact(2, [null, null, null, null, null, 128]), Interact(3)],
        };
        Assert.Equal(0, QuestController.SelectResumeIndex(block, new QuestSnapshot(1622, 1, new byte[6])));
        Assert.Equal(1, QuestController.SelectResumeIndex(block, new QuestSnapshot(1622, 1, new byte[] { 16, 16, 0, 0, 0, 32 })));
        // Both landmarks passed: resume just after the last one.
        Assert.Equal(2, QuestController.SelectResumeIndex(block, new QuestSnapshot(1622, 1, new byte[] { 32, 17, 0, 0, 0, 160 })));
    }

    [Fact]
    public void Resume_with_no_tags_replays_from_the_top()
    {
        var block = new QuestSequence { Sequence = 1, Steps = [Interact(1), Interact(2)] };
        Assert.Equal(0, QuestController.SelectResumeIndex(block, new QuestSnapshot(1622, 1, new byte[] { 1, 2, 3, 4, 5, 6 })));
    }

    // ── controller ──

    [Fact]
    public void Start_refuses_without_a_stored_path()
    {
        Assert.False(_controller.Start(9999));
        Assert.Equal(RunState.Idle, _controller.State);
    }

    [Fact]
    public void Runs_the_block_for_the_live_sequence_and_moves_on_when_it_changes()
    {
        _world.Spawned.UnionWith([10u, 20u, 30u]);
        StorePath(
            new QuestSequence { Sequence = 0, Steps = [Interact(10)] },
            new QuestSequence { Sequence = 1, Steps = [Interact(20)] },
            new QuestSequence { Sequence = 255, Steps = [Interact(30)] });

        Assert.True(_controller.Start(1622));
        // Not accepted yet → sequence 0 → the AcceptQuest-ish step.
        Ticks(12);
        Assert.Contains("Interact 10", _world.Calls);
        Assert.DoesNotContain("Interact 20", _world.Calls);

        // The game says: accepted, sequence 1.
        _quests.Set(1622, 1);
        Ticks(12);
        Assert.Contains("Interact 20", _world.Calls);

        _quests.Set(1622, 255);
        Ticks(12);
        Assert.Contains("Interact 30", _world.Calls);

        _quests.Complete.Add(1622);
        ushort? completed = null;
        _controller.QuestCompleted += id => completed = id;
        Ticks(1);
        Assert.Equal((ushort)1622, completed);
        Assert.Equal(RunState.Idle, _controller.State);
    }

    [Fact]
    public void Entering_a_sequence_resumes_at_the_step_the_variables_point_to()
    {
        _world.Spawned.UnionWith([1u, 2u, 3u]);
        StorePath(new QuestSequence
        {
            Sequence = 1,
            Steps = [Interact(1, [null, null, null, null, null, 32]), Interact(2, [null, null, null, null, null, 128]), Interact(3)],
        });
        _quests.Set(1622, 1, 16, 16, 0, 0, 0, 32); // step 1's landmark already set

        _controller.Start(1622);
        Ticks(12);
        Assert.DoesNotContain("Interact 1", _world.Calls);
        Assert.Contains("Interact 2", _world.Calls);
        Assert.Contains("Resumed sequence 1 at step 2/3", _controller.WakeNote);
    }

    [Fact]
    public void A_block_that_ran_out_without_the_game_advancing_is_replayed_then_faulted()
    {
        _world.Spawned.Add(5);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(5)] });
        _quests.Set(1622, 1);
        _controller.Start(1622);

        // First pass runs the step, then the grace period, then replay 1..3, then fault.
        Ticks(400, seconds: 1);
        Assert.Equal(RunState.Faulted, _controller.State);
        Assert.Contains("did not advance after 3 replays", _controller.StatusLine);
        Assert.Equal(4, _world.Calls.Count(c => c == "Interact 5"));
    }

    [Fact]
    public void A_missing_block_waits_for_the_game_and_reports_it()
    {
        StorePath(new QuestSequence { Sequence = 0, Steps = [Interact(1)] });
        _quests.Set(1622, 3); // the path has no block 3
        _controller.Start(1622);
        Ticks(3);
        Assert.Equal(RunState.Advance, _controller.State);
        Assert.Contains("waiting for the game", _controller.StatusLine);
    }

    [Fact]
    public void Steps_whose_skip_condition_holds_are_skipped()
    {
        _world.Spawned.UnionWith([1u, 2u]);
        _world.TerritoryId = 400;
        var skipped = Interact(1);
        skipped.SkipConditions = new SkipConditions { StepIf = new StepCondition { InTerritory = [400] } };
        StorePath(new QuestSequence { Sequence = 1, Steps = [skipped, Interact(2)] });
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(12);
        Assert.DoesNotContain("Interact 1", _world.Calls);
        Assert.Contains("Interact 2", _world.Calls);
    }

    [Fact]
    public void A_failed_step_faults_the_run_with_the_reason()
    {
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(404)] }); // never spawned
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(100, seconds: 1);
        Assert.Equal(RunState.Faulted, _controller.State);
        Assert.Contains("404 never appeared", _controller.StatusLine);
    }

    [Fact]
    public void Handoff_steps_are_refused_when_the_policy_turns_them_off()
    {
        _policy.HandOffDuties = false;
        _world.TheseusCanEnterDuty = true;
        var duty = new QuestStep { Kind = StepKind.Duty, KindName = "Duty", TerritoryId = 400, ContentFinderConditionId = 247 };
        StorePath(new QuestSequence { Sequence = 1, Steps = [duty] });
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(3);
        Assert.Equal(RunState.Faulted, _controller.State);
        Assert.Contains("Theseus handoff is off", _controller.StatusLine);
        Assert.DoesNotContain(_world.Calls, c => c.StartsWith("TheseusEnter"));
    }

    [Fact]
    public void Handoff_state_is_reported_while_another_plugin_drives()
    {
        _world.TheseusCanEnterDuty = true;
        var duty = new QuestStep { Kind = StepKind.Duty, KindName = "Duty", TerritoryId = 400, ContentFinderConditionId = 247 };
        StorePath(new QuestSequence { Sequence = 1, Steps = [duty] });
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(4);
        Assert.Equal(RunState.Handoff, _controller.State);
    }

    [Fact]
    public void Stop_returns_to_idle_and_releases_the_world()
    {
        _world.Spawned.Add(1);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(2);
        _controller.Stop();
        Assert.Equal(RunState.Idle, _controller.State);
        Assert.Contains("Stop", _world.Calls);
        Assert.Contains("ReleaseDialogue", _world.Calls);
    }
}
