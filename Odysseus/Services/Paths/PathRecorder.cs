using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Odysseus.Services.Quest;

namespace Odysseus.Services.Paths;

/// <summary>One tick's worth of facts the recorder watches. Built by the game world each frame; plain data so the recorder is testable.</summary>
public readonly record struct RecorderObservation(
    DateTime Now,
    uint TerritoryId,
    Vector3 PlayerPosition,
    bool IsOccupied,
    bool InCombat,
    bool InDuty,
    uint? CurrentDutyCfc,
    uint? TargetDataId,
    Vector3? TargetPosition,
    bool TargetIsEnemy,
    QuestSnapshot Quest,
    bool IsAccepted,
    bool IsComplete,
    /// <summary>The territory just changed and the change was preceded by a teleport cast, not a walk.</summary>
    bool ArrivedByTeleport,
    /// <summary>Data-spelled name of the aetheryte we arrived beside, when <see cref="ArrivedByTeleport"/>.</summary>
    string? ArrivalAetheryte,
    /// <summary>The duty just entered is a solo instance (party size 1) rather than a dungeon or trial.</summary>
    bool DutyIsSolo);

/// <summary>
/// Builds a <see cref="QuestPath"/> from play.
///
/// <para>
/// The recorder never asks what you meant; it watches what the game did. Talking to something
/// (the occupied flag rising with a target held) is an interact step at that target. The quest's
/// sequence moving closes the block. Its variables changing while a step was pending become that
/// step's completion mask — the same landmarks the Wake resumes on. A teleport arrival becomes
/// the aetheryte shortcut on whatever you do next; a zone line crossed on foot becomes a
/// <c>WalkTo</c> with a target territory. Combat is a step from the first pull to the last kill,
/// with every enemy you targeted in its kill list. Entering an instance is a duty step. Walk-to
/// waypoints are the one thing it cannot infer — those are a button.
/// </para>
///
/// <para>
/// What comes out is ours: recorded on the user's machine from the game itself, owing nothing to
/// any bundle. This is how new content gets a path on the day it ships.
/// </para>
/// </summary>
public sealed class PathRecorder
{
    private static readonly TimeSpan CombatSettle = TimeSpan.FromSeconds(3);

    private QuestPath? _path;
    private QuestSequence? _block;
    private int _currentSequence = -1;

    private RecorderObservation? _last;
    private QuestStep? _pending;
    private byte[]? _varsBeforePending;
    private string? _pendingAetheryte;
    private QuestStep? _combatStep;
    private DateTime _combatLastSeen;
    private readonly HashSet<uint> _combatEnemies = [];
    private bool _dutyRecorded;

    public QuestPath? Path => _path;
    public bool IsRecording => _path is not null;
    public int StepCount => _path?.StepCount ?? 0;

    public event Action<QuestStep>? StepRecorded;
    public event Action<string>? Note;

    public void Begin(ushort questId, string name, string category = "Recorded")
    {
        _path = new QuestPath { QuestId = questId, Name = name, Category = category, Author = "Odysseus recorder", SourceHash = "recorded" };
        _block = null;
        _currentSequence = -1;
        _last = null;
        _pending = null;
        _pendingAetheryte = null;
        _combatStep = null;
        _combatEnemies.Clear();
        _dutyRecorded = false;
    }

    /// <summary>Stop and hand back what was recorded (null if nothing was begun).</summary>
    public QuestPath? Finish()
    {
        ClosePending(null);
        var path = _path;
        _path = null;
        _block = null;
        return path;
    }

    /// <summary>A waypoint at the player's position — the one step the recorder cannot infer.</summary>
    public void AddWalkToHere()
    {
        if (_last is not { } obs) return;
        Add(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", Position = obs.PlayerPosition, TerritoryId = obs.TerritoryId }, obs);
    }

    public void Observe(RecorderObservation obs)
    {
        if (_path is null)
        {
            _last = obs;
            return;
        }

        var sequence = obs.IsComplete ? 255 : obs.IsAccepted && obs.Quest.IsAvailable ? obs.Quest.Sequence : 0;
        if (sequence != _currentSequence)
        {
            // The pending step is what advanced us; it belongs to the old block, no mask needed.
            ClosePending(null);
            _currentSequence = sequence;
            _block = _path.Block((byte)sequence);
            if (_block is null)
            {
                _block = new QuestSequence { Sequence = (byte)sequence };
                _path.Sequences.Add(_block);
                _path.Sequences.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            }
            Note?.Invoke($"Sequence {sequence}");
        }

        if (_last is { } prev)
        {
            // Travel: teleport arrival tags the next step; a zone line on foot is a WalkTo out of the old zone.
            if (obs.TerritoryId != prev.TerritoryId)
            {
                if (obs.ArrivedByTeleport && obs.ArrivalAetheryte is { } aetheryte)
                {
                    _pendingAetheryte = aetheryte;
                    Note?.Invoke($"Teleported: {aetheryte}");
                }
                else if (!obs.InDuty && !prev.InDuty)
                {
                    Add(new QuestStep
                    {
                        Kind = StepKind.WalkTo, KindName = "WalkTo", Position = prev.PlayerPosition,
                        TerritoryId = prev.TerritoryId, TargetTerritoryId = obs.TerritoryId, Comment = "zone line",
                    }, prev);
                }
            }

            // Interact: occupied rises with a target held.
            if (obs.IsOccupied && !prev.IsOccupied && !obs.InDuty && prev.TargetDataId is { } dataId && !prev.TargetIsEnemy)
            {
                var kind = !obs.IsAccepted && !obs.IsComplete ? StepKind.AcceptQuest
                    : obs.Quest.IsReadyToComplete ? StepKind.CompleteQuest
                    : StepKind.Interact;
                var step = new QuestStep
                {
                    Kind = kind, KindName = kind.ToString(), DataId = dataId,
                    Position = prev.TargetPosition ?? prev.PlayerPosition, TerritoryId = prev.TerritoryId,
                };
                Add(step, prev);
                _pending = step;
                _varsBeforePending = obs.Quest.IsAvailable ? obs.Quest.Variables.ToArray() : null;
            }

            // Pending interact ends: mask = bits the variables gained meanwhile.
            if (_pending is not null && !obs.IsOccupied && prev.IsOccupied)
                ClosePending(obs.Quest);

            // Combat: from first pull to last kill.
            if (obs.InCombat)
            {
                if (_combatStep is null)
                {
                    _combatStep = new QuestStep
                    {
                        Kind = StepKind.Combat, KindName = "Combat", Position = obs.PlayerPosition, TerritoryId = obs.TerritoryId,
                        EnemySpawnType = EnemySpawnType.OverworldEnemies, KillEnemyDataIds = [],
                    };
                    _combatEnemies.Clear();
                    _varsBeforePending ??= obs.Quest.IsAvailable ? obs.Quest.Variables.ToArray() : null;
                    Add(_combatStep, obs);
                }
                _combatLastSeen = obs.Now;
                if (obs.TargetIsEnemy && obs.TargetDataId is { } enemy && _combatEnemies.Add(enemy))
                    _combatStep.KillEnemyDataIds!.Add(enemy);
            }
            else if (_combatStep is not null && obs.Now - _combatLastSeen > CombatSettle)
            {
                if (_combatEnemies.Count > 0 && _combatStep.KillEnemyDataIds!.Count > 0)
                    _combatStep.MinimumKillCount = _combatEnemies.Count;
                _pending = _combatStep;
                ClosePending(obs.Quest);
                _combatStep = null;
            }

            // Duty: entering an instance.
            if (obs.InDuty && !prev.InDuty && !_dutyRecorded && obs.CurrentDutyCfc is { } cfc)
            {
                var kind = obs.DutyIsSolo ? StepKind.SinglePlayerDuty : StepKind.Duty;
                Add(new QuestStep
                {
                    Kind = kind, KindName = kind.ToString(), TerritoryId = prev.TerritoryId, Position = prev.PlayerPosition,
                    ContentFinderConditionId = cfc, DutyEnabled = true,
                    DataId = kind == StepKind.SinglePlayerDuty ? prev.TargetDataId : null,
                }, prev);
                _dutyRecorded = true;
            }
            if (!obs.InDuty && prev.InDuty)
                _dutyRecorded = false;
        }

        _last = obs;
    }

    private void Add(QuestStep step, RecorderObservation at)
    {
        if (_block is null || _path is null) return;
        if (_pendingAetheryte is { } aetheryte)
        {
            step.AetheryteShortcut = aetheryte;
            _pendingAetheryte = null;
        }
        _block.Steps.Add(step);
        StepRecorded?.Invoke(step);
    }

    private void ClosePending(QuestSnapshot? after)
    {
        if (_pending is null) { _varsBeforePending = null; return; }
        if (after is { IsAvailable: true } snap && _varsBeforePending is { } before && snap.Variables.Length >= QuestSnapshot.VariableCount)
        {
            var mask = new byte?[QuestSnapshot.VariableCount];
            var any = false;
            var live = snap.Variables.Span;
            for (var i = 0; i < QuestSnapshot.VariableCount; i++)
            {
                var gained = (byte)(live[i] & ~before[i]);
                if (gained != 0) { mask[i] = gained; any = true; }
            }
            if (any)
            {
                _pending.CompletionQuestVariablesFlags = mask;
                _pending.Comment = $"{string.Join(' ', before)} -> {string.Join(' ', live.ToArray())}";
            }
        }
        _pending = null;
        _varsBeforePending = null;
    }
}
