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
public sealed unsafe class GameStepWorld : IStepWorld, IConditionWorld
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

    // ── UI ──

    public bool IsAddonVisible(string name)
    {
        var addon = _gameGui.GetAddonByName(name);
        return !addon.IsNull && addon.IsVisible;
    }

    public void SelectYesNo(bool yes) => FireAddonCallback("SelectYesno", yes ? 0 : 1);

    public void SelectStringIndex(int index) => FireAddonCallback("SelectString", index);

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
