using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Services.Tribes;

public enum TribeRunState
{
    Idle,
    /// <summary>Switching to a combat gearset the tribe's dailies need.</summary>
    Job,
    /// <summary>Travelling to the issuer.</summary>
    Travel,
    /// <summary>Clicking through the issuer's dialogue to accept dailies.</summary>
    Accept,
    /// <summary>Running an accepted daily through the quest controller.</summary>
    Run,
    Done,
    Faulted,
}

/// <summary>
/// Runs one allied society's dailies for the day: get the right job, go to the issuer, accept up
/// to the day's slots, then run every accepted daily's objectives — each hand-in held — and make
/// one trip home to turn them all in, through the same
/// <see cref="QuestController"/> the MSQ uses.
///
/// <para>
/// The accept step is the one piece the quest engine cannot do — there is no path for "talk to the
/// issuer and pick three dailies" — so it is an addon loop here: interact, then answer whatever
/// window comes up (icon-list of offers, a select-string, the journal-accept confirm, talk
/// advances) until the accepted count reaches the target. Modelled on Auto Daily Tribes, but the
/// running is ours, not delegated.
/// </para>
/// </summary>
public sealed class TribeRunner
{
    private static readonly TimeSpan AcceptStall = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan JobStall = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReinteractGap = TimeSpan.FromSeconds(3);

    private readonly IStepWorld _world;
    private readonly ITribeState _state;
    private readonly QuestController _controller;
    private readonly StepExecutor _travelExecutor;
    private readonly Action<string> _log;

    private TribeInfo? _tribe;
    private TribeIssuer? _issuer;
    private DateTime _phaseStart;
    private DateTime _lastInteract;
    private int _acceptTarget;
    private Queue<ushort> _toRun = new();
    private ushort _running;
    private readonly Queue<ushort> _toTurnIn = new();
    private bool _turningIn;
    private bool _needAccept;
    private readonly List<ushort> _dropped = [];

    public TribeRunner(IStepWorld world, ITribeState state, QuestController controller, StepExecutor travelExecutor, Action<string> log)
    {
        _world = world;
        _state = state;
        _controller = controller;
        _travelExecutor = travelExecutor;
        _log = log;
    }

    public TribeRunState State { get; private set; } = TribeRunState.Idle;
    public string StatusLine { get; private set; } = string.Empty;
    public byte TribeId => _tribe?.Id ?? 0;

    /// <summary>Begin the tribe's dailies. Returns false with a reason when it cannot start.</summary>
    public bool Start(TribeInfo tribe)
    {
        if (!tribe.IsRunnableKind)
        {
            StatusLine = $"{tribe.Name}: {tribe.Kind} dailies aren't automated yet.";
            return false;
        }
        if (tribe.PrimaryIssuer is not { } issuer)
        {
            StatusLine = $"{tribe.Name}: no issuer in the sheet.";
            return false;
        }
        var standing = _state.Read(tribe);
        if (!standing.Unlocked)
        {
            StatusLine = $"{tribe.Name}: not unlocked (rank 0).";
            return false;
        }
        var slots = Math.Min(standing.SlotsLeft, _state.AllowanceLeft);
        _toRun = new Queue<ushort>(standing.AcceptedDailies); // finish any already in the journal too
        _toTurnIn.Clear();
        _turningIn = false;
        if (slots <= 0 && _toRun.Count == 0)
        {
            StatusLine = $"{tribe.Name}: nothing left today (slots {standing.SlotsLeft}, allowance {_state.AllowanceLeft}).";
            return false;
        }
        _tribe = tribe;
        _issuer = issuer;
        _dropped.Clear();
        _acceptTarget = standing.TakenToday + slots;

        // The issuer is only worth a trip when there is something to accept. Dailies already in
        // the journal — a restart mid-run, a fault, a reload — resume from wherever the character
        // stands: the quest controller picks each one up at the game's own sequence, and walking
        // back to the issuer first was a round trip for nothing.
        _needAccept = slots > 0;
        Enter(!_world.IsCombatJob ? TribeRunState.Job
            : _needAccept ? TribeRunState.Travel
            : TribeRunState.Run);
        _log(_needAccept
            ? $"{tribe.Name}: starting — accept up to {slots}, {_toRun.Count} already accepted."
            : $"{tribe.Name}: resuming {_toRun.Count} accepted dailies from here.");
        return true;
    }

    public void Stop()
    {
        if (State == TribeRunState.Run && _controller.State != RunState.Idle)
            _controller.Stop();
        _tribe = null;
        State = TribeRunState.Idle;
    }

    public void Tick()
    {
        if (_tribe is null || State is TribeRunState.Idle or TribeRunState.Done or TribeRunState.Faulted)
            return;
        try
        {
            switch (State)
            {
                case TribeRunState.Job: TickJob(); break;
                case TribeRunState.Travel: TickTravel(); break;
                case TribeRunState.Accept: TickAccept(); break;
                case TribeRunState.Run: TickRun(); break;
            }
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TickJob()
    {
        if (_world.IsCombatJob) { Enter(_needAccept ? TribeRunState.Travel : TribeRunState.Run); return; }
        var sets = _world.CombatGearsets();
        if (sets.Count == 0) { Fault($"{_tribe!.Name}: no combat gearset to switch to."); return; }
        if (_world.UtcNow - _lastInteract > TimeSpan.FromSeconds(1))
        {
            _world.EquipGearset(sets[0]);
            _lastInteract = _world.UtcNow;
        }
        if (_world.UtcNow - _phaseStart > JobStall)
            Fault($"{_tribe!.Name}: could not switch to a combat job.");
    }

    private void TickTravel()
    {
        var issuer = _issuer!;
        if (_world.TerritoryId == issuer.TerritoryId
            && Vector3.Distance(_world.PlayerPosition, issuer.Position) <= StepExecutor.DefaultStopDistance + StepExecutor.ArrivalSlack)
        {
            _travelExecutor.Cancel();
            Enter(TribeRunState.Accept);
            return;
        }
        // Reuse the step executor for the walk/teleport to the issuer position.
        if (_travelExecutor.Status != StepStatus.Running)
        {
            var aetheryte = _issuerAetheryte();
            _travelExecutor.Begin(new QuestStep
            {
                Kind = StepKind.WalkTo, KindName = "WalkTo",
                Position = issuer.Position, TerritoryId = issuer.TerritoryId,
                AetheryteShortcut = aetheryte,
            });
        }
        StatusLine = $"{_tribe!.Name}: going to the issuer";
        if (_travelExecutor.Tick() == StepStatus.Failed)
            Fault($"{_tribe!.Name}: could not reach the issuer — {_travelExecutor.FailReason}");
    }

    /// <summary>Odysseus's own travel picks the nearest aetheryte; leave null and let the executor teleport by distance.</summary>
    private string? _issuerAetheryte() => null;

    private void TickAccept()
    {
        var standing = _state.Read(_tribe!);
        StatusLine = $"{_tribe!.Name}: accepting {standing.TakenToday}/{_acceptTarget}";

        if (standing.TakenToday >= _acceptTarget)
        {
            foreach (var id in standing.AcceptedDailies)
                if (!_toRun.Contains(id))
                    _toRun.Enqueue(id);
            _log($"{_tribe.Name}: accepted; {_toRun.Count} to run.");
            Enter(TribeRunState.Run);
            return;
        }

        // Answer whatever the issuer put up, else (re)interact.
        if (_world.IsAddonVisible("SelectYesno")) { _world.SelectYesNo(true); Bump(); return; }
        if (_world.IsAddonVisible("JournalAccept")) { _world.SelectYesNo(true); Bump(); return; } // confirm = the yes-callback
        if (_world.IsAddonVisible("SelectIconString")) { _world.SelectIconStringIndex(0); Bump(); return; }
        if (_world.IsAddonVisible("SelectString")) { _world.SelectStringIndex(0); Bump(); return; }

        if (_world.IsOccupied)
            return; // mid-conversation; wait

        if (_world.UtcNow - _lastInteract > ReinteractGap)
        {
            if (_issuer!.ENpcId != 0 && !_world.TryInteractWithDataId(_issuer.ENpcId))
                _log($"{_tribe.Name}: issuer {_issuer.ENpcId} not in reach.");
            _lastInteract = _world.UtcNow;
        }
        if (_world.UtcNow - _phaseStart > AcceptStall)
            Fault($"{_tribe.Name}: could not accept at the issuer.");
    }

    private void Bump()
    {
        _lastInteract = _world.UtcNow;
        _phaseStart = _world.UtcNow; // progress: reset the stall clock
    }

    private void TickRun()
    {
        if (_running != 0)
        {
            // The runner owns the frame while it is active, so it must drive the controller too.
            _controller.Tick();

            if (_controller.State == RunState.Faulted)
            {
                _log($"{_tribe!.Name}: daily {_running} faulted ({_controller.StatusLine}) — dropping it.");
                _dropped.Add(_running);
                _controller.Stop();
                _running = 0;
                return;
            }
            if (_controller.State != RunState.Idle)
            {
                // Only while it is still our daily. The controller rolling on into the priority
                // list or the MSQ would otherwise read as a daily that never finishes — which
                // greys every society's Run button for the rest of the day, since they all share
                // this runner's state.
                if (_controller.QuestId == _running)
                {
                    StatusLine = $"{_tribe!.Name}: {_controller.StatusLine}";
                    return; // still running
                }
                _log($"{_tribe!.Name}: the controller rolled on to quest {_controller.QuestId} after the daily — stopping it.");
                _controller.Stop();
            }
            var finished = _running;
            _log($"{_tribe!.Name}: daily {finished} run ended (controller {_controller.State}, quest {_controller.QuestId}).");
            _running = 0; // controller went idle: the daily completed (or was stopped at its hand-in)
            // Objectives phase: the hand-in was held, so the daily is standing at sequence 255 —
            // it goes in the pile for the one trip home. A daily already turned in (or one the
            // roll-on guard stopped early) resumes from wherever it stands, which is also right.
            if (!_turningIn && finished != 0)
                _toTurnIn.Enqueue(finished);
        }

        // Next daily still not complete.
        while (_toRun.Count > 0)
        {
            var id = _toRun.Dequeue();
            // Skip only a daily the controller is actively running. QuestId is sticky after a
            // stop, and matching on it alone silently dropped the last-run daily from the
            // turn-in round — its hand-in never happened and nothing said so.
            if (_controller.State != RunState.Idle && _controller.QuestId == id) continue;
            if (!_controller.Start(id))
            {
                _log($"{_tribe!.Name}: no path/refused for daily {id} — skipping.");
                continue;
            }
            // A daily is one quest. Start resets this flag, so it is armed after, and it is what
            // keeps the controller from rolling into the priority list or the MSQ when it ends.
            _controller.StopAfterQuest = true;
            // Objectives first for the whole batch; the trips home all happen together after.
            _controller.HoldTurnIn = !_turningIn;
            _running = id;
            StatusLine = $"{_tribe!.Name}: {(_turningIn ? "turning in" : "running")} daily {id}";
            return;
        }

        if (!_turningIn && _toTurnIn.Count > 0)
        {
            _turningIn = true;
            while (_toTurnIn.Count > 0)
                _toRun.Enqueue(_toTurnIn.Dequeue());
            _log($"{_tribe!.Name}: objectives done for {_toRun.Count} — back to hand them in.");
            return;
        }

        Enter(TribeRunState.Done);
        // "Done." with a daily silently dropped read as everything having worked, while the quest
        // sat unfinished in the journal. Done says what happened to all of them.
        StatusLine = _dropped.Count == 0
            ? $"{_tribe!.Name}: done."
            : $"{_tribe!.Name}: done, but {_dropped.Count} dail{(_dropped.Count == 1 ? "y" : "ies")} " +
              $"faulted and {(_dropped.Count == 1 ? "was" : "were")} left in the journal ({string.Join(", ", _dropped)}) — " +
              "Run again to retry from where they stand.";
        _log(StatusLine);
    }

    private void Enter(TribeRunState state)
    {
        State = state;
        _phaseStart = _world.UtcNow;
        _lastInteract = default;
    }

    private void Fault(string reason)
    {
        _travelExecutor.Cancel();
        if (_controller.State != RunState.Idle) _controller.Stop();
        State = TribeRunState.Faulted;
        StatusLine = reason;
        _log($"FAULT: {reason}");
    }
}
