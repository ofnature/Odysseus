using System;
using System.Numerics;
using Odysseus.Services.Quest;
using Odysseus.Services.Travel;

namespace Odysseus.Services.Paths;

/// <summary>What the feed needs from the game each frame. The real world implements it; tests do not need it (the recorder is tested on observations directly).</summary>
public interface IRecorderWorld
{
    DateTime UtcNow { get; }
    uint TerritoryId { get; }
    Vector3 PlayerPosition { get; }
    bool IsOccupied { get; }
    bool InCombat { get; }
    bool InDuty { get; }
    bool IsCasting { get; }
    bool IsBetweenAreas { get; }
    uint CurrentDutyCfc { get; }
    uint? TargetDataId { get; }
    Vector3? TargetPosition { get; }
    bool TargetIsEnemy { get; }
}

/// <summary>
/// Turns raw game state into <see cref="RecorderObservation"/>s once per tick, adding the two
/// things that need memory: whether a zone change was a teleport (a cast happened just before the
/// loading screen) and which aetheryte we arrived beside.
/// </summary>
public sealed class RecorderFeed
{
    private static readonly TimeSpan CastToLoad = TimeSpan.FromSeconds(8);
    private const float ArrivalRadius = 40f;

    private readonly IRecorderWorld _world;
    private readonly IQuestStateReader _quests;
    private readonly AetheryteCatalog _aetherytes;
    private readonly DutyCatalog _duties;

    private DateTime _lastCast;
    private uint _lastTerritory;
    private bool _wasBetweenAreas;
    private bool _castBeforeLoad;

    public RecorderFeed(IRecorderWorld world, IQuestStateReader quests, AetheryteCatalog aetherytes, DutyCatalog duties)
    {
        _world = world;
        _quests = quests;
        _aetherytes = aetherytes;
        _duties = duties;
        _lastTerritory = world.TerritoryId;
    }

    public RecorderObservation Next(ushort questId)
    {
        var now = _world.UtcNow;
        if (_world.IsCasting)
            _lastCast = now;

        var between = _world.IsBetweenAreas;
        if (between && !_wasBetweenAreas)
            _castBeforeLoad = now - _lastCast < CastToLoad;
        _wasBetweenAreas = between;

        var territory = _world.TerritoryId;
        var arrivedByTeleport = false;
        string? arrival = null;
        if (territory != _lastTerritory && !between)
        {
            arrivedByTeleport = _castBeforeLoad;
            if (arrivedByTeleport && _aetherytes.NearestIn(territory, _world.PlayerPosition, ArrivalRadius) is { } id)
                arrival = _aetherytes.DataName(id);
            _lastTerritory = territory;
            _castBeforeLoad = false;
        }

        var cfc = _world.CurrentDutyCfc;
        var solo = cfc != 0 && _duties.Describe(cfc) is { PartySize: <= 1 };

        return new RecorderObservation(
            now, territory, _world.PlayerPosition, _world.IsOccupied, _world.InCombat, _world.InDuty,
            cfc == 0 ? null : cfc, _world.TargetDataId, _world.TargetPosition, _world.TargetIsEnemy,
            _quests.Read(questId), _quests.IsAccepted(questId), _quests.IsComplete(questId),
            arrivedByTeleport, arrival, solo);
    }
}
