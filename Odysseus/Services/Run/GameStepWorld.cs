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
    private readonly Action<string>? _notify;

    public GameStepWorld(
        IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui,
        ITargetManager targets, IDataManager data, VnavIpc vnav, DaedalusIpc daedalus,
        TextAdvanceIpc textAdvance, LifestreamIpc lifestream, Travel.AetheryteCatalog aetherytes,
        TheseusIpc theseus, ChatCommandSender chat, DutyCatalog duties, IQuestStateReader quests,
        Deliveries.IShopWorld shops, ItemMaking making, Action<string> log, Action<string>? notify = null)
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
        _notify = notify;
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

    public bool IsPathfinding => _vnav.IsPathfinding;

    public bool IsMoving => _vnav.IsBusy;

    public int PathWaypointCount => _vnav.WaypointCount;

    public bool MoveTo(Vector3 destination, bool fly) => _vnav.MoveTo(destination, fly);

    public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly) => _vnav.MoveCloseTo(destination, tolerance, fly);

    public bool MoveDirectTo(Vector3 destination, bool fly) => _vnav.MoveDirect(destination, fly);

    public void StopMoving() => _vnav.Stop();

    public bool IsMounted => _condition[ConditionFlag.Mounted];

    public bool IsInFlight => _condition[ConditionFlag.InFlight];

    public bool IsRidingVehicle => _condition[ConditionFlag.RidingPillion];

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

    /// <summary>GeneralAction 12, "Materia Melding", learned with unlock link 11 (read off the sheet 2026-09-27).</summary>
    private const uint MeldingGeneralAction = 12;
    private const uint MeldingUnlockLink = 11;

    public bool MeldingUnlocked
    {
        get
        {
            try
            {
                var state = UIState.Instance();
                return state == null || state->IsUnlockLinkUnlocked(MeldingUnlockLink);
            }
            catch
            {
                return true; // unreadable is not evidence it is locked — the window not opening will say
            }
        }
    }

    public bool MeldingOpen => IsAddonVisible("MateriaAttach");

    public void OpenMelding()
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager != null)
                manager->UseAction(ActionType.GeneralAction, MeldingGeneralAction);
        }
        catch (Exception ex)
        {
            _log($"Opening Materia Melding failed: {ex.Message}");
        }
    }

    // What the window carries (/od values MateriaAttach, 2026-09-27): item names from value 147,
    // materia names from value 429, each list running until the first value that is not a string.
    // What its clicks send (/od record MateriaAttach): [1, item, 1, 0] selects an item,
    // [2, materia, 1, 0] selects a materia and opens the confirmation, [-1] closes. The
    // confirmation shows the materia at value 9 and the item at 16; [0, 0, 0] is Meld.
    private const int MeldItemNames = 147;
    private const int MeldMateriaNames = 429;
    private const int MeldListMax = 140;

    private List<string> MeldList(int start)
    {
        var names = new List<string>();
        var unit = (AtkUnitBase*)_gameGui.GetAddonByName("MateriaAttach").Address;
        if (unit == null || !unit->IsVisible) return names;
        for (var i = start; i < start + MeldListMax && i < unit->AtkValuesCount; i++)
        {
            var value = unit->AtkValues[i];
            if (value.String.Value == null || value.Type is AtkValueType.Int or AtkValueType.UInt or AtkValueType.Bool or AtkValueType.Undefined)
                break;
            var text = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue;
            if (text.Length == 0) break;
            names.Add(PlainName(text));
        }
        return names;
    }

    /// <summary>A list entry's name without the trailing space and HQ glyph the window adds.</summary>
    private static string PlainName(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetterOrDigit(text[end - 1]) && text[end - 1] != ')') end--;
        return text[..end].Trim();
    }

    private string ItemName(uint itemId) => _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? string.Empty;

    public int MeldItemIndex(uint itemId)
    {
        try
        {
            var wanted = ItemName(itemId);
            var names = MeldList(MeldItemNames);
            return wanted.Length == 0 ? -1 : names.FindIndex(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _log($"Reading the melding item list failed: {ex.Message}");
            return -1;
        }
    }

    public int MeldMateriaIndex(CraftNote.Meld meld)
    {
        try
        {
            return CraftNote.Pick(MeldList(MeldMateriaNames), meld);
        }
        catch (Exception ex)
        {
            _log($"Reading the melding materia list failed: {ex.Message}");
            return -1;
        }
    }

    public void MeldSelectItem(int index) => MeldCallback("MateriaAttach", 1, index, 1, 0);

    public void MeldSelectMateria(int index) => MeldCallback("MateriaAttach", 2, index, 1, 0);

    public bool? MeldDialogIsFor(uint itemId, CraftNote.Meld meld)
    {
        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName("MateriaAttachDialog").Address;
            if (unit == null || !unit->IsVisible || unit->AtkValuesCount <= 16) return null;
            string Read(int i) => unit->AtkValues[i].String.Value == null
                ? string.Empty
                : PlainName(Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)unit->AtkValues[i].String.Value).TextValue);
            var materia = Read(9);
            var item = Read(16);
            return string.Equals(item, ItemName(itemId), StringComparison.OrdinalIgnoreCase) && CraftNote.Fits(materia, meld);
        }
        catch (Exception ex)
        {
            _log($"Reading the melding confirmation failed: {ex.Message}");
            return false;
        }
    }

    public void ConfirmMeld() => MeldCallback("MateriaAttachDialog", 0, 0, 0);

    public void CloseMelding()
    {
        if (IsAddonVisible("MateriaAttach"))
            MeldCallback("MateriaAttach", -1);
    }

    private void MeldCallback(string addonName, params int[] values)
    {
        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
            if (unit == null || !unit->IsVisible) return;
            FireCallback(unit, true, values);   // the game's own clicks pass true here too
        }
        catch (Exception ex)
        {
            _log($"{addonName} callback failed: {ex.Message}");
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

    public uint? AttunableId(string name)
        => _aetherytes.Resolve(name) ?? _aetherytes.StopNamed(name)?.Id;

    public bool IsAttuned(uint aetheryteId) => Attuned([aetheryteId]) is not null;

    public (string Name, uint TerritoryId, Vector3 At)? AttunableAt(uint aetheryteId)
    {
        if (_aetherytes.AttunableById(aetheryteId) is not { } a)
            return null;
        return (a.Name, a.TerritoryId, Grounded(a.At, a.TerritoryId));
    }

    /// <summary>A map point at the height the mesh finds there, near the player's own height; the player's height where it finds none.</summary>
    private Vector3 Grounded(Vector2 at, uint territoryId)
    {
        var flat = new Vector3(at.X, PlayerPosition.Y, at.Y);
        return territoryId == TerritoryId ? NearestReachablePoint(flat, 60f) ?? flat : flat;
    }

    /// <summary>Attunes the game refused this session (story-locked).</summary>
    private readonly HashSet<uint> _refusedAttunes = [];

    /// <summary>
    /// The saved refusals (config), per character: an aetheryte refused while the story stood at the
    /// same scenario quest is skipped across sessions; once the story has moved it is tried again.
    /// </summary>
    public Dictionary<ulong, Dictionary<uint, ushort>>? SavedRefusals { get; set; }
    public System.Action? SaveRefusals { get; set; }

    private Dictionary<uint, ushort>? MyRefusals(bool create)
    {
        ulong who;
        try
        {
            var state = PlayerState.Instance();
            who = state == null ? 0 : state->ContentId;
        }
        catch
        {
            who = 0;
        }
        if (SavedRefusals is null || who == 0)
            return null;
        if (!SavedRefusals.TryGetValue(who, out var mine) && create)
            SavedRefusals[who] = mine = [];
        return mine;
    }

    public void AttuneRefused(uint aetheryteId)
    {
        _refusedAttunes.Add(aetheryteId);
        if (MyRefusals(create: true) is { } mine)
        {
            mine[aetheryteId] = _quests.CurrentScenarioQuest() ?? 0;
            SaveRefusals?.Invoke();
        }
    }

    private bool IsRefused(uint aetheryteId)
        => _refusedAttunes.Contains(aetheryteId)
           || (MyRefusals(create: false) is { } mine && mine.TryGetValue(aetheryteId, out var at)
               && at == (_quests.CurrentScenarioQuest() ?? 0));

    /// <summary>The game's "special permission is required to use this aetheryte", when last seen in chat.</summary>
    public DateTime LastAttuneRefusal { get; private set; }

    /// <summary>Fed every chat line by the plugin; only the aetheryte refusal is kept.</summary>
    public void NoteChat(string text)
    {
        if (text.Contains("special permission is required to use this aetheryte", StringComparison.OrdinalIgnoreCase))
            LastAttuneRefusal = DateTime.UtcNow;
    }

    public (uint Id, string Name, Vector3 At)? UnattunedNear(Vector3 near, float within)
    {
        try
        {
            (uint, string, Vector3)? best = null;
            var bestDistance = within;
            foreach (var a in _aetherytes.AttunablesIn(TerritoryId))
            {
                var d = Vector2.Distance(new Vector2(near.X, near.Z), a.At);
                if (d > bestDistance || IsAttuned(a.Id) || IsRefused(a.Id)) continue;
                bestDistance = d;
                best = (a.Id, a.Name, Grounded(a.At, a.TerritoryId));
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<(uint Id, string Name, bool IsShard)> UnattunedHere()
    {
        try
        {
            return _aetherytes.AttunablesIn(TerritoryId).Where(a => !IsAttuned(a.Id) && !IsRefused(a.Id))
                .Select(a => (a.Id, a.Name, a.IsShard)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private IGameObject? AttuneObjectNear(Vector3 near, float within, bool acrossGround = true)
    {
        var shards = ShardObjectIds();
        IGameObject? best = null;
        var bestDistance = within;
        foreach (var obj in _objectTable)
        {
            var isAccess = obj.ObjectKind == ObjectKind.Aetheryte
                           || (obj.ObjectKind == ObjectKind.EventObj && shards.Contains(obj.BaseId));
            if (!isAccess) continue;
            // Across the ground only: a map marker has no height, so the point asked about carries a
            // guessed one, and in a city of levels it can be the wrong level entirely — the
            // Crystarium's Cabinet of Curiosity shard sits ~57y below where it was guessed, and a 3D
            // test never found it from the level above.
            var d = acrossGround
                ? Vector2.Distance(new Vector2(obj.Position.X, obj.Position.Z), new Vector2(near.X, near.Z))
                : Vector3.Distance(obj.Position, near);
            if (d > bestDistance) continue;
            bestDistance = d;
            best = obj;
        }
        return best;
    }

    public Vector3? NearestAttuneObject(Vector3 near, float within)
    {
        try
        {
            return AttuneObjectNear(near, within)?.Position;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The aetheryte or shard itself, by its own id when it is loaded — an Aetheryte-kind object's
    /// BaseId is its Aetheryte row. Matching across the ground alone took Eulmore's Mainstay shard,
    /// directly beneath the city aetheryte, for the aetheryte, and walked back down to it.
    /// </summary>
    public Vector3? AttuneObjectFor(uint aetheryteId, Vector3 near, float within)
    {
        try
        {
            foreach (var obj in _objectTable)
                if (obj.ObjectKind == ObjectKind.Aetheryte && obj.BaseId == aetheryteId)
                    return obj.Position;
        }
        catch
        {
            // fall back to where the map puts it
        }
        return NearestAttuneObject(near, within);
    }

    public bool InteractAttuneObject(Vector3 at)
    {
        try
        {
            // The exact object at that spot: in 3D, so the shard a floor below is never the one pressed.
            if (AttuneObjectNear(at, 2f, acrossGround: false) is not { } obj) return false;
            SetTarget(obj);
            return Interact(obj);
        }
        catch (Exception ex)
        {
            _log($"Interacting with the aetheryte failed: {ex.Message}");
            return false;
        }
    }

    public void CloseTravelMenus()
    {
        foreach (var name in new[] { "TelepotTown", "SelectString", "Telepo" })
        {
            try
            {
                var addon = _gameGui.GetAddonByName(name);
                if (!addon.IsNull && addon.IsVisible)
                    ((AtkUnitBase*)addon.Address)->Close(true);
            }
            catch
            {
                // a menu that will not close is left for the player
            }
        }
    }

    public bool AethernetAttuned(string destination)
        => _aetherytes.StopNamed(destination) is not { } stop || IsAttuned(stop.Id);

    /// <summary>GeneralAction 4, "Sprint"; status 50 while it runs.</summary>
    private const uint SprintGeneralAction = 4;
    private const uint SprintStatus = 50;

    public bool Sprint()
    {
        try
        {
            if (_objectTable.LocalPlayer is not { } player) return false;
            foreach (var status in player.StatusList)
                if (status.StatusId == SprintStatus) return false;
            var manager = ActionManager.Instance();
            if (manager == null || manager->GetActionStatus(ActionType.GeneralAction, SprintGeneralAction) != 0)
                return false;
            return manager->UseAction(ActionType.GeneralAction, SprintGeneralAction);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A hop costs a cast and a loading screen: worth this much walking.</summary>
    private const float CityHopCost = 60f;
    private const float CityHopMin = 100f;

    public string? CityHop(uint territoryId, Vector3 from, Vector3 to)
    {
        try
        {
            var shards = _aetherytes.MappedShardsIn(territoryId);
            if (shards.Count < 2) return null;
            static float Flat(Vector3 a, Vector2 b) => Vector2.Distance(new Vector2(a.X, a.Z), b);
            var direct = Vector2.Distance(new Vector2(from.X, from.Z), new Vector2(to.X, to.Z));
            if (direct < CityHopMin) return null;
            var nearHere = shards.MinBy(s => Flat(from, s.At));
            var nearGoal = shards.MinBy(s => Flat(to, s.At));
            if (nearHere.Id == nearGoal.Id || Attuned([nearGoal.Id]) is null) return null;
            var via = Flat(from, nearHere.At) + Flat(to, nearGoal.At) + CityHopCost;
            return via < direct * 0.75f ? nearGoal.Name : null;
        }
        catch
        {
            return null;
        }
    }

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

    public float ActionRecastSeconds(uint actionId)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager == null)
                return 0f;
            var left = manager->GetRecastTime(ActionType.Action, actionId)
                       - manager->GetRecastTimeElapsed(ActionType.Action, actionId);
            return left > 0.05f ? left : 0f;
        }
        catch (Exception ex)
        {
            _log($"Reading the recast of action {actionId} failed: {ex.Message}");
            return 0f;
        }
    }

    public int LowestGearConditionPercent
    {
        get
        {
            try
            {
                var manager = InventoryManager.Instance();
                var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.EquippedItems);
                if (container == null || !container->IsLoaded)
                    return 100;
                var lowest = 100;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;
                    var percent = (int)Math.Round(slot->Condition / 300.0);   // 30000 is pristine
                    if (percent < lowest)
                        lowest = percent;
                }
                return lowest;
            }
            catch (Exception ex)
            {
                _log($"Reading gear condition failed: {ex.Message}");
                return 100;
            }
        }
    }

    public int FreeBagSlots
    {
        get
        {
            try
            {
                var manager = InventoryManager.Instance();
                return manager == null ? 0 : (int)manager->GetEmptySlotsInBag();
            }
            catch
            {
                return 0;
            }
        }
    }

    public void OpenRepairWindow() => SendChatCommand("/generalaction Repair");

    /// <summary>
    /// Fire an addon's own callback with these values — the way every window here is driven, and
    /// the probe behind the Workbench's addon tools. False when the addon is not on screen.
    /// </summary>
    public bool FireAddonValues(string addonName, params int[] values)
    {
        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
            if (unit == null || !unit->IsVisible)
                return false;
            FireCallback(unit, true, values);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Callback to {addonName} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The open context menu's entries, in order. Shape read from ECommons' AddonMaster.ContextMenu
    /// (MIT, see NOTICE.md): the count is AtkValues[0], and entry i's text is AtkValues[i + 8].
    /// </summary>
    public IReadOnlyList<string> ContextMenuEntries()
    {
        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName("ContextMenu").Address;
            if (unit == null || !unit->IsVisible || unit->AtkValuesCount == 0)
                return [];
            var count = (int)unit->AtkValues[0].UInt;
            var entries = new List<string>(count);
            for (var i = 0; i < count && 8 + i < unit->AtkValuesCount; i++)
            {
                var value = unit->AtkValues[8 + i];
                entries.Add(value.String.Value == null
                    ? string.Empty
                    : Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue);
            }
            return entries;
        }
        catch (Exception ex)
        {
            _log($"Reading the context menu failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Every addon on screen right now, by name. What a callback opened is the question the
    /// bench asks after firing one, and the answer is rarely the window you fired at.
    /// </summary>
    public IReadOnlyList<string> VisibleAddonNames()
    {
        var names = new List<string>();
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null || stage->RaptureAtkUnitManager == null)
                return names;
            ref var units = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
            for (var i = 0; i < units.Count; i++)
            {
                var unit = units.Entries[i].Value;
                if (unit == null || !unit->IsVisible)
                    continue;
                var name = unit->NameString;
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
            names.Sort(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _log($"Listing addons failed: {ex.Message}");
        }
        return names;
    }

    /// <summary>
    /// What an addon's node is and whether anything is listening to it — the question behind
    /// "why does clicking this tile do nothing".
    /// </summary>
    /// <summary>
    /// Every AtkValue an addon is carrying, written to the log with its index and type.
    ///
    /// <para>
    /// A window's values are where its contents actually live — JournalResult keeps the optional
    /// quest rewards in its own, past index 80 — and no sheet or header says which index means
    /// what. Reading them off a real window is the only way to find out, and guessing a layout
    /// from another plugin's copy is how you ship a reader that is quietly wrong. Read-only: it
    /// fires nothing and presses nothing.
    /// </para>
    /// </summary>
    /// <summary>One AtkValue as the log shows it — shared by the values dump and the click recorder.</summary>
    internal static string DescribeValue(FFXIVClientStructs.FFXIV.Component.GUI.AtkValue value)
    {
        var type = value.Type;
        if (type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int)
            return $"Int {value.Int}";
        if (type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt)
            return $"UInt {value.UInt}";
        if (type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool)
            return $"Bool {value.Byte != 0}";
        if (type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Float)
            return $"Float {value.Float}";
        if (type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Undefined)
            return "Undefined";
        // Every string flavour the client uses — String, String8, ManagedString — reads the same
        // way, so they are told apart by having a pointer rather than by name.
        if (value.String.Value != null)
            return $"{type} \"{Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue}\"";
        return $"{type} (raw {value.UInt})";
    }

    public string DescribeAddonValues(string addonName)
    {
        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
            if (unit == null || !unit->IsVisible)
                return $"{addonName} is not on screen.";

            var count = unit->AtkValuesCount;
            _log($"── {addonName}: {count} AtkValues ──");
            var written = 0;
            for (var i = 0; i < count; i++)
            {
                var value = unit->AtkValues[i];
                if (value.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Undefined)
                    continue;   // empty slots are the bulk of any window and say nothing
                _log($"  #{i} {DescribeValue(value)}");
                written++;
            }
            return $"{addonName}: {written} of {count} values written to the log.";
        }
        catch (Exception ex)
        {
            return $"reading {addonName}'s values failed: {ex.Message}";
        }
    }

    public string DescribeAddonNode(string addonName, uint nodeId)
    {
        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
            if (unit == null || !unit->IsVisible)
                return $"{addonName} is not on screen.";
            var node = unit->GetNodeById(nodeId);
            if (node == null)
                return $"{addonName} has no node {nodeId}.";
            var registered = node->AtkEventManager.Event;
            var kind = (int)node->Type >= 1000 ? $"component {(int)node->Type}" : node->Type.ToString();
            if (registered != null)
                return $"node {nodeId}: {kind}, event {registered->State.EventType} param {registered->Param}.";

            // A tile that listens on nothing itself may still hold a node that does.
            var inside = InsideListeners(node);
            return inside.Count == 0
                ? $"node {nodeId}: {kind}, no registered event, and nothing inside listens either."
                : $"node {nodeId}: {kind}, silent itself — inside: {string.Join(", ", inside)}";
        }
        catch (Exception ex)
        {
            return $"reading node {nodeId} failed: {ex.Message}";
        }
    }

    /// <summary>The nodes inside a component that carry a registered event, described.</summary>
    private static List<string> InsideListeners(AtkResNode* node)
    {
        var found = new List<string>();
        var component = node->GetAsAtkComponentNode();
        if (component == null || component->Component == null)
            return found;
        ref var uld = ref component->Component->UldManager;
        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var child = uld.NodeList[i];
            if (child == null)
                continue;
            var evt = child->AtkEventManager.Event;
            if (evt != null)
                found.Add($"#{child->NodeId} {child->Type} event {evt->State.EventType} param {evt->Param}");
        }
        return found;
    }

    /// <summary>Click an addon's node by id, the way a button is clicked.</summary>
    public bool ClickAddonNode(string addonName, uint nodeId)
    {
        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
            if (unit == null || !unit->IsVisible)
                return false;
            var node = unit->GetNodeById(nodeId);
            if (node == null)
                return false;
            if (node->AtkEventManager.Event != null)
                return AtkClick.Raw(unit, node);

            // Silent tile: hand the click to the first node inside it that is listening.
            var component = node->GetAsAtkComponentNode();
            if (component == null || component->Component == null)
                return false;
            ref var uld = ref component->Component->UldManager;
            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var child = uld.NodeList[i];
                if (child != null && child->AtkEventManager.Event != null)
                    return AtkClick.Raw(unit, child);
            }
            return false;
        }
        catch (Exception ex)
        {
            _log($"Clicking node {nodeId} of {addonName} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Send a chosen event to an addon's node — or, when the node is a silent tile, to the first
    /// node inside it that listens. The sweep behind "which event opens the context menu".
    /// </summary>
    public bool SendAddonNodeEvent(string addonName, uint nodeId, int eventType, int param)
    {
        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
            if (unit == null || !unit->IsVisible)
                return false;
            var node = unit->GetNodeById(nodeId);
            if (node == null)
                return false;
            if (node->AtkEventManager.Event == null)
            {
                var component = node->GetAsAtkComponentNode();
                if (component != null && component->Component != null)
                {
                    ref var uld = ref component->Component->UldManager;
                    for (var i = 0; i < uld.NodeListCount; i++)
                    {
                        var child = uld.NodeList[i];
                        if (child != null && child->AtkEventManager.Event != null)
                            return AtkClick.Send(unit, child, eventType, param);
                    }
                }
            }
            return AtkClick.Send(unit, node, eventType, param);
        }
        catch (Exception ex)
        {
            _log($"Sending event {eventType} to node {nodeId} of {addonName} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Every event type the client knows, by name and number. Sweeping event numbers blind is how
    /// an afternoon goes; the enum says which one is worth sending.
    /// </summary>
    public IReadOnlyList<string> EventTypeNames()
    {
        var names = new List<string>();
        foreach (AtkEventType value in Enum.GetValues<AtkEventType>())
            names.Add($"{(int)value} {value}");
        names.Sort((a, b) => int.Parse(a.Split(' ')[0]).CompareTo(int.Parse(b.Split(' ')[0])));
        return names;
    }

    /// <summary>Pick an entry of the open context menu. ECommons' own move: values 0, index, 0.</summary>
    public bool SelectContextMenuIndex(int index) => FireAddonValues("ContextMenu", 0, index, 0);

    public bool PressRepairAll()
    {
        // This ClientStructs build lays out no button field for the repair window; the window's
        // own callback is the press — value 0 is Repair All, the way every self-repairing
        // plugin fires it.
        if (!IsAddonVisible("Repair"))
            return false;
        FireAddonCallback("Repair", 0);
        return true;
    }

    public void CloseRepairWindow()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("Repair");
            var unit = (AtkUnitBase*)addon.Address;
            if (unit != null && unit->IsVisible)
                unit->Close(true);
        }
        catch (Exception ex)
        {
            _log($"Closing the repair window failed: {ex.Message}");
        }
    }

    public uint? AetheryteTerritory(uint aetheryteId) => _aetherytes.TerritoryOf(aetheryteId);

    public Vector3? AetherytePosition(uint aetheryteId) => _aetherytes.PositionOf(aetheryteId);

    public uint? NearestAttunedAetheryte(uint territoryId, Vector3 near, float maxDistance)
    {
        foreach (var id in _aetherytes.InTerritory(territoryId, near))
        {
            if (_aetherytes.PositionOf(id) is not { } at || Vector3.Distance(at, near) > maxDistance)
                continue;
            if (Attuned([id]) is not null)
                return id;
        }
        return null;
    }

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
    /// <summary>The doors the path library walks through. Null means none are known.</summary>
    public Travel.Doorways? Doors { get; set; }

    public IReadOnlyList<Travel.Doorways.Door> DoorsInto(uint territoryId) => Doors?.Into(territoryId) ?? [];

    public IReadOnlyList<Travel.Doorways.Door> GatesIn(uint territoryId) => Doors?.GatesIn(territoryId) ?? [];

    /// <summary>The battlehorn assigner the command uses too. Null means no battlehorns are reached.</summary>
    public BattlehornAssigner? Battlehorn { get; set; }

    public string? BattlehornPet(int slot) => Battlehorn?.PetOn(slot);

    public string AssignBattlehorn(int slot, string pet) => Battlehorn?.Assign(slot, pet) ?? "Battlehorns are not available.";

    public bool BattlehornBusy => Battlehorn?.Busy ?? false;

    public string BattlehornMessage => Battlehorn?.LastMessage ?? string.Empty;

    /// <summary>Close a window if it is up.</summary>
    public void CloseAddon(string name)
    {
        try
        {
            var addon = _gameGui.GetAddonByName(name);
            if (!addon.IsNull && addon.IsVisible)
                ((AtkUnitBase*)addon.Address)->Close(true);
        }
        catch
        {
            // a window that will not close is left for the player
        }
    }

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

    public void RefreshAetheryteList()
    {
        try
        {
            var telepo = Telepo.Instance();
            if (telepo != null)
                telepo->UpdateAetheryteList();
        }
        catch (Exception ex)
        {
            _log($"Refreshing the aetheryte list failed: {ex.Message}");
        }
    }

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

    public (string Name, Vector3 At)? MappedAethernetAccess(uint territoryId, Vector3 near)
    {
        if (_aetherytes.MappedShardNear(territoryId, near) is not { } shard)
            return null;
        var flat = new Vector3(shard.At.X, near.Y, shard.At.Y);
        return (shard.Name, NearestReachablePoint(flat, 40f) ?? flat);
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

    /// <summary>
    /// The character's real level on the current job — never the synced one.
    ///
    /// <para>
    /// Dalamud's <c>LocalPlayer.Level</c> is the <i>effective</i> level, so inside synced content it
    /// reads the sync: a level-100 character in the Bozjan Southern Front reads 80, and every level
    /// gate then lies about them. Field-caught 2026-09-17, where "A Winter's Dream needs level 100;
    /// you are 80" was told to a level-100 character standing in Bozja. The unsynced levels live in
    /// PlayerState, indexed by the job's ExpArrayIndex; the synced reading is kept only as the
    /// fallback for when that cannot be read at all.
    /// </para>
    /// </summary>
    public int PlayerLevel
    {
        get
        {
            var player = _objectTable.LocalPlayer;
            if (player is null)
                return 0;
            try
            {
                var index = player.ClassJob.ValueNullable?.ExpArrayIndex ?? -1;
                var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
                if (index >= 0 && state != null && state->ClassJobLevels[index] is var real && real > 0)
                    return real;
            }
            catch
            {
                // A level we cannot read unsynced is still better read synced than not at all.
            }
            return player.Level;
        }
    }

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

    public bool DaedalusDisabledByUser => _daedalus.IsDisabledByUser();

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

    public bool IsDiving => _condition[ConditionFlag.Diving];

    public bool IsSwimming => _condition[ConditionFlag.Swimming];

    public bool IsJumping => _condition[ConditionFlag.Jumping] || _condition[ConditionFlag.Jumping61];

    /// <summary>
    /// The descent keypress, spread over frames the way the game expects: modifiers down, key
    /// down, a few quiet frames, then everything up. The keybind is read from the game's own
    /// input data (MOVE_DESCENT), so remaps are honoured. Ported from Questionable's Dive task
    /// (PunishXIV/Questionable, AGPL-3.0 — the licence this plugin shares; see NOTICE.md).
    /// </summary>
    private readonly Queue<(uint Type, nint Key)> _descentKeys = new();

    public void PressDescent()
    {
        try
        {
            if (_descentKeys.TryDequeue(out var message))
            {
                if (message.Type != 0)
                    SendMessage((nint)FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance()->hWnd,
                        message.Type, message.Key, nint.Zero);
                return;
            }

            var keybind = new FFXIVClientStructs.FFXIV.Client.System.Input.Keybind();
            var keyName = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromString("MOVE_DESCENT");
            FFXIVClientStructs.FFXIV.Client.UI.UIInputData.Instance()->GetKeybindByName(keyName, &keybind);

            var keys = DescentKeys(keybind.KeySettings[0]) ?? DescentKeys(keybind.KeySettings[1]);
            if (keys is null)
            {
                _log("No usable keybind for descending — bind Descend (MOVE_DESCENT) to something and retry.");
                return;
            }

            foreach (var key in keys)
            {
                _descentKeys.Enqueue((WmKeydown, key));
                _descentKeys.Enqueue((0, 0));
                _descentKeys.Enqueue((0, 0));
            }
            for (var i = 0; i < 5; i++)
                _descentKeys.Enqueue((0, 0));
            keys.Reverse();
            foreach (var key in keys)
                _descentKeys.Enqueue((WmKeyup, key));
        }
        catch (Exception ex)
        {
            _log($"Descent press failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void ReleaseDescent()
    {
        try
        {
            // Only the key-ups matter now: a key never pressed is released harmlessly, and the
            // quiet frames and any key-downs still waiting are simply dropped.
            var hWnd = (nint)FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance()->hWnd;
            while (_descentKeys.TryDequeue(out var message))
                if (message.Type == WmKeyup)
                    SendMessage(hWnd, message.Type, message.Key, nint.Zero);
        }
        catch (Exception ex)
        {
            _descentKeys.Clear();
            _log($"Descent release failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static System.Collections.Generic.List<nint>? DescentKeys(
        FFXIVClientStructs.FFXIV.Client.System.Input.KeySetting setting)
    {
        if ((byte)setting.Key == 0)
            return null;
        var keys = new System.Collections.Generic.List<nint>();
        if (setting.KeyModifier.HasFlag(FFXIVClientStructs.FFXIV.Client.System.Input.KeyModifierFlag.Ctrl)) keys.Add(0x11);
        if (setting.KeyModifier.HasFlag(FFXIVClientStructs.FFXIV.Client.System.Input.KeyModifierFlag.Shift)) keys.Add(0x10);
        if (setting.KeyModifier.HasFlag(FFXIVClientStructs.FFXIV.Client.System.Input.KeyModifierFlag.Alt)) keys.Add(0x12);
        keys.Add((byte)setting.Key);
        return keys;
    }

    private const uint WmKeydown = 0x100;
    private const uint WmKeyup = 0x101;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    public bool InCutscene
        => _condition[ConditionFlag.OccupiedInCutSceneEvent]
           || _condition[ConditionFlag.WatchingCutscene]
           || _condition[ConditionFlag.WatchingCutscene78];

    /// <summary>
    /// The subtitle box advances on a click, and this is that click: a fresh AtkEvent aimed at
    /// the addon and a MouseDown/Click/Up triple. Ported from ECommons' AddonMaster.Talk.Click
    /// (MIT, NightmareXIV) — see NOTICE.md.
    /// </summary>
    public void AdvanceTalk()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("Talk");
            if (addon.IsNull || !addon.IsVisible)
                return;
            var unit = (AtkUnitBase*)addon.Address;
            var evt = new AtkEvent
            {
                Listener = (AtkEventListener*)unit,
                Target = &AtkStage.Instance()->AtkEventTarget,
                State = new() { StateFlags = (AtkEventStateFlags)132 },
            };
            var data = default(AtkEventData);
            unit->ReceiveEvent(AtkEventType.MouseDown, 0, &evt, &data);
            unit->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
            unit->ReceiveEvent(AtkEventType.MouseUp, 0, &evt, &data);
        }
        catch (Exception ex)
        {
            _log($"Talk advance failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

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
    /// <summary>
    /// The character's real level on any class, or 0 when that class is not unlocked. Unsynced, for
    /// the same reason <see cref="PlayerLevel"/> is: a gatherer standing in a synced duty has not
    /// forgotten how to mine.
    /// </summary>
    public int LevelOfJob(uint classJobId)
    {
        try
        {
            var index = _data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(classJobId)?.ExpArrayIndex ?? -1;
            var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            return index >= 0 && state != null ? state->ClassJobLevels[index] : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Which plugin fights a solo duty — set from the config, like the pathing provider.</summary>
    public string DutyAiProvider { get; set; } = Ipc.PluginPresence.BossModRebornProvider;

    /// <summary>The Minerva preset to hold the fight under, or empty to just switch its dodging on.</summary>
    public string MinervaPreset { get; set; } = string.Empty;

    /// <summary>The sniping-section skip (ported from CBT). Null means sniping is left to the player.</summary>
    public SnipeSkipper? Snipe { get; set; }

    public bool? AutoSnipeEnabled => Snipe?.Enabled;

    public void SetAutoSnipe(bool on) => Snipe?.Set(on);

    /// <summary>The Minerva handoff, when that is the chosen duty AI. Null changes nothing.</summary>
    public Ipc.MinervaIpc? Minerva { get; set; }

    /// <summary>
    /// Hand the fight to the duty AI, or take it back. A provider with no such switch — see
    /// <see cref="Ipc.PluginPresence.DutyAiCommand"/> — is left to its own settings.
    /// </summary>
    public void SetBossModAi(bool enabled)
    {
        if (Ipc.PluginPresence.DutyAiCommand(DutyAiProvider, enabled) is { } command)
        {
            _chat.Send(command);
            return;
        }

        // Minerva: claimed for the fight, handed back after. Its slot is the toggle.
        Minerva?.Drive(enabled, MinervaPreset);
    }

    public DutyDescription? DescribeDuty(uint contentFinderConditionId) => _duties.Describe(contentFinderConditionId);

    public bool TheseusCanEnterDuty => _theseus.CanEnterDuty;

    public bool TheseusEnterDuty(uint contentFinderConditionId) => _theseus.EnterDuty(contentFinderConditionId);

    public bool TheseusBusy => _theseus.IsBusy;

    // ── Making things ──
    //
    // Straight through to the handoffs; every decision in them (which job's recipe, what is still
    // missing) lives in ItemMaking, and the waiting lives in the executor.

    public bool CrafterReady => _making.CrafterReady;

    public string CrafterName => _making.CrafterName;

    private HashSet<uint>? _craftable;

    public IReadOnlyList<(uint ItemId, int Count, bool HighQuality)> QuestHandInCrafts(ushort questId, string? note)
    {
        var entries = CraftNote.Read(note);
        var items = new List<(uint, int, bool)>();
        foreach (var id in HandInCraftIds(questId))
            items.Add(NoteEntry(id, entries) is { } e ? (id, e.Count, e.HighQuality) : (id, 1, false));
        return items;
    }

    public bool NoteWantsHighQuality(uint itemId, string? note)
        => NoteEntry(itemId, CraftNote.Read(note))?.HighQuality == true;

    public CraftNote.Meld? NoteWantsMeld(uint itemId, string? note)
        => NoteEntry(itemId, CraftNote.Read(note))?.Melded;

    public bool HoldsForCraft(uint itemId, int count, string? note)
    {
        var entry = NoteEntry(itemId, CraftNote.Read(note));
        var have = entry?.HighQuality == true ? ItemCountHq(itemId) : ItemCount(itemId);
        return have >= count && (entry?.Melded is not { } meld || HoldsMelded(itemId, meld));
    }

    /// <summary>
    /// A slot's materia is a Materia row and a grade; the row's item for that grade carries the name
    /// the note writes ("Savage Aim Materia III"). HQ copies share the base item id.
    /// </summary>
    public unsafe bool HoldsMelded(uint itemId, CraftNote.Meld meld)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return false;
            var materia = _data.GetExcelSheet<Materia>();
            foreach (var type in EquipSources)
            {
                var container = manager->GetInventoryContainer(type);
                if (container == null) continue;
                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId != itemId) continue;
                    var matching = 0;
                    for (var i = 0; i < item->Materia.Length; i++)
                    {
                        var row = item->Materia[i];
                        if (row == 0) continue;
                        var grade = item->MateriaGrades[i];
                        if (meld.Grade is { } wantedGrade && grade + 1 != wantedGrade) continue;
                        if (meld.Materia is { } wanted)
                        {
                            var name = materia.GetRowOrDefault(row) is { } m && grade < m.Item.Count
                                ? m.Item[grade].ValueNullable?.Name.ExtractText() ?? string.Empty
                                : string.Empty;
                            if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        matching++;
                    }
                    if (matching >= meld.Count) return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _log($"Reading the materia on item {itemId} failed: {ex.Message}");
            return true; // unreadable is not evidence it is missing — the hand-in will say if it is
        }
    }

    private CraftNote.Entry? NoteEntry(uint itemId, IReadOnlyDictionary<string, CraftNote.Entry> entries)
    {
        var name = _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? string.Empty;
        return name.Length > 0 && entries.TryGetValue(name, out var e) ? e : null;
    }

    public unsafe int ItemCountHq(uint itemId)
    {
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            return manager == null ? 0 : manager->GetInventoryItemCount(itemId, isHq: true);
        }
        catch
        {
            return 0;
        }
    }

    private IReadOnlyList<uint> HandInCraftIds(ushort questId)
    {
        try
        {
            _craftable ??= _data.GetExcelSheet<Recipe>()
                .Where(r => r.ItemResult.RowId != 0)
                .Select(r => r.ItemResult.RowId)
                .ToHashSet();
            if (_data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(65536u + questId) is not { } quest)
                return [];
            var items = new List<uint>();
            foreach (var param in quest.QuestParams)
                if (param.ScriptInstruction.ExtractText().StartsWith("RITEM", StringComparison.Ordinal)
                    && _craftable.Contains(param.ScriptArg) && !items.Contains(param.ScriptArg))
                    items.Add(param.ScriptArg);
            return items;
        }
        catch (Exception ex)
        {
            _log($"Reading quest {questId}'s hand-in items failed: {ex.Message}");
            return [];
        }
    }

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

    /// <summary>
    /// What the yes/no window is asking, read the same way the overcap check reads it. Empty when
    /// no window is up, when it has no prompt node, or when reading it throws — a question we
    /// cannot quote is still a question we can report.
    /// </summary>
    public Quest.QuestSnapshot QuestState(ushort questId) => _quests.Read(questId);

    /// <summary>
    /// The target's health, whether it is down on one knee, and its statuses. The knee is
    /// <c>ActorControlFlags &amp; 0x40</c> — the same bit Questionable reads for "Incapacitated".
    /// </summary>
    public CombatTargetReading? CombatTarget(IReadOnlyCollection<uint> dataIds)
    {
        try
        {
            if (_targets.Target is not IBattleNpc npc || npc.IsDead)
                return null;
            if (dataIds.Count > 0 && !dataIds.Contains(npc.BaseId))
                return null;
            var health = npc.MaxHp == 0 ? 100f : 100f * npc.CurrentHp / npc.MaxHp;
            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)npc.Address;
            var incapacitated = chara != null && ((byte)chara->ActorControlFlags & 0x40) != 0;
            var statuses = new List<uint>();
            foreach (var status in npc.StatusList)
                if (status.StatusId != 0)
                    statuses.Add(status.StatusId);
            return new CombatTargetReading(npc.BaseId, health, incapacitated, statuses);
        }
        catch
        {
            return null;
        }
    }

    public bool HoldCombatActions(bool hold) => _daedalus.HoldActions(hold);

    public string YesNoPrompt()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("SelectYesno");
            if (addon.IsNull || !addon.IsVisible)
                return string.Empty;
            var yesno = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno*)addon.Address;
            var shown = yesno->PromptText == null ? string.Empty : yesno->PromptText->NodeText.ToString();
            if (shown.Length > 0)
                return shown;
            // Some skins of the window have no PromptText node, and a fresh one has not filled it
            // yet — "Overcap check: the window has no PromptText node" in every log. The question
            // the window was opened with is its first value either way.
            var unit = (AtkUnitBase*)addon.Address;
            if (unit->AtkValuesCount > 0 && unit->AtkValues[0].String.Value != null)
                return Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)unit->AtkValues[0].String.Value).TextValue;
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private Dictionary<uint, List<string>>? _travelPrompts;

    public IReadOnlyCollection<string> TravelPrompts(uint territoryId)
    {
        try
        {
            if (_travelPrompts is null)
            {
                var byTerritory = new Dictionary<uint, List<string>>();
                foreach (var warp in _data.GetExcelSheet<Warp>())
                {
                    var question = warp.Question.ExtractText();
                    if (question.Length == 0)
                        continue;
                    if (!byTerritory.TryGetValue(warp.TerritoryType.RowId, out var list))
                        byTerritory[warp.TerritoryType.RowId] = list = [];
                    list.Add(question);
                }
                // A ride that is no warp asks plainly: "Travel to Kholusia?" (the Crystarium's
                // aspiring amaro tamer). The zone's own name in that wording is its question too.
                foreach (var territory in _data.GetExcelSheet<TerritoryType>())
                {
                    var place = territory.PlaceName.ValueNullable?.Name.ExtractText();
                    if (string.IsNullOrEmpty(place))
                        continue;
                    if (!byTerritory.TryGetValue(territory.RowId, out var list))
                        byTerritory[territory.RowId] = list = [];
                    list.Add($"Travel to {place}?");
                }
                _travelPrompts = byTerritory;
            }
            return _travelPrompts.TryGetValue(territoryId, out var prompts) ? prompts : [];
        }
        catch
        {
            return [];
        }
    }

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
    /// Its options are the string AtkValues, in order — but the FIRST string is the prompt, shown
    /// as the header inside the window, and the callback indexes only the options after it.
    /// Returning the prompt as entry zero made every answer off by one: Clutch and Kin's join
    /// choice resolved to the right text, then declined the clutch.
    /// </summary>
    private static IReadOnlyList<string> ReadCutsceneOptions(AtkUnitBase* addon)
    {
        var strings = new List<string>();
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = addon->AtkValues[i];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString) || value.String.Value == null)
                continue;
            strings.Add(Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue);
        }
        return strings.Count > 1 ? strings.GetRange(1, strings.Count - 1) : strings;
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
                    var name = item.ItemName.StringPtr.Value == null
                        ? string.Empty
                        : Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)item.ItemName.StringPtr.Value).TextValue;
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
    /// <summary>How many slots have had their picker opened this window; reset when it closes.</summary>
    private int _handOverSlotCursor;
    private bool _handOverWindowSeen;

    /// <summary>
    /// Fill every slot then press Hand Over, one beat per call: open a slot's item picker
    /// (callback 2, slot), pick the first offer from the ContextIconMenu it opens (callback 0,
    /// index, 1021003), and press the button once every slot has been fed. The callback pair is
    /// the community-proven mechanism (Taurenkey's AutoSelectTurnin; the agent-based fill this
    /// replaces sat silent for two minutes on Fresh Flesh's three fish).
    /// </summary>
    public bool CompleteHandOverWindow()
    {
        try
        {
            var addon = _gameGui.GetAddonByName(HandOverWindow);
            if (addon.IsNull || !addon.IsVisible)
            {
                _handOverSlotCursor = 0;
                _handOverWindowSeen = false;
                return false;
            }
            if (!_handOverWindowSeen)
            {
                _handOverWindowSeen = true;
                _handOverSlotCursor = 0;
            }

            // An item picker is open: take its first offer.
            var menu = _gameGui.GetAddonByName("ContextIconMenu");
            if (!menu.IsNull && menu.IsVisible)
            {
                FireCallback((AtkUnitBase*)menu.Address, false, 0, 0, 1021003, 0, 0);
                return false;
            }

            var request = (FFXIVClientStructs.FFXIV.Client.UI.AddonRequest*)addon.Address;
            if (_handOverSlotCursor < request->EntryCount)
            {
                FireCallback(&request->AtkUnitBase, false, 2, _handOverSlotCursor, 0, 0);
                _handOverSlotCursor++;
                return false;
            }

            var button = request->HandOverButton;
            if (button == null || !button->IsEnabled)
            {
                // A slot did not take (its picker never opened, or the pick failed): start over.
                _handOverSlotCursor = 0;
                return false;
            }
            return AtkClick.Button(&request->AtkUnitBase, button);
        }
        catch (Exception ex)
        {
            _log($"Hand-over failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Fire an addon callback with several values, the way list windows expect.</summary>
    private static void FireCallback(AtkUnitBase* unit, bool updateState, params int[] values)
    {
        var atk = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
            atk[i].SetInt(values[i]);
        unit->FireCallback((uint)values.Length, atk, updateState);
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

    private DateTime _overcapProbeLogged;

    private void ProbeLog(string message)
    {
        if (DateTime.UtcNow - _overcapProbeLogged < TimeSpan.FromSeconds(5)) return;
        _overcapProbeLogged = DateTime.UtcNow;
        _log(message);
    }

    /// <summary>Letters and digits only, lowercased — the part of a prompt no payload can vary.</summary>
    private static string Squash(string text)
        => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public bool ConfirmOvercapDialog()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("SelectYesno");
            if (addon.IsNull || !addon.IsVisible)
            {
                ProbeLog("Overcap check: SelectYesno addon not found/visible by name.");
                return false;
            }
            var yesno = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno*)addon.Address;
            if (yesno->PromptText == null)
            {
                ProbeLog("Overcap check: the window has no PromptText node — reading the text another way is the next move.");
                return false;
            }

            // The live window drops its line-break payloads outright while the sheet renders
            // them as control characters, so nothing about whitespace can be trusted on either
            // side. The sheet text is cut at its first control character — the first sentence is
            // the stable part — and both sides are squashed to bare letters and digits before
            // comparing.
            _overcapPrompts ??= OvercapWarningRows
                .Select(row => _data.GetExcelSheet<Addon>().GetRowOrDefault(row)?.Text.ExtractText() ?? string.Empty)
                .Select(text => Squash(new string(text.TakeWhile(c => !char.IsControl(c)).ToArray())))
                .Where(line => line.Length > 0)
                .ToArray();
            if (_overcapPrompts.Length == 0)
                return false; // no sheet, no guess — the dialog is left for the player

            var prompt = Squash(yesno->PromptText->NodeText.ToString());
            if (!_overcapPrompts.Any(line => prompt.Contains(line, StringComparison.Ordinal)))
            {
                ProbeLog($"Overcap check: prompt did not match. prompt=[{prompt}] vs [{string.Join(" | ", _overcapPrompts)}]");
                return false;
            }
            _log("Overcap check: prompt matched — answering yes by callback.");

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

    /// <summary>The Accept button's node id in JournalAccept — the same id TextAdvance presses (ExecQuestAccept, reference/TextAdvance).</summary>
    private const uint JournalAcceptButtonNode = 44;

    public bool AcceptOfferedQuest()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("JournalAccept");
            if (addon.IsNull || !addon.IsVisible)
                return false;
            var unit = (AtkUnitBase*)addon.Address;
            var button = unit->GetComponentButtonById(JournalAcceptButtonNode);
            if (button == null || !button->IsEnabled)
                return false; // disabled Accept = "not yet available"; pressing it does nothing
            return AtkClick.Button(unit, button);
        }
        catch (Exception ex)
        {
            _log($"Quest accept press failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void HoldDialogue() => _textAdvance.Hold();

    public void ReleaseDialogue() => _textAdvance.Release();

    public void Log(string message) => _log(message);

    public void Notify(string message)
    {
        _log(message);
        _notify?.Invoke(message);
    }

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
