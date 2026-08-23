using System;
using System.Collections.Generic;
using System.Linq;
using Odysseus.Services.Deliveries;

namespace Odysseus.Services.Work;

/// <summary>The engines a work list is run on, behind one seam so the order can be tested without them.</summary>
public interface IWorkEngines
{
    /// <summary>False when the job cannot be started at all — nothing left today, not unlocked, wrong class.</summary>
    bool StartSociety(uint societyId, int count, out string reason);

    bool StartDelivery(uint clientId, DeliveryRoute route, int count, out string reason);

    /// <summary>Something is running right now.</summary>
    bool Busy { get; }

    /// <summary>The last thing to run ended badly; the reason is for the log, not for deciding.</summary>
    bool Faulted { get; }

    string FaultReason { get; }

    /// <summary>A name for a society or client id, for the log and the status line.</summary>
    string NameOf(WorkKind kind, uint targetId);
}

/// <summary>What became of one job.</summary>
public sealed record WorkOutcome(WorkItem Item, string Name, bool Ran, string Note);

public enum WorkRunState { Idle, Starting, Running, Done }

/// <summary>
/// Walks a <see cref="WorkList"/>, one job at a time.
///
/// <para>
/// The whole point is the order, so that is all this does: start the head of the list, wait for it,
/// record what happened, move on. It owns no automation of its own — societies and deliveries are
/// run by the engines that already exist and are already driven by their own windows.
/// </para>
///
/// <para>
/// <b>A failure costs one job, not the day.</b> A society that will not start, or a delivery that
/// faults halfway, is written down and the next job begins. With four clients running unattended
/// that is the difference between checking in twice and standing over them; and a run that stops
/// dead on job two of eight is the failure mode this exists to avoid.
/// </para>
/// </summary>
public sealed class WorkRunner
{
    private readonly IWorkEngines _engines;
    private readonly Action<string> _log;
    private readonly List<WorkItem> _queue = [];
    private readonly List<WorkOutcome> _outcomes = [];
    private WorkItem? _current;
    private string _currentName = string.Empty;

    public WorkRunner(IWorkEngines engines, Action<string> log)
    {
        _engines = engines;
        _log = log;
    }

    public WorkRunState State { get; private set; } = WorkRunState.Idle;

    public string Status { get; private set; } = string.Empty;

    /// <summary>What each job came to, in the order they were attempted.</summary>
    public IReadOnlyList<WorkOutcome> Outcomes => _outcomes;

    public int Remaining => _queue.Count + (_current is null ? 0 : 1);

    /// <summary>Run these jobs in order. <paramref name="limit"/> caps how many jobs run — 1 is the one-shot.</summary>
    public void Begin(IEnumerable<WorkItem> items, int limit = 0)
    {
        _queue.Clear();
        _outcomes.Clear();
        _current = null;
        _queue.AddRange(limit > 0 ? items.Take(limit) : items);
        State = _queue.Count > 0 ? WorkRunState.Starting : WorkRunState.Done;
        Status = _queue.Count > 0 ? $"{_queue.Count} job(s) to do" : "nothing to do";
    }

    public void Stop()
    {
        _queue.Clear();
        _current = null;
        State = WorkRunState.Idle;
        Status = "Stopped.";
    }

    public void Tick()
    {
        switch (State)
        {
            case WorkRunState.Starting: TickStart(); break;
            case WorkRunState.Running: TickRunning(); break;
        }
    }

    private void TickStart()
    {
        // Something else is still winding down; do not start on top of it.
        if (_engines.Busy)
            return;

        if (_queue.Count == 0)
        {
            Finish();
            return;
        }

        var item = _queue[0];
        _queue.RemoveAt(0);
        _currentName = _engines.NameOf(item.Kind, item.TargetId);

        var started = item.Kind switch
        {
            WorkKind.SocietyDailies => _engines.StartSociety(item.TargetId, item.Count, out _reason),
            WorkKind.Delivery => _engines.StartDelivery(item.TargetId, item.Route, item.Count, out _reason),
            _ => Refuse(out _reason),
        };

        if (!started)
        {
            // Not a failure of the day: this job could not begin, so it is written down and skipped.
            Record(item, ran: false, _reason.Length > 0 ? _reason : "would not start");
            return;
        }

        _current = item;
        State = WorkRunState.Running;
        Status = $"Running {item.Describe(_currentName)}";
        _log($"Work list: starting {item.Describe(_currentName)}.");
    }

    private string _reason = string.Empty;

    private static bool Refuse(out string reason)
    {
        reason = "unknown kind of job";
        return false;
    }

    private void TickRunning()
    {
        if (_engines.Busy)
            return;

        var item = _current!;
        _current = null;

        if (_engines.Faulted)
            Record(item, ran: false, _engines.FaultReason.Length > 0 ? _engines.FaultReason : "faulted");
        else
            Record(item, ran: true, string.Empty);

        State = WorkRunState.Starting;
    }

    private void Record(WorkItem item, bool ran, string note)
    {
        _outcomes.Add(new WorkOutcome(item, _currentName, ran, note));
        if (!ran)
            _log($"Work list: skipped {item.Describe(_currentName)} — {note}.");
        if (_queue.Count == 0)
            Finish();
        else
            State = WorkRunState.Starting;
    }

    private void Finish()
    {
        var ran = _outcomes.Count(o => o.Ran);
        var skipped = _outcomes.Count - ran;
        Status = skipped == 0
            ? $"Done: {ran} job(s)."
            : $"Done: {ran} job(s), {skipped} skipped — {string.Join("; ", _outcomes.Where(o => !o.Ran).Select(o => $"{o.Name}: {o.Note}"))}";
        State = WorkRunState.Done;
        _log($"Work list: {Status}");
    }
}
