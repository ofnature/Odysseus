using System.Numerics;
using Odysseus.Services.Flight;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class FlightTests
{
    private const uint Zone = 397;

    // Two from quests, two lying in the world, one of which no path ever recorded.
    private static readonly AetherCurrent FromQuestA = new(1, 2001, null);
    private static readonly AetherCurrent FromQuestB = new(2, 2002, null);
    private static readonly AetherCurrent Loose = new(3, 0, new Vector3(10, 0, 10));
    private static readonly AetherCurrent LooseToo = new(4, 0, new Vector3(30, 0, 30));
    private static readonly AetherCurrent Unrecorded = new(5, 0, null);

    private static ZoneFlight Coerthas(params AetherCurrent[] currents)
        => new(Zone, "Coerthas Western Highlands", currents, 0);

    private sealed class State : IFlightState
    {
        public HashSet<uint> Unlocked { get; } = [];
        public bool IsUnlocked(uint id) => Unlocked.Contains(id);
    }

    private readonly FakeStepWorld _world = new() { ArriveOnMove = true, TerritoryId = Zone };
    private readonly State _state = new();
    private readonly List<string> _log = [];
    private readonly CurrentCollector _collector;

    public FlightTests()
        => _collector = new CurrentCollector(_world, _state, new StepExecutor(_world), _log.Add);

    private void Run(int frames = 200)
    {
        for (var i = 0; i < frames && !_collector.IsFinished; i++)
        {
            _collector.Tick();
            _world.Advance(0.5);
            // The game attunes on arrival; stand in for that once the walk has landed.
            foreach (var c in new[] { Loose, LooseToo })
                if (Vector3.Distance(_world.PlayerPosition, c.Position!.Value) < 1f)
                    _state.Unlocked.Add(c.Id);
        }
    }

    [Fact]
    public void A_zone_can_fly_only_when_every_current_is_held()
    {
        var catalog = new AetherCurrentCatalog([Coerthas(FromQuestA, Loose, LooseToo)]);
        _state.Unlocked.Add(FromQuestA.Id);
        _state.Unlocked.Add(Loose.Id);

        var zone = catalog.Progress(_state.IsUnlocked).Single();
        Assert.Equal(2, zone.Unlocked);
        Assert.Equal(3, zone.Total);
        Assert.False(zone.CanFly);

        _state.Unlocked.Add(LooseToo.Id);
        Assert.True(catalog.Progress(_state.IsUnlocked).Single().CanFly);
    }

    /// <summary>Quest currents are the quest engine's job; the collector must not touch them.</summary>
    [Fact]
    public void Only_the_loose_currents_are_walked_to()
    {
        Assert.True(_collector.Start(Coerthas(FromQuestA, FromQuestB, Loose, LooseToo)));
        Assert.Equal(2, _collector.Target);

        Run();
        Assert.Equal(CollectState.Done, _collector.State);
        Assert.Equal(2, _collector.Collected);
        Assert.DoesNotContain(_state.Unlocked, id => id == FromQuestA.Id || id == FromQuestB.Id);
    }

    [Fact]
    public void A_current_with_no_recorded_position_is_counted_but_not_guessed_at()
    {
        Assert.True(_collector.Start(Coerthas(Loose, Unrecorded)));
        Assert.Equal(1, _collector.Target);

        Run();
        Assert.Equal(CollectState.Done, _collector.State);
        Assert.Contains("no path recorded where", _collector.StatusLine);
    }

    [Fact]
    public void Nothing_reachable_refuses_to_start_and_says_which_case_it_is()
    {
        Assert.False(_collector.Start(Coerthas(Unrecorded)));
        Assert.Contains("no path ever recorded", _collector.StatusLine);

        _state.Unlocked.Add(Loose.Id);
        Assert.False(_collector.Start(Coerthas(Loose)));
        Assert.Contains("Nothing loose left", _collector.StatusLine);
    }

    [Fact]
    public void Collecting_is_refused_from_another_zone()
    {
        _world.TerritoryId = 132;
        Assert.False(_collector.Start(Coerthas(Loose)));
        Assert.Contains("not in Coerthas Western Highlands", _collector.StatusLine);
        Assert.Contains("does not teleport", _collector.StatusLine);
    }

    /// <summary>Leaving mid-run has to stop it, or it walks to coordinates in the wrong world.</summary>
    [Fact]
    public void Leaving_the_zone_stops_the_run()
    {
        Assert.True(_collector.Start(Coerthas(Loose, LooseToo)));
        _collector.Tick();
        _world.TerritoryId = 132;
        _collector.Tick();

        Assert.Equal(CollectState.Blocked, _collector.State);
        Assert.Contains("Left the zone", _collector.StatusLine);
    }

    /// <summary>
    /// The game is the authority on whether an attune took. Trusting the executor's "done" would
    /// spin forever on a current that silently refused.
    /// </summary>
    [Fact]
    public void Arriving_without_attuning_stops_rather_than_repeating()
    {
        Assert.True(_collector.Start(Coerthas(Loose)));
        for (var i = 0; i < 200 && !_collector.IsFinished; i++)
        {
            _collector.Tick();
            _world.Advance(0.5);   // never marked unlocked
        }

        Assert.Equal(CollectState.Blocked, _collector.State);
        Assert.Equal(0, _collector.Collected);
        Assert.Contains("did not attune", _collector.StatusLine);
    }
}
