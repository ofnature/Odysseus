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
    public List<int> CombatGearsetIds { get; } = [0];
    public bool EquipGearset(int id)
    {
        Calls.Add($"Gearset {id}");
        IsCombatJob = true;
        if (EquipLands && SavedGearsets.FirstOrDefault(g => g.Id == id) is { } set)
            CurrentClassJob = set.ClassJobId;
        return CombatGearsetIds.Contains(id) || SavedGearsets.Any(g => g.Id == id);
    }
    public IReadOnlyList<int> CombatGearsets() => CombatGearsetIds;

    // ── Class and job ──
    /// <summary>What <c>Gearsets()</c> reports; separate from the plain id list the tribe runner uses.</summary>
    public List<GearsetInfo> SavedGearsets { get; } = [];
    /// <summary>Equipping a gearset moves the class immediately; clear it to test the wait.</summary>
    public bool EquipLands { get; set; } = true;
    public uint CurrentClassJob { get; set; }
    public Dictionary<string, uint> ClassJobs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<ushort, uint> QuestStartJobs { get; } = new();
    public IReadOnlyList<GearsetInfo> Gearsets() => SavedGearsets;
    public uint? ResolveClassJob(string name)
        => ClassJobs.TryGetValue(name.Replace(" ", string.Empty), out var id) ? id : null;
    public uint? QuestStartClassJob(ushort questId)
        => QuestStartJobs.TryGetValue(questId, out var job) ? job : null;
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
    public bool MoveDirectTo(Vector3 destination, bool fly) => Move(destination, fly, direct: true);

    private bool Move(Vector3 destination, bool fly, bool direct = false)
    {
        Calls.Add($"Move{(direct ? "Direct" : "")} {destination.X:F0},{destination.Y:F0},{destination.Z:F0} fly={fly}");
        LastMoveTarget = destination;
        if (!MoveAccepted) return false;
        // A pathfind that finds nothing leaves you standing still — that is what zero waypoints
        // means, and modelling it is what lets the executor's retry-then-give-up be tested.
        IsMoving = PathWaypointCount != 0;
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
    /// <summary>Territory → an aetheryte in it the character has attuned. Absent means no way in.</summary>
    public Dictionary<uint, uint> AttunedByTerritory { get; } = new();
    /// <summary>Territory → the aethernet hop that reaches it, for a zone with no aetheryte of its own.</summary>
    public Dictionary<uint, (uint? Aetheryte, string Hop, uint Lands)> AethernetByTerritory { get; } = new();
    public TravelRoute? RouteTo(uint territoryId, Vector3? near)
    {
        if (AttunedByTerritory.TryGetValue(territoryId, out var id))
            return new TravelRoute(id, null, territoryId);
        if (AethernetByTerritory.TryGetValue(territoryId, out var v))
            return new TravelRoute(v.Aetheryte, v.Hop, v.Lands);
        return null;
    }
    public bool Teleport(uint aetheryteId)
    {
        Calls.Add($"Teleport {aetheryteId}");
        if (!TeleportAccepted) return false;
        if (ArriveOnTeleport && AetheryteTerritories.TryGetValue(aetheryteId, out var t)) TerritoryId = t;
        return true;
    }
    /// <summary>Aethernet destination → the zone it lands in, and where the fake puts you.</summary>
    public Dictionary<string, uint> AethernetTerritories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public uint? AethernetTerritoryOf(string destination)
        => AethernetTerritories.TryGetValue(destination, out var t) ? t : null;
    /// <summary>Territory → where its aethernet access point stands. Absent means none placed.</summary>
    public Dictionary<uint, Vector3> AethernetAccess { get; } = new();
    public Vector3? NearestAethernetAccess(uint territoryId, Vector3 near)
        => AethernetAccess.TryGetValue(territoryId, out var p) ? p : null;
    public bool AethernetTeleport(string destination)
    {
        Calls.Add($"Aethernet {destination}");
        if (!TeleportAccepted) return false;
        if (ArriveOnTeleport && AethernetTerritories.TryGetValue(destination, out var t)) TerritoryId = t;
        return true;
    }
    public void Mount() { Calls.Add("Mount"); IsMounted = true; }
    public bool IsQuestComplete(ushort questId) => CompletedQuests.Contains(questId);
    public bool IsQuestAccepted(ushort questId) => AcceptedQuests.Contains(questId);
    /// <summary>Both qualities together, as the real reader counts them.</summary>
    public Dictionary<uint, int> Bag { get; } = new();
    public int ItemCount(uint itemId) => Bag.GetValueOrDefault(itemId);
    /// <summary>The FC chest pages the client has loaded; never counted as held.</summary>
    public Dictionary<uint, int> FcChest { get; } = new();
    public int FreeCompanyChestCount(uint itemId) => FcChest.GetValueOrDefault(itemId);
    public bool IsDataIdSpawned(uint dataId) => Spawned.Contains(dataId);
    public float? DistanceToDataId(uint dataId) => Spawned.Contains(dataId) ? 1f : null;
    /// <summary>Spawned objects sit on the player unless a test places them somewhere.</summary>
    public Dictionary<uint, Vector3> Positions { get; } = new();
    public Vector3? PositionOfDataId(uint dataId)
        => Spawned.Contains(dataId) ? Positions.GetValueOrDefault(dataId, PlayerPosition) : null;
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

    // ── Making things ──
    public bool CrafterReady { get; set; } = true;
    public bool IsCrafting { get; set; }
    /// <summary>How many a craft actually delivers before Artisan stops; the default makes the whole order.</summary>
    public int CraftDelivers { get; set; } = int.MaxValue;
    /// <summary>Leave Artisan's loop running after the order, to exercise the waiting path.</summary>
    public bool CraftKeepsRunning { get; set; }
    /// <summary>The job it would craft as; null stands for "no recipe, or Artisan refused".</summary>
    public string? CraftJob { get; set; } = "BSM";
    public string CraftShortfallText { get; set; } = string.Empty;
    public string? StartCraft(uint itemId, int count)
    {
        Calls.Add($"Craft {count} x {itemId}");
        if (CraftJob is null) return null;
        Bag[itemId] = Bag.GetValueOrDefault(itemId) + Math.Min(CraftDelivers, count);
        IsCrafting = CraftKeepsRunning;
        return CraftJob;
    }
    public void StopCrafting() { Calls.Add("StopCraft"); IsCrafting = false; }
    public string CraftShortfall(uint itemId, int count) => CraftShortfallText;

    public bool GathererReady { get; set; } = true;
    public bool IsGathering { get; set; }
    public bool GathererIdle { get; set; }
    public string GathererStatus { get; set; } = string.Empty;
    public bool GatherStarts { get; set; } = true;
    /// <summary>Item → how many starting the gatherer puts in the bag; nothing by default.</summary>
    public Dictionary<uint, int> GatherDelivers { get; } = new();
    public bool StartGathering()
    {
        Calls.Add("StartGather");
        if (!GatherStarts) return false;
        IsGathering = true;
        foreach (var (id, n) in GatherDelivers) Bag[id] = Bag.GetValueOrDefault(id) + n;
        return true;
    }
    public void StopGathering() { Calls.Add("StopGather"); IsGathering = false; }

    // ── Actions ──
    public bool TryTargetDataId(uint dataId) { Calls.Add($"Target {dataId}"); return Spawned.Contains(dataId); }
    public void SendChatCommand(string command) => Calls.Add($"Chat {command}");
    public bool UseItemAccepted { get; set; } = true;
    public bool UseItem(uint itemId) { Calls.Add($"UseItem {itemId}"); return UseItemAccepted; }
    public Dictionary<string, uint> Actions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public uint? ResolveAction(string name) => Actions.TryGetValue(name, out var id) ? id : null;
    public bool UseActionAccepted { get; set; } = true;
    public bool UseAction(uint actionId, Vector3? ground) { Calls.Add($"UseAction {actionId}{(ground is { } g ? $" @({g.X:F0},{g.Y:F0},{g.Z:F0})" : "")}"); return UseActionAccepted; }
    // ── Vendors ──
    /// <summary>The shop the window is showing, or 0 for none. Tests set it or let OpenShop set it.</summary>
    public uint OpenShopId { get; set; }
    /// <summary>What OpenShop lands on; 0 means "whatever was asked for".</summary>
    public uint ShopOpensAs { get; set; }
    public bool OpenShopAccepted { get; set; } = true;
    public bool BuyAccepted { get; set; } = true;
    public bool ShopIsBusy { get; set; }
    /// <summary>How many a buy actually delivers; null means "everything asked for", 0 means a shop that takes the order and does nothing.</summary>
    public int? BuyDelivers { get; set; }
    public bool IsShopOpen(uint shopId) => OpenShopId != 0 && (shopId == 0 || shopId == OpenShopId);
    public bool OpenShop(uint vendorDataId, uint shopId)
    {
        Calls.Add($"OpenShop {vendorDataId}/{shopId}");
        if (!OpenShopAccepted) return false;
        OpenShopId = ShopOpensAs != 0 ? ShopOpensAs : shopId;
        return true;
    }
    public bool BuyFromShop(uint shopId, uint itemId, int count)
    {
        Calls.Add($"Buy {count} x {itemId} from {shopId}");
        if (!BuyAccepted) return false;
        Bag[itemId] = Bag.GetValueOrDefault(itemId) + (BuyDelivers is { } d ? Math.Min(d, count) : count);
        return true;
    }
    public bool ShopBusy(uint shopId) => ShopIsBusy;
    public void CloseShop() { Calls.Add("CloseShop"); OpenShopId = 0; }
    public int Gil { get; set; } = 100_000;

    // ── The Request window ──
    public List<HandOverRequest> Requests { get; } = [];
    public bool CanSatisfyHandOver { get; set; } = true;
    public bool HandOverAccepted { get; set; } = true;
    public IReadOnlyList<HandOverRequest> HandOverRequests
        => VisibleAddons.Contains("Request") ? Requests : [];
    public bool CompleteHandOverWindow()
    {
        Calls.Add("HandOver");
        if (!VisibleAddons.Contains("Request") || !HandOverAccepted) return false;
        VisibleAddons.Remove("Request");
        IsOccupied = false;
        return true;
    }

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
