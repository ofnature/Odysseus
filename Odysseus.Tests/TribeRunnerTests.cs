using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;
using Odysseus.Services.Tribes;

namespace Odysseus.Tests;

public class TribeRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeStepWorld _world = new() { ArriveOnMove = true, TerritoryId = 146 };
    private readonly FakeQuestStateReader _quests = new();
    private readonly PathStore _store;
    private readonly QuestController _controller;
    private readonly FakeTribeState _state = new();
    private readonly TribeRunner _runner;
    private readonly List<string> _log = [];

    private static readonly TribeInfo Amaljaa = new(1, "Amalj'aa", 0, TribeKind.Combat, 8,
        [new TribeIssuer(1005550, 146, new Vector3(105, 15, -357), 10)], [2001, 2002, 2003, 2004]);
    private static readonly TribeInfo Moogles = new(8, "Moogles", 1, TribeKind.Crafter, 8,
        [new TribeIssuer(1017171, 400, Vector3.Zero, 30)], [3001]);

    private sealed class FakeTribeState : ITribeState
    {
        public int AllowanceLeft { get; set; } = 12;
        public byte Rank { get; set; } = 1;
        public List<ushort> Accepted { get; } = [];
        public int CompletedToday { get; set; }
        public TribeStanding Read(TribeInfo tribe) => new(tribe.Id, Rank, 100, 300, Accepted.ToList(), CompletedToday);
    }

    private sealed class Policy : IRunPolicy
    {
        public bool HandOffSoloDuties => true;
        public bool HandOffDuties => true;
        public bool ContinueToNextQuest => false;
        public int StopAtLevel => 0;
        public bool ConfirmBeforeResume => false;
    }

    public TribeRunnerTests()
    {
        _store = new PathStore(_dir);
        _controller = new QuestController(_quests, _store, new StepExecutor(_world), _world, _world, new Policy(),
            _ => null, _ => 0, new RunLog(null), _log.Add);
        _runner = new TribeRunner(_world, _state, _controller, new StepExecutor(_world), _log.Add);
        _world.PlayerPosition = new Vector3(105, 15, -357); // at the issuer
        _world.Spawned.Add(1005550);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void StoreDaily(ushort id, uint npc)
    {
        _world.Spawned.Add(npc);
        _store.Save(new QuestPath
        {
            QuestId = id, Name = $"Daily {id}", Category = "Allied Societies",
            Sequences = [new QuestSequence { Sequence = 0, Steps = [new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = npc, TerritoryId = 146, Position = _world.PlayerPosition }] }],
        });
    }

    /// <summary>An objective at the mob/object, and a hand-in back at the issuer.</summary>
    private void StoreDailyWithTurnIn(ushort id, uint npc)
    {
        _world.Spawned.Add(npc);
        _store.Save(new QuestPath
        {
            QuestId = id, Name = $"Daily {id}", Category = "Allied Societies",
            Sequences =
            [
                new QuestSequence { Sequence = 0, Steps = [new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = npc, TerritoryId = 146, Position = _world.PlayerPosition }] },
                new QuestSequence { Sequence = 255, Steps = [new QuestStep { Kind = StepKind.CompleteQuest, KindName = "CompleteQuest", DataId = 1005550, TerritoryId = 146, Position = _world.PlayerPosition }] },
            ],
        });
    }

    private void Ticks(int n, double seconds = 0.5)
    {
        for (var i = 0; i < n; i++) { _runner.Tick(); _world.Advance(seconds); }
    }

    [Fact]
    public void A_crafter_tribe_is_refused_with_a_reason()
    {
        Assert.False(_runner.Start(Moogles));
        Assert.Contains("aren't automated yet", _runner.StatusLine);
    }

    [Fact]
    public void A_locked_tribe_and_an_exhausted_day_are_refused()
    {
        _state.Rank = 0;
        Assert.False(_runner.Start(Amaljaa));
        Assert.Contains("not unlocked", _runner.StatusLine);

        _state.Rank = 1;
        _state.CompletedToday = 3;
        Assert.False(_runner.Start(Amaljaa));
        Assert.Contains("nothing left today", _runner.StatusLine);

        _state.CompletedToday = 0;
        _state.AllowanceLeft = 0;
        Assert.False(_runner.Start(Amaljaa));
    }

    [Fact]
    public void Switches_to_a_combat_job_before_travelling()
    {
        _world.IsCombatJob = false;
        Assert.True(_runner.Start(Amaljaa));
        Assert.Equal(TribeRunState.Job, _runner.State);
        Ticks(4);
        Assert.Contains("Gearset 0", _world.Calls);
        Assert.NotEqual(TribeRunState.Job, _runner.State);
    }

    [Fact]
    public void Accepts_at_the_issuer_then_runs_each_daily()
    {
        StoreDaily(2001, 5001);
        StoreDaily(2002, 5002);
        Assert.True(_runner.Start(Amaljaa));
        Ticks(2);
        Assert.Equal(TribeRunState.Accept, _runner.State);

        // Interacts, and answers the offer list the issuer puts up.
        Ticks(4, seconds: 2);
        Assert.Contains("Interact 1005550", _world.Calls);
        _world.VisibleAddons.Add("SelectIconString");
        Ticks(1);
        Assert.Contains("IconSelect 0", _world.Calls);
        _world.VisibleAddons.Remove("SelectIconString");

        // The game says two are now in the journal; the runner moves on to running them.
        _state.Accepted.AddRange([2001, 2002]);
        _state.CompletedToday = 1;   // takes it to the 3-a-day target
        Ticks(2);
        Assert.Equal(TribeRunState.Run, _runner.State);

        _quests.Set(2001, 0);
        Ticks(30);
        Assert.Contains("Interact 5001", _world.Calls);
    }

    [Fact]
    public void All_objectives_first_then_one_trip_home_for_every_turn_in()
    {
        StoreDailyWithTurnIn(2001, 5001);
        StoreDailyWithTurnIn(2002, 5002);
        _state.Accepted.AddRange([2001, 2002]);
        _state.CompletedToday = 3;   // nothing to accept; straight to the objectives
        _quests.Set(2001, 0);
        _quests.Set(2002, 0);
        Assert.True(_runner.Start(Amaljaa));
        Ticks(2);
        Assert.Equal(TribeRunState.Run, _runner.State);

        // Daily one's objective is done; the game moves it to its hand-in sequence — and instead
        // of walking home, the runner holds the turn-in and starts daily two's objective.
        Ticks(30);
        Assert.Contains("Interact 5001", _world.Calls);
        _quests.Set(2001, 255);
        Ticks(30);
        Assert.Contains("Interact 5002", _world.Calls);
        Assert.DoesNotContain("Interact 1005550", _world.Calls);   // no trip home yet
        _quests.Set(2002, 255);
        Ticks(10);

        // Now the one trip: both hand-ins from the same visit.
        Ticks(60);
        Assert.Contains("Interact 1005550", _world.Calls);
        _quests.Accepted.Remove(2001); _quests.Complete.Add(2001);
        Ticks(60);
        _quests.Accepted.Remove(2002); _quests.Complete.Add(2002);
        Ticks(30);
        Assert.Equal(TribeRunState.Done, _runner.State);
        Assert.Contains(_log, l => l.Contains("back to hand them in"));
    }

    [Fact]
    public void A_daily_that_faults_is_dropped_and_the_rest_continue()
    {
        StoreDaily(2001, 404);       // never spawns
        StoreDaily(2002, 5002);
        _state.Accepted.AddRange([2001, 2002]);
        _state.CompletedToday = 3;   // nothing to accept; straight to running what is held
        Assert.True(_runner.Start(Amaljaa));
        Ticks(2);
        Assert.Equal(TribeRunState.Run, _runner.State);

        _world.Spawned.Remove(404);
        Ticks(200, seconds: 1);
        Assert.Contains("Interact 5002", _world.Calls);
        Assert.Contains(_log, l => l.Contains("faulted") || l.Contains("dropping"));
    }
}
