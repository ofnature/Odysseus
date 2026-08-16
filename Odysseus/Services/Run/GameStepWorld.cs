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
public sealed unsafe class GameStepWorld : IStepWorld, IConditionWorld, Paths.IRecorderWorld
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
    private readonly Action<string> _log;

    public GameStepWorld(
        IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui,
        ITargetManager targets, IDataManager data, VnavIpc vnav, DaedalusIpc daedalus,
        TextAdvanceIpc textAdvance, LifestreamIpc lifestream, Travel.AetheryteCatalog aetherytes,
        TheseusIpc theseus, ChatCommandSender chat, DutyCatalog duties, IQuestStateReader quests, Action<string> log)
    {
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

    public bool NavmeshReady => _vnav.IsReady;

    public bool IsMoving => _vnav.IsBusy;

    public int PathWaypointCount => _vnav.WaypointCount;

    public bool MoveTo(Vector3 destination, bool fly) => _vnav.MoveTo(destination, fly);

    public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly) => _vnav.MoveCloseTo(destination, tolerance, fly);

    public void StopMoving() => _vnav.Stop();

    public bool IsMounted => _condition[ConditionFlag.Mounted];

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

    // ── Travel ──

    public uint? ResolveAetheryte(string name) => _aetherytes.Resolve(name);

    public uint? AetheryteTerritory(uint aetheryteId) => _aetherytes.TerritoryOf(aetheryteId);

    public bool Teleport(uint aetheryteId) => _lifestream.Teleport(aetheryteId);

    public bool AethernetTeleport(string destination) => _lifestream.AethernetTeleport(destination);

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
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (module == null) return result;
            var jobs = _data.GetExcelSheet<ClassJob>();
            for (var i = 0; i < 100; i++)
            {
                if (!module->IsValidGearset(i)) continue;
                var entry = module->GetGearset(i);
                if (entry == null) continue;
                var role = jobs.GetRowOrDefault(entry->ClassJob)?.Role ?? 0;
                if (role != 0) result.Add(i);
            }
        }
        catch (Exception ex)
        {
            _log($"Gearset scan failed: {ex.Message}");
        }
        return result;
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

    // ── World objects ──

    public bool IsDataIdSpawned(uint dataId) => NearestWithDataId(dataId) is not null;

    public float? DistanceToDataId(uint dataId)
    {
        var obj = NearestWithDataId(dataId);
        return obj is null ? null : Vector3.Distance(obj.Position, PlayerPosition);
    }

    public Vector3? PositionOfDataId(uint dataId) => NearestWithDataId(dataId)?.Position;

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
        return Interact(target); // interacting with a hostile is "engage"; Daedalus takes it from there
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

    public bool UseItem(uint itemId)
    {
        try
        {
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

    public void SelectStringIndex(int index) => FireAddonCallback("SelectString", index);

    public IReadOnlyList<string> SelectStringEntries()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("SelectString");
            if (addon.IsNull || !addon.IsVisible)
                return Array.Empty<string>();
            var select = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectString*)addon.Address;
            return ReadPopupMenu(&select->PopupMenu.PopupMenu);
        }
        catch (Exception ex)
        {
            _log($"SelectString read failed: {ex.Message}");
            return Array.Empty<string>();
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
                return false;
            var owner = button->AtkComponentBase.OwnerNode;
            if (owner == null)
                return false;
            var evt = owner->AtkResNode.AtkEventManager.Event;
            if (evt == null)
                return false;
            journal->AtkUnitBase.ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, null);
            return true;
        }
        catch (Exception ex)
        {
            _log($"JournalResult complete failed: {ex.Message}");
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
