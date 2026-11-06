using System;

namespace Odysseus.Services.Run;

/// <summary>What to do at a point between steps while a stop is asked for.</summary>
public enum StopVerdict
{
    /// <summary>The character is free, and has stayed free a moment: stop here.</summary>
    Stop,

    /// <summary>Wait here and begin nothing: a fight to end, a window to close, the free moment to settle.</summary>
    Hold,

    /// <summary>
    /// Waiting will not free the character: inside a duty, whose way out is the run's own next step, or a
    /// window still up after <see cref="StopRequest.WindowHoldMax"/>. A quest takes one more step and asks
    /// again after it; where the run has no step of its own left, it stops anyway.
    /// </summary>
    Carry,
}

/// <summary>
/// Another plugin's "stop after the run you are in" — <c>Odysseus.RequestStopAfterRun</c>, which Argus
/// sends before it moves the character to resend the Free Company's vessels.
///
/// <para>
/// Taken only while something of ours is busy, so no stale request is left to stop the next start, and
/// asking again is not a toggle. The runners read it between steps, never inside one, and stop at the
/// first of those points where the character is free. It is spent once nothing of ours is busy any more,
/// or by a fresh start. Pure, so the rules are pinned by tests.
/// </para>
/// </summary>
public sealed class StopRequest
{
    /// <summary>
    /// How long the character stays free before the stop: a turn-in's cutscene or the next talk can open a
    /// beat after the step that caused it ended, and once we have stopped nothing would advance it.
    /// </summary>
    public static readonly TimeSpan FreeSettle = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a dialogue, cutscene or menu is waited on. A fight is waited out — Daedalus fights for as
    /// long as we read busy — but a window can stand for good when it is the next step's to answer.
    /// </summary>
    public static readonly TimeSpan WindowHoldMax = TimeSpan.FromSeconds(30);

    private DateTime? _freeSince;
    private DateTime? _windowSince;

    public bool Requested { get; private set; }

    /// <summary>
    /// Ask for the stop. True when <paramref name="busy"/>: it will stop, or already is stopping. False,
    /// with nothing changed, when idle.
    /// </summary>
    public bool Request(bool busy)
    {
        if (!busy)
            return false;
        Requested = true;
        return true;
    }

    /// <summary>Drop the request: a fresh start supersedes it.</summary>
    public void Clear()
    {
        Requested = false;
        _freeSince = null;
        _windowSince = null;
    }

    /// <summary>Spend the request once nothing is busy any more. True when it was spent just now.</summary>
    public bool Settle(bool busy)
    {
        if (!Requested || busy)
            return false;
        Clear();
        return true;
    }

    /// <summary>At a point between steps, with the request standing: stop here, hold, or carry on.</summary>
    public StopVerdict At(bool inDuty, bool inCombat, bool occupied, DateTime now)
    {
        if (inDuty || inCombat)
        {
            _freeSince = null;
            _windowSince = null;
            return inDuty ? StopVerdict.Carry : StopVerdict.Hold;
        }
        if (occupied)
        {
            _freeSince = null;
            _windowSince ??= now;
            if (now - _windowSince.Value < WindowHoldMax)
                return StopVerdict.Hold;
            _windowSince = null; // the next point between steps waits afresh
            return StopVerdict.Carry;
        }
        _windowSince = null;
        _freeSince ??= now;
        return now - _freeSince.Value >= FreeSettle ? StopVerdict.Stop : StopVerdict.Hold;
    }
}
