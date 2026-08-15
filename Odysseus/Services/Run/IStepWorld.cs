using System;
using System.Numerics;

namespace Odysseus.Services.Run;

/// <summary>
/// Everything the step executor needs from the game and from other plugins, behind one seam.
///
/// <para>
/// The executor is where step logic lives — arrival checks, interact-then-wait, combat gating —
/// and that logic is both the easiest to get wrong and the only part testable without a client.
/// Keeping every game touch behind this interface is what makes those tests possible; the real
/// implementation is a thin translation layer with no decisions in it.
/// </para>
/// </summary>
public interface IStepWorld
{
    DateTime UtcNow { get; }

    Vector3 PlayerPosition { get; }

    uint TerritoryId { get; }

    // ── Navigation ──

    /// <summary>The zone's navmesh is built. Movement before this silently does nothing.</summary>
    bool NavmeshReady { get; }

    /// <summary>A path is being computed or followed.</summary>
    bool IsMoving { get; }

    /// <summary>Waypoints in the current path, or -1 when unreadable. Zero after a pathfind means unreachable.</summary>
    int PathWaypointCount { get; }

    /// <summary>Paths to a point. False when the request was refused outright.</summary>
    bool MoveTo(Vector3 destination, bool fly);

    /// <summary>Paths to within a tolerance of a point, for standing next to something.</summary>
    bool MoveCloseTo(Vector3 destination, float tolerance, bool fly);

    void StopMoving();

    /// <summary>Mounted right now.</summary>
    bool IsMounted { get; }

    /// <summary>Summon a mount (mount roulette). Async — poll <see cref="IsMounted"/>.</summary>
    void Mount();

    /// <summary>Flying is available in the current zone (aether currents attuned).</summary>
    bool CanFlyHere { get; }

    // ── Player state ──

    bool InCombat { get; }

    /// <summary>Not occupied, casting, zoning or otherwise mid-something.</summary>
    bool IsReady { get; }

    bool IsOccupied { get; }

    bool IsDead { get; }

    // ── World objects ──

    /// <summary>The object with this data id is present in the object table (spawned).</summary>
    bool IsDataIdSpawned(uint dataId);

    /// <summary>Distance from the player to the nearest object with this data id, or null when absent.</summary>
    float? DistanceToDataId(uint dataId);

    /// <summary>Targets and interacts with the nearest object with this data id. False when it is not there.</summary>
    bool TryInteractWithDataId(uint dataId);

    /// <summary>Targets and engages the nearest attackable object whose data id is in <paramref name="dataIds"/> (any, if empty). False when none within radius.</summary>
    bool AttackNearestEnemy(System.Collections.Generic.IReadOnlyCollection<uint> dataIds, float radius);

    // ── UI ──

    bool IsAddonVisible(string name);

    /// <summary>Answers a yes/no dialog if one is showing.</summary>
    void SelectYesNo(bool yes);

    /// <summary>Picks an entry in a list dialog if one is showing.</summary>
    void SelectStringIndex(int index);

    /// <summary>Ask TextAdvance to drive dialogue for us / stop.</summary>
    void HoldDialogue();

    void ReleaseDialogue();

    void Log(string message);
}
