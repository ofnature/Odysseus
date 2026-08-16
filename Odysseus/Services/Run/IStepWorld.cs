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

    // ── Travel ──

    /// <summary>Aetheryte id for a name as the path data spells it, or null when unknown.</summary>
    uint? ResolveAetheryte(string name);

    /// <summary>The zone an aetheryte stands in.</summary>
    uint? AetheryteTerritory(uint aetheryteId);

    /// <summary>Start a teleport. False when refused outright (no Lifestream, unknown/locked aetheryte).</summary>
    bool Teleport(uint aetheryteId);

    /// <summary>Start an aethernet hop to a shard by its display name. False when refused.</summary>
    bool AethernetTeleport(string destination);

    /// <summary>A teleport or aethernet hop is in progress (Lifestream busy, or the player is between areas).</summary>
    bool IsTravelBusy { get; }

    // ── Player state ──

    int PlayerLevel { get; }

    bool IsCasting { get; }

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

    // ── Instances and handoffs ──

    /// <summary>Inside any instanced duty (solo or otherwise).</summary>
    bool InDuty { get; }

    /// <summary>Hands the fight to, or takes it back from, BossMod's AI (<c>/bmrai on|off</c>).</summary>
    void SetBossModAi(bool enabled);

    /// <summary>What a ContentFinderCondition is, or null when the sheet does not know it.</summary>
    Quest.DutyDescription? DescribeDuty(uint contentFinderConditionId);

    /// <summary>Theseus is loaded and can begin a duty right now.</summary>
    bool TheseusCanEnterDuty { get; }

    /// <summary>Ask Theseus to enter and run a duty. False when refused.</summary>
    bool TheseusEnterDuty(uint contentFinderConditionId);

    /// <summary>Theseus is driving the character.</summary>
    bool TheseusBusy { get; }

    // ── Actions ──

    /// <summary>Targets the nearest object with this data id without interacting. False when absent.</summary>
    bool TryTargetDataId(uint dataId);

    /// <summary>Sends a slash command as the player (emotes, jump).</summary>
    void SendChatCommand(string command);

    /// <summary>Uses an inventory item by id, on the current target if it needs one.</summary>
    bool UseItem(uint itemId);

    /// <summary>Action row id for a name as the path data spells it, or null when unknown.</summary>
    uint? ResolveAction(string name);

    /// <summary>Use an action on the current target (or at a ground point). False when refused.</summary>
    bool UseAction(uint actionId, Vector3? groundTarget);

    /// <summary>Ask the game to compute recommended gear for the current job. Async; poll <see cref="RecommendedGearReady"/>.</summary>
    bool PrepareRecommendedGear();

    bool RecommendedGearReady { get; }

    /// <summary>Equip what was computed.</summary>
    void EquipRecommendedGear();

    // ── UI ──

    bool IsAddonVisible(string name);

    /// <summary>Answers a yes/no dialog if one is showing.</summary>
    void SelectYesNo(bool yes);

    /// <summary>Picks an entry in a list dialog if one is showing.</summary>
    void SelectStringIndex(int index);

    /// <summary>The entries of the list dialog currently showing, in order; empty when none.</summary>
    System.Collections.Generic.IReadOnlyList<string> SelectStringEntries();

    /// <summary>
    /// Press Complete on the quest reward window. Returns false when the window is not up or the
    /// button is disabled — which means an optional reward still needs choosing.
    /// </summary>
    bool CompleteQuestRewardWindow();

    /// <summary>Ask TextAdvance to drive dialogue for us / stop.</summary>
    void HoldDialogue();

    void ReleaseDialogue();

    void Log(string message);
}
