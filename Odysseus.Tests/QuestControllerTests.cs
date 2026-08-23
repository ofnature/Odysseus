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
    public ushort? ScenarioQuest { get; set; }
    public ushort? CurrentScenarioQuest() => ScenarioQuest;
    public CharacterFacts Facts { get; set; }
    public CharacterFacts Character() => Facts;
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
        public bool ContinueToNextQuest { get; set; }
        public int StopAtLevel { get; set; }
        public bool ConfirmBeforeResume { get; set; }
    }

    private readonly Policy _policy = new();
    private readonly Dictionary<ushort, ushort> _chain = new();
    private readonly Dictionary<ushort, int> _levels = new();
    private readonly HashSet<ushort> _handOrLand = [];
    private readonly RunLog _runLog = new(null);

    public QuestControllerTests()
    {
        _store = new PathStore(_dir);
        _controller = new QuestController(_quests, _store, new StepExecutor(_world), _world, _world, _policy,
            id => _chain.TryGetValue(id, out var n) ? n : null,
            id => _levels.TryGetValue(id, out var l) ? l : 0,
            _runLog, _log.Add,
            needsHandOrLand: id => _handOrLand.Contains(id));
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
    public void A_step_whose_completion_flags_are_set_is_skipped_not_chased()
    {
        // A Sappy Ending: the spot with bit 32 was already tapped — its plant is despawned — and
        // the spot with bit 16 still stands. The run goes to the one still standing.
        _world.Spawned.Add(111u);
        _world.Spawned.Add(222u);
        StorePath(new QuestSequence
        {
            Sequence = 1,
            Steps = [Interact(222, [null, null, null, null, null, 16]), Interact(111, [null, null, null, null, null, 32])],
        });
        _quests.Set(1622, 1, 0, 0, 0, 0, 0, 32);
        Assert.True(_controller.Start(1622));
        Ticks(40);

        Assert.DoesNotContain("Interact 111", _world.Calls);
        Assert.Contains("Interact 222", _world.Calls);
        Assert.Contains(_log, l => l.Contains("completion flags are already set"));
    }

    [Fact]
    public void A_step_that_did_not_take_is_retried_quickly_then_patiently()
    {
        // Accepting Brotherhood of Ash landed while the previous turn-in was still closing: the
        // step reported its dialogue over, the game never moved, and the twenty seconds before
        // anyone noticed was the pause between one quest and the next.
        _world.Spawned.Add(1u);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });
        _quests.Set(1622, 1);
        _controller.Start(1622);

        Assert.InRange(SecondsUntilReplay(), 0, 12); // the interaction plainly did not take
        Assert.InRange(SecondsUntilReplay(), 15, 30); // now give the game its proper time
    }

    /// <summary>Run until the block is replayed, and say how long the game was given.</summary>
    private double SecondsUntilReplay()
    {
        _log.Clear();
        var started = _world.UtcNow;
        for (var i = 0; i < 200 && !_log.Any(m => m.Contains("did not advance")); i++)
            Ticks(1);
        Assert.Contains(_log, m => m.Contains("did not advance"));
        return (_world.UtcNow - started).TotalSeconds;
    }

    [Fact]
    public void A_cutscene_still_playing_gets_the_full_wait()
    {
        _world.Spawned.Add(1u);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(12);

        _world.IsOccupied = true;
        Ticks(10, seconds: 1);
        Assert.DoesNotContain(_log, m => m.Contains("did not advance"));
    }

    [Fact]
    public void Quest_markers_carry_a_part_done_block_past_the_npcs_that_are_finished()
    {
        // Quest 2601: talk to three townspeople, no completion flags to resume from.
        var block = new QuestSequence
        {
            Sequence = 1,
            Steps = [Interact(1021149), Interact(1021150), Interact(1021148)],
        };
        var marks = new Dictionary<uint, QuestController.Marker>
        {
            [1021149] = QuestController.Marker.Unmarked, // stood right there, nothing over its head
            [1021150] = QuestController.Marker.Marked,
        };
        Assert.Equal(1, QuestController.SkipStepsAlreadyDealtWith(block, 0, id => marks.GetValueOrDefault(id)));

        // An NPC we cannot see says nothing either way, so the block runs as written.
        marks[1021149] = QuestController.Marker.Unknown;
        Assert.Equal(0, QuestController.SkipStepsAlreadyDealtWith(block, 0, id => marks.GetValueOrDefault(id)));

        // Not one icon anywhere is what a client that is not drawing them looks like. Without a
        // single known-good reading, nothing is skipped on the strength of the missing ones.
        marks[1021149] = marks[1021150] = marks[1021148] = QuestController.Marker.Unmarked;
        Assert.Equal(0, QuestController.SkipStepsAlreadyDealtWith(block, 0, id => marks.GetValueOrDefault(id)));
    }

    [Fact]
    public void Resume_with_no_tags_replays_from_the_top()
    {
        var block = new QuestSequence { Sequence = 1, Steps = [Interact(1), Interact(2)] };
        Assert.Equal(0, QuestController.SelectResumeIndex(block, new QuestSnapshot(1622, 1, new byte[] { 1, 2, 3, 4, 5, 6 })));
    }

    [Fact]
    public void A_crafter_only_quest_switches_class_before_it_starts()
    {
        // All twelve custom delivery unlocks are "Disciples of the Land or Hand": on a combat class
        // the NPC will not offer the quest at all.
        _world.SavedGearsets.AddRange([
            new GearsetInfo(0, 34, 0, 100, JobKind.Combat),
            new GearsetInfo(3, 9, 0, 90, JobKind.Crafter),
        ]);
        _world.CurrentJobKind = JobKind.Combat;
        _handOrLand.Add(1622);
        _levels[1622] = 90;
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });

        Assert.True(_controller.Start(1622));
        Assert.Contains(_world.Calls, c => c == "Gearset 3");
        Assert.Contains(_log, m => m.Contains("Disciple of the Hand or Land"));
    }

    [Fact]
    public void Without_a_crafter_gearset_it_says_so_rather_than_walking_to_an_npc_who_will_not_talk()
    {
        _world.SavedGearsets.Add(new GearsetInfo(0, 34, 0, 100, JobKind.Combat));
        _world.CurrentJobKind = JobKind.Combat;
        _handOrLand.Add(1622);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });

        Assert.False(_controller.Start(1622));
        Assert.Contains("no crafter or gatherer gearset", _controller.StatusLine);
    }

    [Fact]
    public void A_crafter_below_the_quests_level_is_named_rather_than_the_class_you_were_standing_as()
    {
        _world.SavedGearsets.AddRange([
            new GearsetInfo(0, 34, 0, 100, JobKind.Combat), // a level 100 combat job would pass the usual gate
            new GearsetInfo(3, 9, 0, 62, JobKind.Crafter),
        ]);
        _world.CurrentJobKind = JobKind.Combat;
        _handOrLand.Add(1622);
        _levels[1622] = 90;
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });

        Assert.False(_controller.Start(1622));
        Assert.Contains("level 90", _controller.StatusLine);
        Assert.Contains("level 62", _controller.StatusLine);
    }

    [Fact]
    public void Already_a_crafter_means_nothing_to_switch()
    {
        _world.SavedGearsets.Add(new GearsetInfo(3, 9, 0, 90, JobKind.Crafter));
        _world.CurrentJobKind = JobKind.Crafter;
        _handOrLand.Add(1622);
        _levels[1622] = 90;
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });

        Assert.True(_controller.Start(1622));
        Assert.DoesNotContain(_world.Calls, c => c.StartsWith("Gearset "));
    }

    [Fact]
    public void Stop_after_quest_holds_against_continue_to_next()
    {
        // The tribe runner runs one daily at a time through this controller and arms this flag.
        // With "continue to next quest" on, a completion that rolled on would leave the controller
        // busy forever from the runner's point of view — and every society's Run button greys on
        // its state.
        _policy.ContinueToNextQuest = true;
        _chain[1622] = 1700;                                   // a next quest exists and has a path
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });
        _store.Save(new QuestPath { QuestId = 1700, Name = "Next", Sequences = [new QuestSequence { Sequence = 1, Steps = [Interact(2)] }] });
        _world.Spawned.UnionWith([1u, 2u]);
        _quests.Set(1622, 1);

        Assert.True(_controller.Start(1622));
        _controller.StopAfterQuest = true;                     // armed after Start, as the runner does

        _quests.Accepted.Remove(1622);
        _quests.Complete.Add(1622);                            // the daily completes
        Ticks(6);

        Assert.Equal(RunState.Idle, _controller.State);
        Assert.NotEqual((ushort)1700, _controller.QuestId);    // it did not roll on
        Assert.Contains("as armed", _controller.StatusLine);
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
    public void Steps_not_selected_by_the_quest_variables_are_skipped()
    {
        _world.Spawned.UnionWith([1u, 2u]);
        var gated = Interact(1);
        gated.RequiredQuestVariables = [null, null, null, [new VariableMatch(null, 3, null)], null, null];
        StorePath(new QuestSequence { Sequence = 1, Steps = [gated, Interact(2)] });
        _quests.Set(1622, 1, 0, 0, 0, 0x40, 0, 0); // slot 3 high nibble is 4, not 3
        _controller.Start(1622);
        Ticks(12);
        Assert.DoesNotContain("Interact 1", _world.Calls);
        Assert.Contains("Interact 2", _world.Calls);
        Assert.Contains(_runLog.Recent, r => r.Outcome == "Skipped" && r.Reason!.Contains("variables"));
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

    private void StoreTwoQuestChain()
    {
        _world.Spawned.UnionWith([1u, 2u]);
        _store.Save(new QuestPath { QuestId = 1622, Name = "Mogwin's Trial", Category = "3.x/MSQ", Sequences = [new QuestSequence { Sequence = 1, Steps = [Interact(1)] }] });
        _store.Save(new QuestPath { QuestId = 1623, Name = "Moglin's Judgment", Category = "3.x/MSQ", Sequences = [new QuestSequence { Sequence = 0, Steps = [Interact(2)] }] });
        _chain[1622] = 1623;
        _quests.Set(1622, 1);
    }

    [Fact]
    public void Rolls_into_the_next_msq_quest_when_continue_is_on()
    {
        StoreTwoQuestChain();
        _policy.ContinueToNextQuest = true;
        _controller.Start(1622);
        Ticks(3);
        _quests.Complete.Add(1622);
        Ticks(6);
        Assert.Equal((ushort)1623, _controller.QuestId);
        Assert.NotEqual(RunState.Idle, _controller.State);
        Assert.Equal(1, _controller.QuestsThisRun);
        Ticks(12);
        Assert.Contains("Interact 2", _world.Calls);
    }

    [Fact]
    public void Stops_after_the_quest_when_continue_is_off_or_armed_off()
    {
        StoreTwoQuestChain();
        _policy.ContinueToNextQuest = false;
        _controller.Start(1622);
        Ticks(3);
        _quests.Complete.Add(1622);
        Ticks(3);
        Assert.Equal(RunState.Idle, _controller.State);
        Assert.Contains("1 done", _controller.StatusLine);

        // Continue on, but the user armed "stop after this quest".
        _quests.Complete.Remove(1622);
        _policy.ContinueToNextQuest = true;
        _controller.Start(1622);
        _controller.StopAfterQuest = true;
        Ticks(3);
        _quests.Complete.Add(1622);
        Ticks(3);
        Assert.Equal(RunState.Idle, _controller.State);
        Assert.Contains("as armed", _controller.StatusLine);
        Assert.DoesNotContain("Interact 2", _world.Calls);
    }

    [Fact]
    public void Level_stop_holds_the_run_at_the_configured_level()
    {
        StoreTwoQuestChain();
        _policy.ContinueToNextQuest = true;
        _policy.StopAtLevel = 54;
        _world.PlayerLevel = 54;
        _controller.Start(1622);
        Ticks(3);
        _quests.Complete.Add(1622);
        Ticks(3);
        Assert.Equal(RunState.Idle, _controller.State);
        Assert.Contains("level 54", _controller.StatusLine);
    }

    [Fact]
    public void A_ready_priority_quest_runs_before_the_next_msq_quest()
    {
        StoreTwoQuestChain(); // 1622 -> 1623 in the chain
        _world.Spawned.Add(3);
        _store.Save(new QuestPath { QuestId = 7000, Name = "Side Errand", Category = "x", Sequences = [new QuestSequence { Sequence = 0, Steps = [Interact(3)] }] });
        _policy.ContinueToNextQuest = true;
        _controller.PriorityNext = () => 7000;
        _controller.StoryCurrent = () => 1623;              // where the story stands, independent of what just ran

        _controller.Start(1622);
        Ticks(3);
        _quests.Complete.Add(1622);
        Ticks(6);
        Assert.Equal((ushort)7000, _controller.QuestId);    // priority first…
        Ticks(12);
        Assert.Contains("Interact 3", _world.Calls);

        _controller.PriorityNext = () => null;              // …then, with the list drained, the story
        _quests.Complete.Add(7000);
        Ticks(6);
        Assert.Equal((ushort)1623, _controller.QuestId);
    }

    [Fact]
    public void Level_gate_stops_before_walking_to_a_quest_the_character_cannot_accept()
    {
        _world.Spawned.Add(1);
        StorePath(new QuestSequence { Sequence = 0, Steps = [Interact(1)] });
        _levels[1622] = 60;
        _world.PlayerLevel = 54;

        Assert.False(_controller.Start(1622));
        Assert.Equal(RunState.Idle, _controller.State);
        Assert.Contains("needs level 60", _controller.StatusLine);
        Assert.DoesNotContain("Interact 1", _world.Calls);
    }

    [Fact]
    public void Level_gate_does_not_block_a_quest_already_accepted()
    {
        _world.Spawned.Add(1);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });
        _levels[1622] = 60;
        _world.PlayerLevel = 54;
        _quests.Set(1622, 1); // in the journal already — synced down or level changed since
        Assert.True(_controller.Start(1622));
        Ticks(12);
        Assert.Contains("Interact 1", _world.Calls);
    }

    [Fact]
    public void No_next_quest_stops_with_a_reason()
    {
        StoreTwoQuestChain();
        _chain.Clear();
        _policy.ContinueToNextQuest = true;
        _controller.Start(1622);
        Ticks(3);
        _quests.Complete.Add(1622);
        Ticks(3);
        Assert.Equal(RunState.Idle, _controller.State);
        Assert.Contains("No next MSQ quest", _controller.StatusLine);
    }

    [Fact]
    public void Confirm_before_resume_parks_a_mid_quest_start_until_answered()
    {
        _world.Spawned.Add(1);
        StorePath(new QuestSequence { Sequence = 3, Steps = [Interact(1)] });
        _quests.Set(1622, 3);
        _policy.ConfirmBeforeResume = true;

        _controller.Start(1622);
        Assert.True(_controller.AwaitingResumeConfirm);
        Assert.Equal(RunState.Reconcile, _controller.State);
        Ticks(6);
        Assert.DoesNotContain("Interact 1", _world.Calls); // parked

        _controller.ConfirmResume();
        Ticks(12);
        Assert.Contains("Interact 1", _world.Calls);
    }

    [Fact]
    public void Confirm_is_not_asked_for_a_fresh_quest()
    {
        _world.Spawned.Add(1);
        StorePath(new QuestSequence { Sequence = 0, Steps = [Interact(1)] });
        _policy.ConfirmBeforeResume = true;
        _controller.Start(1622); // not accepted → sequence 0 → nothing to resume
        Assert.False(_controller.AwaitingResumeConfirm);
    }

    [Fact]
    public void Every_step_outcome_lands_in_the_log()
    {
        _world.Spawned.Add(1);
        var skipped = Interact(2);
        skipped.SkipConditions = new SkipConditions { StepIf = new StepCondition { InTerritory = [400] } };
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1), skipped, Interact(404)] });
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(120, seconds: 1);

        var outcomes = _runLog.Recent.Reverse().Select(r => r.Outcome).ToList();
        Assert.Equal(new[] { "Done", "Skipped", "Failed" }, outcomes);
        Assert.Contains("404 never appeared", _runLog.Recent.First().Reason);
        Assert.All(_runLog.Recent, r => Assert.Equal("Mogwin's Trial", r.QuestName));
    }

    [Fact]
    public void Skip_moves_past_the_current_step_and_logs_it()
    {
        _world.Spawned.UnionWith([2u]);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(404), Interact(2)] }); // 404 never spawns
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(3);
        Assert.True(_controller.SkipStep());
        Ticks(12);
        Assert.Contains("Interact 2", _world.Calls);
        Assert.Contains(_runLog.Recent, r => r.Outcome == "Skipped" && r.Reason == "skipped by user");
    }

    [Fact]
    public void Skip_and_retry_clear_a_fault_on_that_step()
    {
        // The last step of a block: nothing left to go on to, so a missing NPC is a fault.
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(404)] });
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(100, seconds: 1);
        Assert.Equal(RunState.Faulted, _controller.State);

        Assert.True(_controller.RetryStep());
        Assert.NotEqual(RunState.Faulted, _controller.State);
        Ticks(100, seconds: 1);
        Assert.Equal(RunState.Faulted, _controller.State); // still no 404 — faults again

        Assert.True(_controller.SkipStep());
        Ticks(12);
        Assert.NotEqual(RunState.Faulted, _controller.State);
    }

    [Fact]
    public void An_npc_that_is_not_there_is_taken_as_done_while_the_block_has_more_to_run()
    {
        // Quest 2601 again: picked up part-done, the first townsperson has gone. Faulting there
        // costs the whole quest, and the ones left are those with something still to say.
        _world.Spawned.Add(2u);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(404), Interact(2)] });
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Ticks(100, seconds: 1);

        Assert.NotEqual(RunState.Faulted, _controller.State);
        Assert.Contains("Interact 2", _world.Calls);
    }

    [Fact]
    public void Pause_after_step_stops_once_the_current_step_is_done()
    {
        _world.Spawned.UnionWith([1u, 2u]);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1), Interact(2)] });
        _quests.Set(1622, 1);
        _controller.PauseAfterStep = true;
        _controller.Start(1622);
        _controller.PauseAfterStep = true; // Start clears it; the idle "Step" button sets it after Start
        Ticks(12);
        Assert.Contains("Interact 1", _world.Calls);
        Assert.DoesNotContain("Interact 2", _world.Calls);
        Assert.Equal(RunState.Idle, _controller.State);
        Assert.Contains("Paused after step 1", _controller.StatusLine);
    }

    [Fact]
    public void Step_once_runs_one_step_and_returns_to_idle_without_a_quest()
    {
        _world.Spawned.Add(77);
        var step = Interact(77);
        Assert.True(_controller.StepOnce(step));
        Assert.Equal(RunState.Step, _controller.State);
        Ticks(12);
        Assert.Contains("Interact 77", _world.Calls);
        Assert.Equal(RunState.Idle, _controller.State);
        Assert.Contains("Step done", _controller.StatusLine);
    }

    [Fact]
    public void Step_once_is_refused_while_a_quest_runs_and_faults_on_a_bad_step()
    {
        _world.Spawned.Add(1);
        StorePath(new QuestSequence { Sequence = 1, Steps = [Interact(1)] });
        _quests.Set(1622, 1);
        _controller.Start(1622);
        Assert.False(_controller.StepOnce(Interact(2)));
        _controller.Stop();

        Assert.True(_controller.StepOnce(Interact(404)));
        Ticks(100, seconds: 1);
        Assert.Equal(RunState.Faulted, _controller.State);
        Assert.Contains("single step failed", _controller.StatusLine);
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
