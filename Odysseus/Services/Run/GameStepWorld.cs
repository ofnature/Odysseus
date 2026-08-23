using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Odysseus.Services.Ipc;
using Odysseus.Services.Quest;

namespace Odysseus.Services.Run;

/// <summary>
/// The real <see cref="IStepWorld"/> and <see cref="IConditionWorld"/> — a translation layer over
/// the game and the plugins Odysseus leans on. Deliberately decision-free: everything here is "do
/// the thing" or "report the fact", and all judgement lives in <see cref="StepExecutor"/> and
/// <see cref="QuestController"/> where it can be tested.
/// </summary>
public sealed unsafe class GameStepWorld : IStepWorld, IConditionWorld, IChocoboWorld, Paths.IRecorderWorld
{
    /// <summary>GeneralAction 9 — Mount Roulette (verified against the sheet 2026-08-15).</summary>
    private const uint MountRouletteGeneralAction = 9;

    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly ITargetManager _targets;
    private readonly IDataManager _data;
    private readonly VnavIpc _vnav;
    private readonly DaedalusIpc _daedalus;
    private readonly TextAdvanceIpc _textAdvance;
    private readonly LifestreamIpc _lifestream;
    private readonly Travel.AetheryteCatalog _aetherytes;
    private readonly TheseusIpc _theseus;
    private readonly ChatCommandSender _chat;
    private readonly DutyCatalog _duties;
    private readonly IQuestStateReader _quests;
    private readonly Deliveries.IShopWorld _shops;
    private readonly ItemMaking _making;
    private readonly Action<string> _log;

    public GameStepWorld(
        IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui,
        ITargetManager targets, IDataManager data, VnavIpc vnav, DaedalusIpc daedalus,
        TextAdvanceIpc textAdvance, LifestreamIpc lifestream, Travel.AetheryteCatalog aetherytes,
        TheseusIpc theseus, ChatCommandSender chat, DutyCatalog duties, IQuestStateReader quests,
        Deliveries.IShopWorld shops, ItemMaking making, Action<string> log)
    {
        _shops = shops;
        _making = making;
        _lifestream = lifestream;
        _aetherytes = aetherytes;
        _theseus = theseus;
        _chat = chat;
        _duties = duties;
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
        _gameGui = gameGui;
        _targets = targets;
        _data = data;
        _vnav = vnav;
        _daedalus = daedalus;
        _textAdvance = textAdvance;
        _quests = quests;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;

    public Vector3 PlayerPosition => _objectTable.LocalPlayer?.Position ?? Vector3.Zero;

    public uint TerritoryId => _clientState.TerritoryType;

    // ── Navigation ──

    // ── Pathing ──
    //
    // Everything that knows the pathing plugin is vnavmesh lives here and in VnavIpc; the engine
    // only ever sees IStepWorld. Ariadne replaces vnavmesh by adding its own IPC wrapper and
    // repointing these seven members — nothing in StepExecutor, QuestController or the runners
    // needs to change. The two that would need thought are IsMoving (we report "busy", which folds
    // pathfinding and following together) and PathWaypointCount, which the executor reads as
    // "zero after a pathfind means unreachable" — a different pathfinder may signal that
    // differently.

    public bool NavmeshReady => _vnav.IsReady;

    public Vector3? NearestReachablePoint(Vector3 near, float within) => _vnav.NearestReachablePoint(near, within, within);

    public bool RebuildNavmesh() => _vnav.Rebuild();

    public float NavmeshBuildProgress => _vnav.BuildProgress;

    public bool IsMoving => _vnav.IsBusy;

    public int PathWaypointCount => _vnav.WaypointCount;

    public bool MoveTo(Vector3 destination, bool fly) => _vnav.MoveTo(destination, fly);

    public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly) => _vnav.MoveCloseTo(destination, tolerance, fly);

    public bool MoveDirectTo(Vector3 destination, bool fly) => _vnav.MoveDirect(destination, fly);

    public void StopMoving() => _vnav.Stop();

    public bool IsMounted => _condition[ConditionFlag.Mounted];

    public bool IsInFlight => _condition[ConditionFlag.InFlight];

    public void Mount()
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager != null)
                manager->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction);
        }
        catch (Exception ex)
        {
            _log($"Mount failed: {ex.Message}");
        }
    }

    /// <summary>GeneralAction 23, "Dismount" (read off the sheet 2026-08-20).</summary>
    private const uint DismountGeneralAction = 23;

    public void Dismount()
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager != null)
                manager->UseAction(ActionType.GeneralAction, DismountGeneralAction);
        }
        catch (Exception ex)
        {
            _log($"Dismount failed: {ex.Message}");
        }
    }

    // ── Chocobo companion ──

    public float CompanionTimeLeft
    {
        get
        {
            try
            {
                var ui = UIState.Instance();
                return ui == null ? 0f : ui->Buddy.CompanionInfo.TimeLeft;
            }
            catch
            {
                return 0f;
            }
        }
    }

    /// <summary>
    /// The field, not a city and not a duty. <see cref="CanMountHere"/> carries the first half:
    /// <c>TerritoryType.Mount</c> is false for exactly the zones a companion is refused in.
    /// </summary>
    public bool CanSummonHere => CanMountHere && !InDuty;

    public bool CanFlyHere
    {
        get
        {
            try
            {
                var territory = _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(_clientState.TerritoryType);
                if (territory is not { } t)
                    return false;
                var set = t.AetherCurrentCompFlgSet.RowId;
                if (set == 0)
                    return false; // zone has no currents — no flying (ARR zones fly freely only after the MSQ unlock, handled by the game refusing the mount)
                var state = PlayerState.Instance();
                return state != null && state->IsAetherCurrentZoneComplete(set);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// A zone from the base game. <c>TerritoryType.ExVersion</c> is 0 for every one of them
    /// (checked 2026-08-20 across Thanalan, Coerthas, the Fringes, Lakeland and Urqopacha).
    /// </summary>
    public bool InBaseGameZone
    {
        get
        {
            try
            {
                return _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(_clientState.TerritoryType)?.ExVersion.RowId == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Straight off <c>TerritoryType.Mount</c> — false for every city zone.</summary>
    public bool CanMountHere
    {
        get
        {
            try
            {
                return _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(_clientState.TerritoryType)?.Mount ?? false;
            }
            catch
            {
                return false;
            }
        }
    }

    // ── Travel ──

    public uint? ResolveAetheryte(string name) => _aetherytes.Resolve(name);

    public uint? AetheryteTerritory(uint aetheryteId) => _aetherytes.TerritoryOf(aetheryteId);

    /// <summary>
    /// A teleport straight into the zone, else a hop across the city aethernet, else nothing.
    ///
    /// <para>
    /// Attunement is checked because an unattuned aetheryte is not a route, it is a refused
    /// teleport. When the unlock state cannot be read at all the first candidate is taken and
    /// Lifestream gets to be the one that says no — better than declaring a zone unreachable
    /// because of a bad read.
    /// </para>
    /// </summary>
    public TravelRoute? RouteTo(uint territoryId, Vector3? near)
    {
        if (Attuned(_aetherytes.InTerritory(territoryId, near)) is { } direct)
            return new TravelRoute(direct, null, territoryId);

        // No aetheryte in the zone. It may still be half a city, reachable only across the aethernet.
        if (_aetherytes.ShardIn(territoryId, near) is not { } shard)
            return null;

        // Already on that network — one hop and nothing else.
        if (_aetherytes.GroupOfTerritory(TerritoryId) == shard.Group)
            return new TravelRoute(null, shard.Name, territoryId);

        if (_aetherytes.HubOfGroup(shard.Group) is not { } hub || Attuned([hub]) is null)
            return null;
        return new TravelRoute(hub, shard.Name, _aetherytes.TerritoryOf(hub) ?? territoryId);
    }

    /// <summary>The first of these the character has attuned, or null when none is.</summary>
    private uint? Attuned(IReadOnlyList<uint> candidates)
    {
        if (candidates.Count == 0)
            return null;
        try
        {
            var state = UIState.Instance();
            if (state == null)
                return candidates[0];
            foreach (var id in candidates)
                if (state->IsAetheryteUnlocked(id))
                    return id;
            return null;
        }
        catch
        {
            return candidates[0];
        }
    }

    public bool Teleport(uint aetheryteId) => _lifestream.Teleport(aetheryteId);

    /// <summary>
    /// The path data spells destinations <c>"[Ul'dah] Goldsmiths' Guild"</c> — its own convention
    /// for saying which city — while Lifestream and the Aetheryte sheet both call the place
    /// <c>"Goldsmiths' Guild"</c>. Passing the bracketed form through matched nothing, so every
    /// aethernet hop was refused; the city is stripped here, at the one place that talks to
    /// Lifestream.
    /// </summary>
    public uint? AethernetTerritoryOf(string destination)
        => _aetherytes.StopNamed(destination)?.TerritoryId;

    private HashSet<uint>? _shardObjectIds;

    /// <summary>
    /// The EObj rows named "Aethernet shard". Their positions are not in any sheet — the
    /// <c>Aetheryte</c> rows carry no Level at all, for shards or for the city aetheryte — so the
    /// only truthful source is the object table, and these ids are how it is recognised there.
    /// </summary>
    private HashSet<uint> ShardObjectIds()
    {
        if (_shardObjectIds is not null) return _shardObjectIds;
        _shardObjectIds = [];
        try
        {
            var names = _data.GetExcelSheet<EObjName>();
            foreach (var eobj in _data.GetExcelSheet<EObj>())
                if (names.GetRowOrDefault(eobj.RowId)?.Singular.ExtractText() is { Length: > 0 } n
                    && n.Contains("aethernet", StringComparison.OrdinalIgnoreCase))
                    _shardObjectIds.Add(eobj.RowId);
        }
        catch (Exception ex)
        {
            _log($"Aethernet shard object ids unavailable: {ex.Message}");
        }
        return _shardObjectIds;
    }

    /// <summary>
    /// Where to stand to use the aethernet, read off what is actually loaded around you — a shard
    /// object, or the city aetheryte, whichever is nearer.
    /// </summary>
    public Vector3? NearestAethernetAccess(uint territoryId, Vector3 near)
    {
        try
        {
            var shards = ShardObjectIds();
            Vector3? best = null;
            var bestDistance = float.MaxValue;
            foreach (var obj in _objectTable)
            {
                var isAccess = obj.ObjectKind == ObjectKind.Aetheryte
                               || (obj.ObjectKind == ObjectKind.EventObj && shards.Contains(obj.BaseId));
                if (!isAccess) continue;
                var distance = Vector3.Distance(obj.Position, near);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = obj.Position;
            }
            return best;
        }
        catch (Exception ex)
        {
            _log($"Aethernet access lookup failed: {ex.Message}");
            return null;
        }
    }

    public bool AethernetTeleport(string destination, bool byNameOnly = false)
    {
        // By id when the sheet knows the destination — both sides then read the same row and there
        // is no spelling to disagree about. The name gate stays as the fallback.
        if (!byNameOnly && _aetherytes.StopNamed(destination) is { } stop && stop.PlaceNameId != 0)
        {
            _log($"Aethernet to {stop.Name} by id {stop.PlaceNameId}.");
            if (_lifestream.AethernetTeleportByPlaceName(stop.PlaceNameId))
                return true;
            _log($"Aethernet to {stop.Name} by id was refused; trying by name.");
        }
        var place = StripCity(destination);
        _log($"Aethernet to \"{place}\" by name.");
        return _lifestream.AethernetTeleport(place);
    }

    /// <summary>"[Ul'dah] Goldsmiths' Guild" → "Goldsmiths' Guild". Anything unbracketed is left alone.</summary>
    public static string StripCity(string destination)
    {
        var close = destination.IndexOf(']');
        return destination.StartsWith('[') && close > 0
            ? destination[(close + 1)..].Trim()
            : destination.Trim();
    }

    public bool AtAethernetShard => _lifestream.ActiveAetheryte != 0;

    public bool IsTravelBusy
        => _lifestream.IsBusy
           || _condition[ConditionFlag.BetweenAreas]
           || _condition[ConditionFlag.BetweenAreas51]
           || _condition[ConditionFlag.Casting];

    // ── Player state ──

    public int PlayerLevel => _objectTable.LocalPlayer?.Level ?? 0;

    /// <summary>ClassJob role 1–4 (tank, melee, ranged, healer); crafters and gatherers are role 0.</summary>
    public bool IsCombatJob => (_objectTable.LocalPlayer?.ClassJob.ValueNullable?.Role ?? 0) != 0;

    public bool EquipGearset(int gearsetId)
    {
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (module == null || !module->IsValidGearset(gearsetId)) return false;
            return module->EquipGearset(gearsetId, 0) >= 0;
        }
        catch (Exception ex)
        {
            _log($"EquipGearset {gearsetId} failed: {ex.Message}");
            return false;
        }
    }

    public IReadOnlyList<int> CombatGearsets()
    {
        var result = new List<int>();
        foreach (var set in Gearsets())
            if (set.Kind == JobKind.Combat)
                result.Add(set.Id);
        return result;
    }

    /// <summary>
    /// The 100 gearset slots, skipping the empty ones and the ones whose class the character has
    /// never levelled. Level comes from <c>ClassJobLevels</c> rather than the gearset, which only
    /// carries an item level — a SwitchClass step choosing between two combat gearsets wants the
    /// job actually played, and that is the class level.
    /// </summary>
    public IReadOnlyList<GearsetInfo> Gearsets()
    {
        var result = new List<GearsetInfo>();
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            var state = PlayerState.Instance();
            if (module == null) return result;
            var jobs = _data.GetExcelSheet<ClassJob>();
            for (var i = 0; i < 100; i++)
            {
                if (!module->IsValidGearset(i)) continue;
                var entry = module->GetGearset(i);
                if (entry == null || entry->ClassJob == 0) continue;
                if (jobs.GetRowOrDefault(entry->ClassJob) is not { } job) continue;

                var level = 0;
                if (state != null && job.ExpArrayIndex >= 0)
                    level = state->ClassJobLevels[job.ExpArrayIndex];
                if (level == 0) continue; // the class is not unlocked on this character

                result.Add(new GearsetInfo(i, entry->ClassJob, job.ClassJobParent.RowId, level, KindOf(job)));
            }
        }
        catch (Exception ex)
        {
            _log($"Gearset scan failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Crafters and gatherers both have role 0, so the role alone cannot separate them. ClassJob
    /// rows 8–15 are CRP..CUL and 16–18 are MIN/BTN/FSH — the same fixed block the delivery code
    /// reads craft types out of.
    /// </summary>
    private static JobKind KindOf(ClassJob job) => job.Role != 0
        ? JobKind.Combat
        : job.RowId switch
        {
            >= 8 and <= 15 => JobKind.Crafter,
            >= 16 and <= 18 => JobKind.Gatherer,
            _ => JobKind.Other,
        };

    public uint CurrentClassJob => _objectTable.LocalPlayer?.ClassJob.RowId ?? 0;

    public JobKind CurrentJobKind
    {
        get
        {
            try
            {
                var job = _objectTable.LocalPlayer?.ClassJob.ValueNullable;
                return job is { } j ? KindOf(j) : JobKind.Other;
            }
            catch
            {
                return JobKind.Other;
            }
        }
    }

    private Dictionary<string, uint>? _classJobsByName;

    /// <summary>
    /// The path data names classes as the game displays them ("Blue Mage", "Conjurer"). Both the
    /// name and the three-letter abbreviation are indexed, and spaces are dropped, so "BlueMage"
    /// resolves too — the upstream enum spells some of them without the space.
    /// </summary>
    public uint? ResolveClassJob(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_classJobsByName is null)
        {
            _classJobsByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in _data.GetExcelSheet<ClassJob>())
                {
                    if (row.RowId == 0) continue;
                    Index(row.Name.ExtractText(), row.RowId);
                    Index(row.NameEnglish.ExtractText(), row.RowId);
                    Index(row.Abbreviation.ExtractText(), row.RowId);
                }
            }
            catch (Exception ex)
            {
                _log($"ClassJob sheet unavailable: {ex.Message}");
            }
        }
        return _classJobsByName.TryGetValue(Key(name), out var id) ? id : null;

        void Index(string text, uint rowId)
        {
            if (text.Length > 0) _classJobsByName!.TryAdd(Key(text), rowId);
        }

        static string Key(string text) => text.Replace(" ", string.Empty).Trim();
    }

    // ── Equipment ──
    //
    // All of this is RaptureGearsetModule and InventoryManager, never the GearSetList window. The
    // window renders from the module, is virtualised (its rows are not separate nodes), and its
    // node ids move between patches — reading the module is both simpler and steadier.

    /// <summary>
    /// Where a piece of equipment goes. <c>EquipSlotCategory</c> rows 1–11 are the ordinary slots
    /// in order, 12 is a ring (either hand), 13 is a two-handed weapon (main hand) and 17 is a soul
    /// crystal. Anything else is not equipment.
    /// </summary>
    private static ushort[]? EquipSlotsFor(uint equipSlotCategory) => equipSlotCategory switch
    {
        >= 1 and <= 11 => [(ushort)(equipSlotCategory - 1)],
        12 => [11, 12],
        13 => [0],
        17 => [13],
        _ => null,
    };

    /// <summary>Bags and armoury, in the order worth searching.</summary>
    private static readonly InventoryType[] EquipSources =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    public bool IsEquipped(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.EquippedItems);
            if (container == null) return false;
            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item != null && item->ItemId == itemId) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A main hand whose <c>ClassJobCategory</c> names exactly one class. That category's name is
    /// the class abbreviation for single-class tools — "GSM" for a Chaser Hammer — so resolving it
    /// through the same lookup a SwitchClass step uses both identifies the class and rules out the
    /// broad categories ("All Classes", "Disciples of the Hand"), which resolve to nothing.
    /// </summary>
    public uint? EquipClassOf(uint itemId)
    {
        try
        {
            if (_data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is not { } row)
                return null;
            // 1 and 13 are the main-hand slots; a class comes from the weapon, never from gear.
            if (row.EquipSlotCategory.RowId is not (1 or 13))
                return null;
            var category = row.ClassJobCategory.ValueNullable?.Name.ExtractText();
            return category is { Length: > 0 } name ? ResolveClassJob(name) : null;
        }
        catch
        {
            return null;
        }
    }

    public bool EquipItem(uint itemId)
    {
        try
        {
            if (_data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is not { } row)
                return false;
            if (EquipSlotsFor(row.EquipSlotCategory.RowId) is not { } targets)
            {
                _log($"Item {itemId} is not a piece of equipment.");
                return false;
            }

            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            foreach (var source in EquipSources)
            {
                var container = manager->GetInventoryContainer(source);
                if (container == null || !container->IsLoaded) continue;
                for (ushort slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId != itemId) continue;
                    // The first target slot free, else the first — swapping out what is there.
                    var target = targets[0];
                    foreach (var candidate in targets)
                    {
                        var occupant = manager->GetInventorySlot(InventoryType.EquippedItems, candidate);
                        if (occupant == null || occupant->ItemId == 0) { target = candidate; break; }
                    }
                    manager->MoveItemSlot(source, slot, InventoryType.EquippedItems, target, true);
                    return true;
                }
            }

            _log($"Item {itemId} is not in the bags or the armoury.");
            return false;
        }
        catch (Exception ex)
        {
            _log($"Equipping item {itemId} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool CreateGearset()
    {
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            return module != null && module->CreateGearset() >= 0;
        }
        catch (Exception ex)
        {
            _log($"Creating a gearset failed: {ex.Message}");
            return false;
        }
    }

    public bool UpdateGearset()
    {
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (module == null) return false;
            var current = module->CurrentGearsetIndex;
            return current >= 0 && module->IsValidGearset(current) && module->UpdateGearset(current) >= 0;
        }
        catch (Exception ex)
        {
            _log($"Updating the gearset failed: {ex.Message}");
            return false;
        }
    }

    public uint? QuestStartClassJob(ushort questId)
    {
        try
        {
            var manager = QuestManager.Instance();
            if (manager == null) return null;
            var work = manager->GetQuestById(questId);
            return work == null || work->AcceptClassJob == 0 ? null : work->AcceptClassJob;
        }
        catch
        {
            return null;
        }
    }

    public bool InCombat => _condition[ConditionFlag.InCombat];

    public bool IsReady
        => _objectTable.LocalPlayer is not null
           && !_condition[ConditionFlag.BetweenAreas]
           && !_condition[ConditionFlag.BetweenAreas51]
           && !_condition[ConditionFlag.Occupied]
           && !_condition[ConditionFlag.OccupiedInCutSceneEvent]
           && !_condition[ConditionFlag.OccupiedInQuestEvent]
           && !_condition[ConditionFlag.Casting]
           && !_condition[ConditionFlag.Unconscious];

    public bool IsOccupied
        => _condition[ConditionFlag.Occupied]
           || _condition[ConditionFlag.OccupiedInCutSceneEvent]
           || _condition[ConditionFlag.OccupiedInQuestEvent]
           || _condition[ConditionFlag.OccupiedInEvent]
           || _condition[ConditionFlag.WatchingCutscene]
           || _condition[ConditionFlag.WatchingCutscene78];

    public bool IsDead => _objectTable.LocalPlayer?.IsDead ?? false;

    // ── IRecorderWorld ──

    public bool IsCasting => _condition[ConditionFlag.Casting];

    public bool IsBetweenAreas => _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];

    public uint CurrentDutyCfc
    {
        get
        {
            try
            {
                var main = FFXIVClientStructs.FFXIV.Client.Game.GameMain.Instance();
                return main == null ? 0u : (uint)Math.Max(0, (int)main->CurrentContentFinderConditionId);
            }
            catch
            {
                return 0;
            }
        }
    }

    public uint? TargetDataId => _targets.Target?.BaseId;

    public Vector3? TargetPosition => _targets.Target?.Position;

    public bool TargetIsEnemy => _targets.Target is { ObjectKind: ObjectKind.BattleNpc };

    // ── IConditionWorld ──

    public bool IsQuestComplete(ushort questId) => _quests.IsComplete(questId);

    public bool IsQuestAccepted(ushort questId) => _quests.IsAccepted(questId);

    /// <summary>
    /// Both qualities counted. A quest takes an HQ item as readily as an NQ one, so counting only
    /// NQ would have the engine remake something already sitting in the bag — which is the whole
    /// point of the condition that asks.
    /// </summary>
    public unsafe int ItemCount(uint itemId)
    {
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (manager == null) return 0;
            return manager->GetInventoryItemCount(itemId, isHq: false)
                   + manager->GetInventoryItemCount(itemId, isHq: true);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The five Free Company chest pages, in tab order.</summary>
    private static readonly InventoryType[] FreeCompanyPages =
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5,
    ];

    /// <summary>
    /// Counted by walking the pages, because <c>GetInventoryItemCount</c> does not reach them.
    ///
    /// <para>
    /// A page's container only holds anything once the game has sent it, which it does when that
    /// tab is first viewed — so this answers for the pages the character has looked at this
    /// session and reports zero for the rest. That is the whole of the FC chest that is knowable
    /// without opening it, and a zero here means "cannot say", never "definitely not there".
    /// </para>
    /// </summary>
    public int FreeCompanyChestCount(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return 0;

            var total = 0;
            foreach (var page in FreeCompanyPages)
            {
                var container = manager->GetInventoryContainer(page);
                if (container == null || !container->IsLoaded) continue;
                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item != null && item->ItemId == itemId)
                        total += (int)item->Quantity;
                }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    // ── World objects ──

    public bool IsDataIdSpawned(uint dataId) => NearestWithDataId(dataId) is not null;

    /// <summary>
    /// The nameplate icon ids the game uses for quest markers. 71343 is one, read off an NPC
    /// mid-quest (2026-08-19); the ids around it are the rest of the family — available, in
    /// progress, ready to turn in, and the same again for the main scenario. The whole 71xxx block
    /// is taken as "quest", which is why nothing is ever <i>skipped</i> on the strength of it.
    /// </summary>
    private const uint QuestMarkerFirst = 71000, QuestMarkerLast = 71999;

    public bool HasQuestMarker(uint dataId)
    {
        var obj = NearestWithDataId(dataId);
        if (obj is null)
            return false;
        var icon = ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address)->NamePlateIconId;
        return icon >= QuestMarkerFirst && icon <= QuestMarkerLast;
    }

    public float? DistanceToDataId(uint dataId)
    {
        var obj = NearestWithDataId(dataId);
        return obj is null ? null : Vector3.Distance(obj.Position, PlayerPosition);
    }

    public Vector3? PositionOfDataId(uint dataId) => NearestWithDataId(dataId)?.Position;

    /// <summary>
    /// Point the player at an object.
    ///
    /// <para>
    /// The game's own yaw convention: zero faces south (+Z) and it turns anticlockwise, which is
    /// <c>atan2(dx, dz)</c> rather than the usual <c>atan2(dz, dx)</c>.
    /// </para>
    /// </summary>
    public void FaceDataId(uint dataId)
    {
        try
        {
            var target = NearestWithDataId(dataId);
            var player = _objectTable.LocalPlayer;
            if (target is null || player is null)
                return;

            var delta = target.Position - player.Position;
            if (delta.LengthSquared() < 0.01f)
                return;

            ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address)
                ->SetRotation(MathF.Atan2(delta.X, delta.Z));
        }
        catch (Exception ex)
        {
            _log($"Facing {dataId} failed: {ex.Message}");
        }
    }

    public bool TryInteractWithDataId(uint dataId)
    {
        var target = NearestWithDataId(dataId);
        if (target is null)
            return false;
        SetTarget(target);
        return Interact(target);
    }

    public bool AttackNearestEnemy(IReadOnlyCollection<uint> dataIds, float radius)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return false;

        var target = _objectTable
            .Where(o => o.ObjectKind == ObjectKind.BattleNpc)
            .Where(o => dataIds.Count == 0 || dataIds.Contains(o.BaseId))
            .Where(IsAttackable)
            .Where(o => Vector3.Distance(o.Position, player.Position) <= radius)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();
        if (target is null)
            return false;

        SetTarget(target);

        // An overworld mob standing off at twenty yalms is not engaged by pressing interact at
        // it from here. Walk into reach first — hostiles usually close the rest themselves — and
        // report the approach as engagement so the caller keeps waiting rather than deciding
        // there was nothing to fight.
        if (Vector3.Distance(target.Position, player.Position) > StepExecutor.InteractReach)
        {
            if (!IsTravelBusy)
                _vnav.MoveTo(target.Position, false);
            return true;
        }

        Interact(target); // interacting with a hostile is "engage"; Daedalus takes it from there
        return true;
    }

    // ── Instances and handoffs ──

    public bool InDuty => _condition[ConditionFlag.BoundByDuty] || _condition[ConditionFlag.BoundByDuty56] || _condition[ConditionFlag.BoundByDuty95];

    /// <summary>
    /// BossMod has no IPC for this — the AI's follow/idle switch is reachable only from the
    /// <c>/bmrai</c> chat command (Theseus finding #3). One call site, so a rename is one fix.
    /// </summary>
    public void SetBossModAi(bool enabled) => _chat.Send($"/bmrai {(enabled ? "on" : "off")}");

    public DutyDescription? DescribeDuty(uint contentFinderConditionId) => _duties.Describe(contentFinderConditionId);

    public bool TheseusCanEnterDuty => _theseus.CanEnterDuty;

    public bool TheseusEnterDuty(uint contentFinderConditionId) => _theseus.EnterDuty(contentFinderConditionId);

    public bool TheseusBusy => _theseus.IsBusy;

    // ── Making things ──
    //
    // Straight through to the handoffs; every decision in them (which job's recipe, what is still
    // missing) lives in ItemMaking, and the waiting lives in the executor.

    public bool CrafterReady => _making.CrafterReady;

    public bool IsCrafting => _making.IsCrafting;

    public (uint ItemId, int Count)? NextCraft(uint itemId, int count) => _making.NextCraft(itemId, count);

    public string? StartCraft(uint itemId, int count) => _making.StartCraft(itemId, count);

    public void StopCrafting() => _making.StopCrafting();

    public IReadOnlyList<MaterialShortfall> CraftShortfall(uint itemId, int count)
        => _making.CraftShortfall(itemId, count);

    /// <summary>
    /// The first vendor the object table can actually see. Every candidate is considered, not just
    /// the first in the sheet: seven NPCs sell Copper Ore and the Goldsmiths' Guild one is sixth,
    /// so stopping at the first declined a sale from a merchant standing three paces away.
    /// </summary>
    public VendorOffer? VendorNearbyFor(uint itemId)
    {
        foreach (var vendor in _making.VendorsFor(itemId))
            if (IsDataIdSpawned(vendor.VendorDataId))
                return new VendorOffer(vendor.VendorDataId, vendor.ShopId, vendor.VendorName, vendor.Cost);
        return null;
    }

    public bool GathererReady => _making.GathererReady;

    public bool IsGathering => _making.IsGathering;

    public bool GathererIdle => _making.GathererIdle;

    public string GathererStatus => _making.GathererStatus;

    public bool StartGathering() => _making.StartGathering();

    public void StopGathering() => _making.StopGathering();

    // ── Actions ──

    public bool TryTargetDataId(uint dataId)
    {
        var target = NearestWithDataId(dataId);
        if (target is null)
            return false;
        SetTarget(target);
        return true;
    }

    public void SendChatCommand(string command) => _chat.Send(command);

    /// <summary>
    /// Use an item, on the current target where the item wants one.
    ///
    /// <para>
    /// Two different mechanisms behind one verb. An ordinary item is used out of the bags through
    /// the inventory agent. An <b>event item</b> — the quest key items, ids from 2,000,000 up, like
    /// the 2001288 that treats the survivors in "They Came from the Deep" — is not in the bags at
    /// all: it is an action, and putting it through the inventory agent silently does nothing,
    /// which is exactly how four steps of that quest ran in ten seconds and changed nothing.
    /// </para>
    /// </summary>
    public bool UseItem(uint itemId)
    {
        try
        {
            if (itemId >= EventItemBase)
            {
                var actions = ActionManager.Instance();
                if (actions == null)
                    return false;

                // The target explicitly rather than "whatever is targeted": the placeholder is
                // resolved by the game at a moment we do not control, and a targeted quest item
                // refused for having no target is indistinguishable from one refused for range.
                var target = _targets.Target?.GameObjectId ?? CurrentTarget;
                if (actions->UseAction(ActionType.EventItem, itemId, target))
                    return true;

                var status = actions->GetActionStatus(ActionType.EventItem, itemId, target, false, false);
                _log($"Event item {itemId} refused (status {status}{(status == OutOfRange ? " — out of range" : "")}).");
                return false;
            }

            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance();
            if (agent == null)
                return false;
            agent->UseItem(itemId, InventoryType.Invalid, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            _log($"UseItem {itemId} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Throw a ground-targeted quest item at a spot. <c>UseActionLocation</c> rather than
    /// <c>UseAction</c>: the item lands on the ground where aimed, which is what "throw the
    /// scalebomb at the suspicious object" is.
    /// </summary>
    public bool UseItemOnGround(uint itemId, Vector3 position)
    {
        try
        {
            var actions = ActionManager.Instance();
            if (actions == null)
                return false;
            var spot = position;
            if (actions->UseActionLocation(ActionType.EventItem, itemId, CurrentTarget, &spot))
                return true;
            _log($"Ground-targeted item {itemId} at {spot} was refused.");
            return false;
        }
        catch (Exception ex)
        {
            _log($"UseItemOnGround {itemId} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Event item ids start here; below it is an ordinary inventory item.</summary>
    private const uint EventItemBase = 2_000_000;

    /// <summary>The game's "whatever is targeted" placeholder, used only when nothing is targeted.</summary>
    private const ulong CurrentTarget = 0xE000_0000;

    /// <summary>The action-status code for "target is too far away".</summary>
    private const uint OutOfRange = 566;

    private Dictionary<string, uint>? _actionsByName;

    /// <summary>
    /// The path data names quest actions ("Big Sneeze", "Fiery Breath"); the Action sheet has
    /// them by name. Built once, case-insensitive; a name with several rows takes the lowest id
    /// that is not a PvP action.
    /// </summary>
    public uint? ResolveAction(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_actionsByName is null)
        {
            _actionsByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in _data.GetExcelSheet<Lumina.Excel.Sheets.Action>())
                {
                    var n = row.Name.ExtractText();
                    if (n.Length == 0 || row.IsPvP) continue;
                    _actionsByName.TryAdd(n, row.RowId);
                }
            }
            catch (Exception ex)
            {
                _log($"Action sheet unavailable: {ex.Message}");
            }
        }
        return _actionsByName.TryGetValue(name.Trim(), out var id) ? id : null;
    }

    public bool UseAction(uint actionId, Vector3? groundTarget)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager == null) return false;
            if (groundTarget is { } g)
                return manager->UseActionLocation(ActionType.Action, actionId, 0xE0000000, &g);
            var target = _targets.Target;
            return manager->UseAction(ActionType.Action, actionId, target?.GameObjectId ?? 0xE0000000);
        }
        catch (Exception ex)
        {
            _log($"UseAction {actionId} failed: {ex.Message}");
            return false;
        }
    }

    // ── Vendors ──
    //
    // Straight through to the delivery world's shop half. The handler code there is field-proven
    // and there is nothing quest-specific to add, so a PurchaseItem step buys through exactly the
    // same three calls the ingredient runs use.

    public bool IsShopOpen(uint shopId) => _shops.IsShopOpen(shopId);

    public uint OpenShopId => _shops.OpenShopId;

    public bool OpenShop(uint vendorDataId, uint shopId) => _shops.OpenShop(vendorDataId, shopId);

    public bool BuyFromShop(uint shopId, uint itemId, int count) => _shops.BuyFromShop(shopId, itemId, count);

    public bool ShopBusy(uint shopId) => _shops.ShopBusy(shopId);

    public void CloseShop() => _shops.CloseShop();

    public int Gil => _shops.Gil;

    public bool PrepareRecommendedGear()
    {
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RecommendEquipModule.Instance();
            var job = _objectTable.LocalPlayer?.ClassJob.RowId ?? 0;
            return module != null && job != 0 && module->SetupForClassJob((byte)job);
        }
        catch (Exception ex)
        {
            _log($"Recommended gear setup failed: {ex.Message}");
            return false;
        }
    }

    public bool RecommendedGearReady
    {
        get
        {
            try
            {
                var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RecommendEquipModule.Instance();
                return module != null && !module->IsUpdating;
            }
            catch
            {
                return false;
            }
        }
    }

    public void EquipRecommendedGear()
    {
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RecommendEquipModule.Instance();
            if (module != null)
                module->EquipRecommendedGear();
        }
        catch (Exception ex)
        {
            _log($"Equip recommended failed: {ex.Message}");
        }
    }

    // ── UI ──

    public bool IsAddonVisible(string name)
    {
        var addon = _gameGui.GetAddonByName(name);
        return !addon.IsNull && addon.IsVisible;
    }

    public void SelectYesNo(bool yes) => FireAddonCallback("SelectYesno", yes ? 0 : 1);

    public void SelectStringIndex(int index)
    {
        if (IsAddonVisible("SelectString"))
        {
            FireAddonCallback("SelectString", index);
            return;
        }
        FireAddonCallback(CutsceneChoice, index);
    }

    /// <summary>
    /// The options of whichever list dialogue is up.
    ///
    /// <para>
    /// Two windows ask the same question. <c>SelectString</c> is the plain menu; a choice put to you
    /// mid-conversation is <c>CutSceneSelectString</c>, which holds its options as AtkValues rather
    /// than in a PopupMenu — reading only the first left every in-conversation choice unanswered
    /// even though the path data named it.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SelectStringEntries()
    {
        try
        {
            var plain = _gameGui.GetAddonByName("SelectString");
            if (!plain.IsNull && plain.IsVisible)
            {
                var select = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectString*)plain.Address;
                return ReadPopupMenu(&select->PopupMenu.PopupMenu);
            }

            var cutscene = _gameGui.GetAddonByName(CutsceneChoice);
            if (!cutscene.IsNull && cutscene.IsVisible)
                return ReadCutsceneOptions((AtkUnitBase*)cutscene.Address);

            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _log($"List dialogue read failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>The in-conversation choice window.</summary>
    public const string CutsceneChoice = "CutSceneSelectString";

    /// <summary>
    /// Its options are the string AtkValues, in order. The prompt lives in the window's own text
    /// node rather than among them, so every string here is an option — and the index of one is the
    /// index the callback takes.
    /// </summary>
    private static IReadOnlyList<string> ReadCutsceneOptions(AtkUnitBase* addon)
    {
        var options = new List<string>();
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = addon->AtkValues[i];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString) || value.String.Value == null)
                continue;
            options.Add(Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue);
        }
        return options;
    }

    public string? QuestName(ushort questId)
    {
        try
        {
            var name = _data.GetExcelSheet<Lumina.Excel.Sheets.Quest>()
                .GetRowOrDefault(Quest.QuestCatalog.RowIdBase + questId)?.Name.ExtractText();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<string> SelectIconStringEntries()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("SelectIconString");
            if (addon.IsNull || !addon.IsVisible)
                return Array.Empty<string>();
            var select = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectIconString*)addon.Address;
            return ReadPopupMenu(&select->PopupMenu.PopupMenu);
        }
        catch (Exception ex)
        {
            _log($"SelectIconString read failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public void SelectIconStringIndex(int index) => FireAddonCallback("SelectIconString", index);

    private static IReadOnlyList<string> ReadPopupMenu(FFXIVClientStructs.FFXIV.Client.UI.PopupMenu* menu)
    {
        var count = Math.Clamp(menu->EntryCount, 0, 32);
        var entries = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var ptr = menu->EntryNames[i].Value;
            entries.Add(ptr == null ? string.Empty
                : Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue);
        }
        return entries;
    }

    /// <summary>
    /// Clicks the Complete button the way a mouse would: replay the button's own click event into
    /// the addon (the ECommons <c>ClickAddonButton</c> mechanism). No signature needed. Reward
    /// <i>selection</i> is a native call TextAdvance sig-scans for; that stays with TextAdvance.
    /// </summary>
    public bool CompleteQuestRewardWindow()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("JournalResult");
            if (addon.IsNull || !addon.IsVisible)
                return false;
            var journal = (FFXIVClientStructs.FFXIV.Client.UI.AddonJournalResult*)addon.Address;
            var button = journal->CompleteButton;
            if (button == null || !button->IsEnabled)
                return false;   // disabled means an optional reward still needs choosing
            return AtkClick.Button(&journal->AtkUnitBase, button);
        }
        catch (Exception ex)
        {
            _log($"JournalResult complete failed: {ex.Message}");
            return false;
        }
    }

    // ── The Request window ──
    //
    // The window is AddonRequest; what it is asking for lives in UIState's NpcTrade, and the
    // filling is AgentNpcTrade's — the same agent the delivery turn-in drives, because a delivery
    // turn-in *is* this window with a collectability rating attached.

    private const string HandOverWindow = "Request";

    public IReadOnlyList<HandOverRequest> HandOverRequests
    {
        get
        {
            try
            {
                if (!IsAddonVisible(HandOverWindow)) return Array.Empty<HandOverRequest>();
                var state = UIState.Instance();
                if (state == null) return Array.Empty<HandOverRequest>();
                var requests = state->NpcTrade.Requests;
                var list = new List<HandOverRequest>(requests.Count);
                for (var i = 0; i < requests.Count && i < requests.Items.Length; i++)
                {
                    var item = requests.Items[i];
                    if (item.ItemId == 0) continue;
                    var name = item.ItemName.ToString();
                    list.Add(new HandOverRequest(item.ItemId,
                        name.Length > 0 ? name : $"item {item.ItemId}",
                        Math.Max(1, item.RequiredQuantity)));
                }
                return list;
            }
            catch (Exception ex)
            {
                _log($"Hand-over window read failed: {ex.Message}");
                return Array.Empty<HandOverRequest>();
            }
        }
    }

    /// <summary>The game's own check, so a hand-in that cannot be met is named rather than waited on.</summary>
    public bool CanSatisfyHandOver
    {
        get
        {
            try
            {
                var state = UIState.Instance();
                return state != null && state->NpcTrade.CanSatisfyRequests();
            }
            catch
            {
                return true; // unreadable is not evidence of a shortfall — let the watchdog decide
            }
        }
    }

    /// <summary>
    /// Fill every slot then press Hand Over. Each slot is selected through the agent and takes its
    /// first offered item — the offers are already filtered to what that slot accepts, so "the
    /// first one" cannot be the wrong item, only the wrong copy of the right one.
    /// </summary>
    public bool CompleteHandOverWindow()
    {
        try
        {
            var addon = _gameGui.GetAddonByName(HandOverWindow);
            if (addon.IsNull || !addon.IsVisible)
                return false;
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentNpcTrade.Instance();
            if (agent == null || !agent->IsAgentActive())
                return false;

            var state = UIState.Instance();
            if (state == null)
                return false;

            // How many slots there are is the trade state's answer, not the addon's: the same field
            // the delivery turn-in reads, and the one that says what each slot will accept.
            var request = (FFXIVClientStructs.FFXIV.Client.UI.AddonRequest*)addon.Address;
            var slots = Math.Clamp((int)state->NpcTrade.Requests.Count, 0, 5);
            var result = default(AtkValue);
            Span<AtkValue> args = stackalloc AtkValue[4];
            for (var slot = 0; slot < slots; slot++)
            {
                if (agent->SelectedTurnInSlot >= 0)
                    return false; // a slot is mid-flight; come back next tick

                agent->SelectTurnInSlot((ushort)slot, 0, 0);
                if (agent->SelectedTurnInSlot != slot || agent->SelectedTurnInSlotItemOptions <= 0)
                    continue; // already filled, or it has nothing to offer for this one

                // Take the first offer. Same event the delivery turn-in uses to choose its item.
                args[0].SetInt(0);
                args[1].SetInt(0);
                args[2].SetInt(0);
                args[3].SetInt(0);
                fixed (AtkValue* p = args)
                    agent->ReceiveEvent(&result, p, 4, 1);
            }

            var button = request->HandOverButton;
            if (button == null || !button->IsEnabled)
                return false;
            return AtkClick.Button(&request->AtkUnitBase, button);
        }
        catch (Exception ex)
        {
            _log($"Hand-over failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The high-quality trade confirmation. Its Yes is greyed until "Proceed with trade" is
    /// ticked, so this ticks first and returns — the enable state settles a frame later — then
    /// presses Yes on the following pass, force-enabling it because that gate is UI-only.
    ///
    /// <para>
    /// Recognised by the checkbox, not by its text: a plain yes/no has no <c>ConfirmCheckBox</c>
    /// and is left alone, which keeps this from answering prompts it was never meant to see.
    /// </para>
    /// </summary>
    /// <summary>Addon sheet row 102434 — "Do you really want to trade a high-quality item?"</summary>
    private const uint HighQualityTradeRow = 102434;

    private string? _highQualityPrompt;

    public bool ConfirmTradeDialog()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("SelectYesno");
            if (addon.IsNull || !addon.IsVisible)
                return false;

            var yesno = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno*)addon.Address;
            var checkbox = yesno->ConfirmCheckBox;
            if (checkbox == null || checkbox->AtkComponentButton.AtkComponentBase.OwnerNode == null)
                return false; // an ordinary yes/no has no checkbox at all

            if (!IsHighQualityTrade(yesno))
                return false;

            if (!checkbox->IsChecked)
                return AtkClick.CheckBox(&yesno->AtkUnitBase, checkbox);

            AtkClick.ForceEnable(yesno->YesButton);
            return AtkClick.Button(&yesno->AtkUnitBase, yesno->YesButton);
        }
        catch (Exception ex)
        {
            _log($"Trade confirmation failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The prompt is matched against the game's own string for it, so this answers one question
    /// and no other. Both sides come from the Addon sheet, which keeps it language-independent.
    /// A sheet we cannot read falls back to the checkbox alone — still far narrower than any
    /// yes/no, and better than leaving a blocking dialog on screen.
    /// </summary>
    private bool IsHighQualityTrade(FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno* yesno)
    {
        _highQualityPrompt ??= _data.GetExcelSheet<Addon>().GetRowOrDefault(HighQualityTradeRow)?.Text.ExtractText()
                               ?? string.Empty;
        if (_highQualityPrompt.Length == 0)
            return true;
        if (yesno->PromptText == null)
            return false;
        var prompt = yesno->PromptText->NodeText.ToString();
        return prompt.Contains(_highQualityPrompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Addon sheet rows for the reward-overcap warnings — gil (4474), company seals (4605),
    /// tomestones (4606), seals and tomestones (4607), and "all the following:" (4609), the one a
    /// capped weekly turn-in raises. Read 2026-08-23. Matched by each row's first line, since the
    /// live prompt appends the list of what would be lost.
    /// </summary>
    private static readonly uint[] OvercapWarningRows = [4474, 4605, 4606, 4607, 4609];

    private string[]? _overcapPrompts;

    public bool ConfirmOvercapDialog()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("SelectYesno");
            if (addon.IsNull || !addon.IsVisible)
                return false;
            var yesno = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno*)addon.Address;
            if (yesno->PromptText == null)
                return false;

            _overcapPrompts ??= OvercapWarningRows
                .Select(row => _data.GetExcelSheet<Addon>().GetRowOrDefault(row)?.Text.ExtractText() ?? string.Empty)
                .Select(text => text.Split((char)10)[0].Trim())
                .Where(line => line.Length > 0)
                .ToArray();
            if (_overcapPrompts.Length == 0)
                return false; // no sheet, no guess — the dialog is left for the player

            var prompt = yesno->PromptText->NodeText.ToString();
            if (!_overcapPrompts.Any(line => prompt.Contains(line, StringComparison.OrdinalIgnoreCase)))
                return false;

            // The callback, not the button: this window comes in more than one skin — the field
            // dump of a live one showed no plain Button components at all, and the YesButton
            // click returned false forever, silently. The callback (0 = yes) is the one interface
            // every skin answers to, and it is how SelectYesNo already says yes elsewhere.
            SelectYesNo(true);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Overcap confirmation failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void HoldDialogue() => _textAdvance.Hold();

    public void ReleaseDialogue() => _textAdvance.Release();

    public void Log(string message) => _log(message);

    // ── Helpers ──

    private IGameObject? NearestWithDataId(uint dataId)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return null;
        return _objectTable
            .Where(o => o.BaseId == dataId && o.IsTargetable)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();
    }

    /// <summary>The only place Odysseus writes the hard target — the Daedalus claim lives with the write.</summary>
    private void SetTarget(IGameObject target)
    {
        _daedalus.RecordTargetWrite(target.GameObjectId);
        _targets.Target = target;
    }

    private bool Interact(IGameObject target)
    {
        try
        {
            var system = TargetSystem.Instance();
            if (system is null)
                return false;
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address;
            if (native is null)
                return false;
            system->InteractWithObject(native, false);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Interact failed: {ex.Message}");
            return false;
        }
    }

    private void FireAddonCallback(string addonName, int value)
    {
        try
        {
            var addon = _gameGui.GetAddonByName(addonName);
            if (addon.IsNull || !addon.IsVisible)
                return;
            ((AtkUnitBase*)addon.Address)->FireCallbackInt(value);
        }
        catch (Exception ex)
        {
            _log($"Dialog \"{addonName}\" callback failed: {ex.Message}");
        }
    }

    private static bool IsAttackable(IGameObject o)
    {
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)o.Address;
        return native is not null && native->GetIsTargetable() && !native->IsDead();
    }
}
