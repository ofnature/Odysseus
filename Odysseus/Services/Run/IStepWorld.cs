using System;
using System.Numerics;

namespace Odysseus.Services.Run;

/// <summary>What a class/job is for. Crafters and gatherers share role 0, so the role alone cannot tell them apart.</summary>
public enum JobKind
{
    Other,
    Combat,
    Crafter,
    Gatherer,
}

/// <summary>
/// One saved gearset. <paramref name="ParentClassJobId"/> is the class a job grew out of — Conjurer
/// for White Mage — and is what lets a step asking for "Conjurer" be satisfied by the White Mage
/// gearset a character past level 30 actually has.
/// </summary>
public sealed record GearsetInfo(int Id, uint ClassJobId, uint ParentClassJobId, int Level, JobKind Kind);

/// <summary>One base material a craft is short of, after the recipe tree has been walked to the bottom.</summary>
public sealed record MaterialShortfall(uint ItemId, string Name, int Missing);

/// <summary>A merchant who sells something, and what they want for it.</summary>
public sealed record VendorOffer(uint VendorDataId, uint ShopId, string VendorName, uint UnitPrice);

/// <summary>One slot of the NPC hand-over window: an item, and how many of it it wants.</summary>
public sealed record HandOverRequest(uint ItemId, string Name, int Quantity);

/// <summary>
/// How to reach a zone the path does not say how to reach.
///
/// <para>
/// The two legs exist because half a city can hold no aetheryte at all — Ul'dah's Steps of Thal
/// has six aethernet shards and nothing to teleport to — so getting there means teleporting to the
/// city and then hopping. Either leg may stand alone: a plain zone is one teleport, and the other
/// half of the city you are already standing in is one hop.
/// </para>
/// </summary>
/// <param name="AetheryteId">Teleport here first, or null when already in the right city.</param>
/// <param name="AethernetName">Then hop here, or null when the teleport lands you in the zone.</param>
/// <param name="AetheryteTerritory">The zone the teleport lands in — not the destination when a hop follows.</param>
public sealed record TravelRoute(uint? AetheryteId, string? AethernetName, uint AetheryteTerritory);

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

    /// <summary>
    /// The loaded mesh has a point within <paramref name="within"/> yalms of <paramref name="point"/>
    /// that is reachable from where the player stands. False at the player's own feet means the mesh
    /// that is loaded does not cover this spot at all — a stale mesh for the zone, usually.
    /// </summary>
    bool MeshReaches(Vector3 point, float within);

    /// <summary>
    /// How far through building this zone's mesh the pathfinder is, or negative when it is not
    /// building. A mesh on its way is worth waiting for; one that is not coming is a fault.
    /// </summary>
    float NavmeshBuildProgress { get; }

    /// <summary>A path is being computed or followed.</summary>
    bool IsMoving { get; }

    /// <summary>Waypoints in the current path, or -1 when unreadable. Zero after a pathfind means unreachable.</summary>
    int PathWaypointCount { get; }

    /// <summary>Paths to a point. False when the request was refused outright.</summary>
    bool MoveTo(Vector3 destination, bool fly);

    /// <summary>Paths to within a tolerance of a point, for standing next to something.</summary>
    bool MoveCloseTo(Vector3 destination, float tolerance, bool fly);

    /// <summary>
    /// Walks straight at a point with no pathfinding — for steps marked <c>DisableNavmesh</c>,
    /// where the path author found the mesh gets it wrong.
    /// </summary>
    bool MoveDirectTo(Vector3 destination, bool fly);

    void StopMoving();

    /// <summary>Mounted right now.</summary>
    bool IsMounted { get; }

    /// <summary>Airborne on the mount. Dismounting from here is a fall; landing first is not.</summary>
    bool IsInFlight { get; }

    /// <summary>Summon a mount (mount roulette). Async — poll <see cref="IsMounted"/>.</summary>
    void Mount();

    /// <summary>
    /// Put the mount away. Async, like <see cref="Mount"/>. Needed because a flight that ends over
    /// an NPC's head cannot talk to it: interacting from the air does nothing at all.
    /// </summary>
    void Dismount();

    /// <summary>Flying is available in the current zone (aether currents attuned).</summary>
    bool CanFlyHere { get; }

    /// <summary>A zone from the base game, where the path data's flying gets caught on the scenery.</summary>
    bool InBaseGameZone { get; }

    /// <summary>
    /// Mounts are allowed in the current zone. The cities forbid them, and asking anyway puts an
    /// error on screen and leaves the run standing there having waited for a mount that is never
    /// coming.
    /// </summary>
    bool CanMountHere { get; }

    // ── Travel ──

    /// <summary>Aetheryte id for a name as the path data spells it, or null when unknown.</summary>
    uint? ResolveAetheryte(string name);

    /// <summary>The zone an aetheryte stands in.</summary>
    uint? AetheryteTerritory(uint aetheryteId);

    /// <summary>
    /// How to get to a zone, using only aetherytes the character has attuned. Null when there is
    /// no way in.
    ///
    /// <para>
    /// This is how a run reaches a quest it is not standing next to. Most steps name their own
    /// shortcut, but 1,851 of the bundle's 4,255 quests open on a step that names a territory and
    /// no aetheryte — they work when the previous quest left you in the right place and not
    /// otherwise, which is exactly the case where pressing Start should still work.
    /// </para>
    /// </summary>
    TravelRoute? RouteTo(uint territoryId, Vector3? near);

    /// <summary>Start a teleport. False when refused outright (no Lifestream, unknown/locked aetheryte).</summary>
    bool Teleport(uint aetheryteId);

    /// <summary>
    /// Start an aethernet hop. False when refused outright.
    /// </summary>
    /// <param name="byNameOnly">
    /// Skip the id-addressed route and ask by display name. The two take different paths inside
    /// Lifestream, and a hop refused one way has been seen to work the other, so a retry uses this
    /// rather than repeating an attempt that already failed.
    /// </param>
    bool AethernetTeleport(string destination, bool byNameOnly = false);

    /// <summary>
    /// The zone an aethernet destination sits in, or null when the sheet does not know the name.
    /// Lets a hop be judged by where it landed rather than by having stopped being busy.
    /// </summary>
    uint? AethernetTerritoryOf(string destination);

    /// <summary>
    /// Where to stand to use the aethernet in this zone — the nearest shard or city aetheryte.
    /// Null when the zone has none, or none whose position the sheet records.
    /// </summary>
    Vector3? NearestAethernetAccess(uint territoryId, Vector3 near);

    /// <summary>
    /// Standing at an aetheryte or aethernet shard, as the game reckons it rather than as a
    /// distance. False also when Lifestream cannot say, so it is only ever used to <i>allow</i> a
    /// hop, never to refuse one.
    /// </summary>
    bool AtAethernetShard { get; }

    /// <summary>A teleport or aethernet hop is in progress (Lifestream busy, or the player is between areas).</summary>
    bool IsTravelBusy { get; }

    // ── Player state ──

    int PlayerLevel { get; }

    bool IsCasting { get; }

    /// <summary>The current class/job is a Disciple of War or Magic.</summary>
    bool IsCombatJob { get; }

    /// <summary>Equip a gearset by id (0-based). False when refused.</summary>
    bool EquipGearset(int gearsetId);

    /// <summary>Gearset ids whose class is a combat job, in order.</summary>
    System.Collections.Generic.IReadOnlyList<int> CombatGearsets();

    /// <summary>Every saved gearset, in slot order.</summary>
    System.Collections.Generic.IReadOnlyList<GearsetInfo> Gearsets();

    /// <summary>The ClassJob row the character is on right now; 0 when unreadable.</summary>
    uint CurrentClassJob { get; }

    /// <summary>What sort of class is being played right now — combat, crafter, gatherer.</summary>
    JobKind CurrentJobKind { get; }

    /// <summary>ClassJob row id for a class name as the path data spells it, or null when unknown.</summary>
    uint? ResolveClassJob(string name);

    /// <summary>
    /// The class the character was on when it accepted a quest, or null when the quest is not in
    /// the journal. Some steps switch back to it after a detour onto another job.
    /// </summary>
    uint? QuestStartClassJob(ushort questId);

    /// <summary>That item is in an equipment slot right now.</summary>
    bool IsEquipped(uint itemId);

    /// <summary>
    /// The single class a main-hand tool makes you, or null when the item is not one.
    ///
    /// <para>
    /// Being a Goldsmith is what a Chaser Hammer is <i>for</i> — the game changes your class off the
    /// main hand, so any Goldsmith tool does it and the weathered one the quest hands over is just
    /// the one you are given when you own none. This is what lets an equip fall back to a gearset.
    /// Deliberately null for gear that merely happens to be restricted, where the item itself is
    /// the requirement and no gearset substitutes for it.
    /// </para>
    /// </summary>
    uint? EquipClassOf(uint itemId);

    /// <summary>
    /// Move an item out of the bags or armoury into the slot it belongs in. False when it is not
    /// equipment, or is nowhere to be found. Async — poll <see cref="IsEquipped"/>.
    /// </summary>
    bool EquipItem(uint itemId);

    /// <summary>Save what is equipped now as a new gearset. False when all 100 slots are taken.</summary>
    bool CreateGearset();

    /// <summary>Overwrite the active gearset with what is equipped now. False when there is none.</summary>
    bool UpdateGearset();

    bool InCombat { get; }

    /// <summary>Not occupied, casting, zoning or otherwise mid-something.</summary>
    bool IsReady { get; }

    bool IsOccupied { get; }

    bool IsDead { get; }

    // ── World objects ──

    /// <summary>The object with this data id is present in the object table (spawned).</summary>
    bool IsDataIdSpawned(uint dataId);

    /// <summary>
    /// Turn to face an object. The walk that gets us there ends pointed wherever the last leg of
    /// the path happened to be going, which is often past the target rather than at it.
    /// </summary>
    void FaceDataId(uint dataId);

    /// <summary>
    /// Whether the object is wearing a quest marker — the icon over its head that says it has
    /// something for you. Reads false for one that is not loaded, so absence proves nothing.
    /// </summary>
    bool HasQuestMarker(uint dataId);

    /// <summary>Distance from the player to the nearest object with this data id, or null when absent.</summary>
    float? DistanceToDataId(uint dataId);

    /// <summary>Targets and interacts with the nearest object with this data id. False when it is not there.</summary>
    /// <summary>Where a spawned object is, or null when it is not loaded.</summary>
    Vector3? PositionOfDataId(uint dataId);

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

    // ── Making things ──
    //
    // Odysseus neither crafts nor gathers. A Craft step asks Artisan for N of a recipe and watches
    // the bag; a Gather step switches GatherBuddy on and watches the bag. Same handoff doctrine as
    // Theseus above: we say what we want, wait, and stop with a reason if it does not arrive.

    /// <summary>Artisan is loaded and answering.</summary>
    bool CrafterReady { get; }

    /// <summary>Artisan's endurance loop is running.</summary>
    bool IsCrafting { get; }

    /// <summary>
    /// The recipe to run right now to get closer to making <paramref name="count"/> of an item,
    /// deepest first — twelve Copper Rings with no ingots in the bag answers "twelve Copper Ingot".
    /// Null when it is already held, or when what is missing cannot be crafted at all.
    /// </summary>
    (uint ItemId, int Count)? NextCraft(uint itemId, int count);

    /// <summary>
    /// Ask for <paramref name="count"/> of an item. Returns the job it will craft as, or null when
    /// the item has no recipe or Artisan refused.
    /// </summary>
    string? StartCraft(uint itemId, int count);

    void StopCrafting();

    /// <summary>
    /// What making <paramref name="count"/> of an item is still short of, followed to the bottom of
    /// the recipe tree so only what cannot itself be crafted is named. Empty when nothing is short.
    /// </summary>
    System.Collections.Generic.IReadOnlyList<MaterialShortfall> CraftShortfall(uint itemId, int count);

    /// <summary>
    /// A merchant standing within reach who sells this, or null. Restricted to one who is actually
    /// here: a shop cannot be opened across a zone, and a character new enough to be short of ore
    /// is exactly the one who should not be sent hunting for a vendor they cannot see.
    /// </summary>
    VendorOffer? VendorNearbyFor(uint itemId);

    /// <summary>GatherBuddy is loaded and answering.</summary>
    bool GathererReady { get; }

    /// <summary>Its auto-gather switch is on.</summary>
    bool IsGathering { get; }

    /// <summary>On, but with nothing it can reach — a timed node, the wrong job, or an item on no list.</summary>
    bool GathererIdle { get; }

    /// <summary>Whatever it is telling its own window, which is the only reason it gives.</summary>
    string GathererStatus { get; }

    bool StartGathering();

    void StopGathering();

    // ── Actions ──

    /// <summary>Targets the nearest object with this data id without interacting. False when absent.</summary>
    bool TryTargetDataId(uint dataId);

    /// <summary>Sends a slash command as the player (emotes, jump).</summary>
    void SendChatCommand(string command);

    /// <summary>Uses an inventory item by id, on the current target if it needs one.</summary>
    bool UseItem(uint itemId);

    /// <summary>Throw a ground-targeted quest item at a spot — a scalebomb at a suspicious object.</summary>
    bool UseItemOnGround(uint itemId, Vector3 position);

    /// <summary>Action row id for a name as the path data spells it, or null when unknown.</summary>
    uint? ResolveAction(string name);

    /// <summary>Use an action on the current target (or at a ground point). False when refused.</summary>
    bool UseAction(uint actionId, Vector3? groundTarget);

    // ── Vendors ──
    //
    // The same shop machinery the delivery runner buys craft ingredients with; a PurchaseItem step
    // is the same three moves (open, buy, verify) against a shop the path names.

    /// <summary>The vendor window is open. <c>0</c> asks only whether <i>a</i> shop is open.</summary>
    bool IsShopOpen(uint shopId);

    /// <summary>The event id of the shop that is open right now, or 0 when none is.</summary>
    uint OpenShopId { get; }

    /// <summary>Interact with a vendor and pick a shop; <c>0</c> takes the first one it offers.</summary>
    bool OpenShop(uint vendorDataId, uint shopId);

    /// <summary>Buy from an open shop. False when the item is not on its shelves.</summary>
    bool BuyFromShop(uint shopId, uint itemId, int count);

    /// <summary>A purchase is still going through.</summary>
    bool ShopBusy(uint shopId);

    void CloseShop();

    /// <summary>Gil on hand.</summary>
    int Gil { get; }

    /// <summary>
    /// How many of an item are held, both qualities. Also on <c>IConditionWorld</c>, which is what
    /// asks it about skip clauses; a PurchaseItem step needs it to know what is left to buy.
    /// </summary>
    int ItemCount(uint itemId);

    /// <summary>
    /// How many of an item are sitting in the Free Company chest, or 0 when none is or no page is
    /// readable.
    ///
    /// <para>
    /// Deliberately <b>not</b> part of <see cref="ItemCount"/>. Nothing in the chest can be handed
    /// over, crafted with or counted against a "skip if already held" clause — folding it in would
    /// have a purchase step skip itself and the hand-in it was buying for fail. It exists only so a
    /// stop can say where the missing item actually is.
    /// </para>
    /// </summary>
    int FreeCompanyChestCount(uint itemId);

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

    /// <summary>The entries of the icon-list dialog (quest offers) currently showing; empty when none.</summary>
    System.Collections.Generic.IReadOnlyList<string> SelectIconStringEntries();

    /// <summary>The quest's display name, for matching it against a hand-in menu. Null if unknown.</summary>
    string? QuestName(ushort questId);

    /// <summary>Picks an entry in the icon-list dialog if one is showing.</summary>
    void SelectIconStringIndex(int index);

    /// <summary>
    /// Press Complete on the quest reward window. Returns false when the window is not up or the
    /// button is disabled — which means an optional reward still needs choosing.
    /// </summary>
    bool CompleteQuestRewardWindow();

    // ── The Request window ──
    //
    // The NPC hand-over window: an interaction that wants items puts it up with a slot per item,
    // and the interaction cannot end until the slots are filled and Hand Over is pressed. Both are
    // things TextAdvance does when it is loaded and holding; these three are what let a run say
    // what is missing, and finish the hand-in, when it is not.

    /// <summary>What the hand-over window is asking for; empty when it is not up.</summary>
    System.Collections.Generic.IReadOnlyList<HandOverRequest> HandOverRequests { get; }

    /// <summary>The game's own answer to whether the bags can satisfy every slot.</summary>
    bool CanSatisfyHandOver { get; }

    /// <summary>
    /// Fill every slot from the bags and press Hand Over. False when the window is not up, a slot
    /// could not be filled, or the button is disabled.
    /// </summary>
    bool CompleteHandOverWindow();

    /// <summary>
    /// Answer the "do you really want to trade a high-quality item?" confirmation, whose Yes is
    /// greyed until its checkbox is ticked. Returns true when it acted — including the tick on its
    /// own, which takes one pass, with Yes on the next.
    ///
    /// <para>
    /// False when no such dialog is up. A plain yes/no with no checkbox is deliberately <i>not</i>
    /// touched here: this answers one specific question, and blanket-confirming whatever prompt
    /// happens to be on screen is how an automation agrees to something nobody asked it to.
    /// </para>
    /// </summary>
    bool ConfirmTradeDialog();

    /// <summary>Ask TextAdvance to drive dialogue for us / stop.</summary>
    void HoldDialogue();

    void ReleaseDialogue();

    void Log(string message);
}
