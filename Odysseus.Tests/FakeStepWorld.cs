using System.Numerics;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>Scriptable world for executor and controller tests. Everything is a plain field the test pokes.</summary>
public sealed class FakeStepWorld : IStepWorld, IConditionWorld
{
    public DateTime UtcNow { get; set; } = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
    public Vector3 PlayerPosition { get; set; }
    public uint TerritoryId { get; set; } = 400;
    public bool NavmeshReady { get; set; } = true;
    public bool IsMoving { get; set; }
    public int PathWaypointCount { get; set; } = 5;
    public bool IsMounted { get; set; }
    public bool CanFlyHere { get; set; }
    public int PlayerLevel { get; set; } = 54;
    public bool IsCasting { get; set; }
    public bool IsCombatJob { get; set; } = true;
    public List<int> Gearsets { get; } = [0];
    public bool EquipGearset(int id) { Calls.Add($"Gearset {id}"); IsCombatJob = true; return Gearsets.Contains(id); }
    public IReadOnlyList<int> CombatGearsets() => Gearsets;
    public List<string> IconEntries { get; } = [];
    public IReadOnlyList<string> SelectIconStringEntries() => VisibleAddons.Contains("SelectIconString") ? IconEntries : [];
    public void SelectIconStringIndex(int index) => Calls.Add($"IconSelect {index}");
    public bool InCombat { get; set; }
    public bool IsReady { get; set; } = true;
    public bool IsOccupied { get; set; }
    public bool IsDead { get; set; }

    public HashSet<uint> Spawned { get; } = [];
    public HashSet<ushort> CompletedQuests { get; } = [];
    public HashSet<ushort> AcceptedQuests { get; } = [];
    public HashSet<string> VisibleAddons { get; } = [];
    public Queue<bool> AttackResults { get; } = new();

    public List<string> Calls { get; } = [];
    public Vector3? LastMoveTarget { get; private set; }
    public bool MoveAccepted { get; set; } = true;
    /// <summary>When set, a MoveTo teleports the player there on the next tick (arrival shortcut).</summary>
    public bool ArriveOnMove { get; set; }

    public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);

    public bool MoveTo(Vector3 destination, bool fly) => Move(destination, fly);
    public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly) => Move(destination, fly);

    private bool Move(Vector3 destination, bool fly)
    {
        Calls.Add($"Move {destination.X:F0},{destination.Y:F0},{destination.Z:F0} fly={fly}");
        LastMoveTarget = destination;
        if (!MoveAccepted) return false;
        IsMoving = true;
        if (ArriveOnMove) { PlayerPosition = destination; IsMoving = false; }
        return true;
    }

    public void StopMoving() { Calls.Add("Stop"); IsMoving = false; }

    // ── Travel ──
    public Dictionary<string, uint> Aetherytes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<uint, uint> AetheryteTerritories { get; } = new();
    public bool TeleportAccepted { get; set; } = true;
    public bool IsTravelBusy { get; set; }
    /// <summary>When set, a Teleport lands the player in the aetheryte's territory immediately.</summary>
    public bool ArriveOnTeleport { get; set; } = true;
    public uint? ResolveAetheryte(string name) => Aetherytes.TryGetValue(name, out var id) ? id : null;
    public uint? AetheryteTerritory(uint aetheryteId) => AetheryteTerritories.TryGetValue(aetheryteId, out var t) ? t : null;
    public bool Teleport(uint aetheryteId)
    {
        Calls.Add($"Teleport {aetheryteId}");
        if (!TeleportAccepted) return false;
        if (ArriveOnTeleport && AetheryteTerritories.TryGetValue(aetheryteId, out var t)) TerritoryId = t;
        return true;
    }
    public bool AethernetTeleport(string destination) { Calls.Add($"Aethernet {destination}"); return TeleportAccepted; }
    public void Mount() { Calls.Add("Mount"); IsMounted = true; }
    public bool IsQuestComplete(ushort questId) => CompletedQuests.Contains(questId);
    public bool IsQuestAccepted(ushort questId) => AcceptedQuests.Contains(questId);
    public bool IsDataIdSpawned(uint dataId) => Spawned.Contains(dataId);
    public float? DistanceToDataId(uint dataId) => Spawned.Contains(dataId) ? 1f : null;
    public bool TryInteractWithDataId(uint dataId)
    {
        Calls.Add($"Interact {dataId}");
        return Spawned.Contains(dataId);
    }
    public bool AttackNearestEnemy(IReadOnlyCollection<uint> dataIds, float radius)
    {
        Calls.Add("Attack");
        return AttackResults.Count > 0 && AttackResults.Dequeue();
    }
    // ── Instances and handoffs ──
    public bool InDuty { get; set; }
    public bool BossModAi { get; private set; }
    public Dictionary<uint, Odysseus.Services.Quest.DutyDescription> Duties { get; } = new();
    public Odysseus.Services.Quest.DutyDescription? DescribeDuty(uint cfc) => Duties.TryGetValue(cfc, out var d) ? d : null;
    public bool TheseusCanEnterDuty { get; set; }
    public bool TheseusEnterAccepted { get; set; } = true;
    public bool TheseusBusy { get; set; }
    public void SetBossModAi(bool enabled) { BossModAi = enabled; Calls.Add($"BmrAi {enabled}"); }
    public bool TheseusEnterDuty(uint cfc) { Calls.Add($"TheseusEnter {cfc}"); if (TheseusEnterAccepted) TheseusBusy = true; return TheseusEnterAccepted; }

    // ── Actions ──
    public bool TryTargetDataId(uint dataId) { Calls.Add($"Target {dataId}"); return Spawned.Contains(dataId); }
    public void SendChatCommand(string command) => Calls.Add($"Chat {command}");
    public bool UseItemAccepted { get; set; } = true;
    public bool UseItem(uint itemId) { Calls.Add($"UseItem {itemId}"); return UseItemAccepted; }
    public Dictionary<string, uint> Actions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public uint? ResolveAction(string name) => Actions.TryGetValue(name, out var id) ? id : null;
    public bool UseActionAccepted { get; set; } = true;
    public bool UseAction(uint actionId, Vector3? ground) { Calls.Add($"UseAction {actionId}{(ground is { } g ? $" @({g.X:F0},{g.Y:F0},{g.Z:F0})" : "")}"); return UseActionAccepted; }
    public bool RecommendedGearReady { get; set; } = true;
    public bool PrepareRecommendedGear() { Calls.Add("PrepareGear"); return true; }
    public void EquipRecommendedGear() => Calls.Add("EquipGear");

    public bool IsAddonVisible(string name) => VisibleAddons.Contains(name);
    public void SelectYesNo(bool yes) => Calls.Add($"YesNo {yes}");
    public void SelectStringIndex(int index) => Calls.Add($"Select {index}");
    public bool RewardCompleteEnabled { get; set; } = true;
    public bool CompleteQuestRewardWindow()
    {
        Calls.Add("CompleteReward");
        if (!VisibleAddons.Contains("JournalResult") || !RewardCompleteEnabled) return false;
        VisibleAddons.Remove("JournalResult");
        IsOccupied = false;
        return true;
    }
    public List<string> ListEntries { get; } = [];
    public IReadOnlyList<string> SelectStringEntries() => VisibleAddons.Contains("SelectString") ? ListEntries : [];
    public void HoldDialogue() => Calls.Add("HoldDialogue");
    public void ReleaseDialogue() => Calls.Add("ReleaseDialogue");
    public void Log(string message) => Calls.Add("Log " + message);
}
