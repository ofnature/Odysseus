namespace Odysseus.Services.Run;

/// <summary>
/// The run state machine from the plan doc. Movement, combat and dialogue ownership is defined
/// per state and must stay unambiguous — that table is the contract:
///
/// <code>
/// State      Movement                  Combat              Dialogue
/// Travel     Lifestream → vnavmesh     —                   —
/// Step       Odysseus (vnav)           —                   Odysseus chooses, TextAdvance advances
/// Combat     Odysseus (vnav)           Daedalus rotation   —
/// Handoff    BossMod AI / Theseus      Daedalus rotation   TextAdvance
/// </code>
///
/// BossMod suppresses its own movement automatically while a vnavmesh path is running, so the
/// only explicit toggle is <c>/bmrai on|off</c> around a solo instance (Theseus findings #2, #3).
/// </summary>
public enum RunState
{
    /// <summary>Nothing in flight.</summary>
    Idle,

    /// <summary>Choosing the next quest and loading its path.</summary>
    Select,

    /// <summary>Teleporting / aethernetting / mounting toward the step's zone.</summary>
    Travel,

    /// <summary>Executing a step: walking the last leg, interacting, choosing dialogue.</summary>
    Step,

    /// <summary>Quest enemies engaged; the rotation plugin is killing them.</summary>
    Combat,

    /// <summary>A solo duty or a full duty is running under BossMod / Theseus.</summary>
    Handoff,

    /// <summary>The quest's sequence advanced; deciding the next step or the next quest.</summary>
    Advance,

    /// <summary>
    /// Off the rails — the live quest state disagrees with the step we think we are on, or a
    /// step stalled. Read the game and reconcile via the Wake instead of walking on.
    /// </summary>
    Reconcile,

    /// <summary>Stopped and waiting for the user; the reason is on the run status line.</summary>
    Faulted,
}

public static class RunStateExtensions
{
    /// <summary>
    /// Odysseus is driving the character. This is the value published on <c>Odysseus.IsBusy</c>,
    /// so it decides when Daedalus fights for us — every state that moves, talks or waits on a
    /// handoff counts, and only the two states where the character is under nobody's control do not.
    /// </summary>
    public static bool IsDriving(this RunState state)
        => state is not (RunState.Idle or RunState.Faulted);
}
