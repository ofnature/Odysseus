using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Odysseus.Services.Run;

namespace Odysseus.Services.Quest;

/// <summary>What a caller is told about the quest it asked for.</summary>
public sealed class QuestStatusDto
{
    /// <summary>Contract version of this document; fields are only ever added.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Idle (nothing asked yet), Refused, Running, Done (the quest completed), Faulted, Stopped (short of done).</summary>
    public string State { get; set; } = "Idle";
    public uint QuestId { get; set; }
    public int Sequence { get; set; }
    public int Step { get; set; }
    /// <summary>The run's own status line — what the main window shows.</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>Why it faulted, or was refused or stopped short, when it was.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// The way in for another plugin that wants a quest done — Calypso first, for the Island Sanctuary
/// quests its progression needs (Seeking Sanctuary to reach the island, then the ones that open its
/// features).
///
/// <para>
/// One quest per request, run exactly as Start on the main window runs it, with Stop after armed:
/// the run ends at that quest's completion and never rolls on into the story, the priority list or
/// an aether current detour. The caller asks again for the next one.
/// </para>
///
/// <para>
/// <c>Start</c> returning true has to mean "it began": a refusal (Odysseus switched off, already
/// busy, no path for the quest, the quest already done) is a false with the reason in the log, never
/// a run that quietly does something else. Who started what is told apart from the player's own
/// runs, so a caller waiting on its quest is not fooled by a quest the player started meanwhile.
/// </para>
/// </summary>
public sealed class QuestGateway
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly QuestController _controller;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _somethingElseIsRunning;
    private readonly Func<ushort, bool> _hasPath;
    private readonly Func<ushort, bool> _isComplete;
    private readonly Func<ushort, bool> _canTakeNow;
    private readonly Action<string> _log;

    private ushort _questId;
    private string _state = "Idle";
    private string? _reason;

    /// <param name="canTakeNow">
    /// Accepted already, or offered now: nothing it needs first is left undone. A quest whose
    /// prerequisites are not met would only walk to an NPC with nothing to give.
    /// </param>
    public QuestGateway(QuestController controller, Func<bool> enabled, Func<bool> somethingElseIsRunning,
        Func<ushort, bool> hasPath, Func<ushort, bool> isComplete, Func<ushort, bool> canTakeNow, Action<string> log)
    {
        _controller = controller;
        _enabled = enabled;
        _somethingElseIsRunning = somethingElseIsRunning;
        _hasPath = hasPath;
        _isComplete = isComplete;
        _canTakeNow = canTakeNow;
        _log = log;
    }

    /// <summary>The game's quest ids are row ids from 65536 up; Odysseus counts from 0. Both are taken.</summary>
    private static ushort Short(uint questId) => (ushort)(questId >= 0x10000 ? questId - 0x10000 : questId);

    /// <summary>
    /// Whether Odysseus could run this quest now: not done, a path stored or workable from the
    /// journal data, and nothing it needs first left undone.
    /// </summary>
    public bool CanRun(uint questId) => WhyNot(Short(questId), busyToo: false) is null;

    /// <summary>Begin the quest. True means it began and <c>Odysseus.IsBusy</c> is already true.</summary>
    public bool Start(uint questId)
    {
        var id = Short(questId);
        if (WhyNot(id, busyToo: true) is { } refusal)
        {
            _log($"Asked over IPC for quest {id}: refused — {refusal}.");
            Settle(id, "Refused", refusal);
            return false;
        }

        if (!_controller.Start(id))
        {
            _log($"Asked over IPC for quest {id}: it did not start — {_controller.StatusLine}");
            Settle(id, "Refused", _controller.StatusLine);
            return false;
        }
        // After Start, which disarms it: this quest, then stop.
        _controller.StopAfterQuest = true;
        Settle(id, "Running", null);
        _log($"Asked over IPC for quest {id}: running it, then stopping.");
        return true;
    }

    private void Settle(ushort id, string state, string? reason)
    {
        _questId = id;
        _state = state;
        _reason = reason;
    }

    private string? WhyNot(ushort id, bool busyToo)
        => id == 0 ? "no quest was named"
            : !_enabled() ? "Odysseus is switched off"
            : busyToo && (_somethingElseIsRunning() || _controller.State.IsDriving()) ? "Odysseus is already busy"
            : _isComplete(id) ? $"quest {id} is already complete"
            : !_hasPath(id) ? $"there is no path for quest {id}, recorded or from the journal data"
            : !_canTakeNow(id) ? $"quest {id} is not offered yet — something it needs first is not done"
            : null;

    /// <summary>Stop the quest this gateway started. A run the player started is left alone.</summary>
    public void Stop()
    {
        Refresh();
        if (_state != "Running") return;
        _controller.Stop();
        Refresh();
    }

    /// <summary>True while the quest this gateway started is still being run.</summary>
    public bool IsRunning
    {
        get
        {
            Refresh();
            return _state == "Running";
        }
    }

    public string StatusJson()
    {
        Refresh();
        var mine = _state == "Running" && _controller.QuestId == _questId;
        return JsonSerializer.Serialize(new QuestStatusDto
        {
            State = _state,
            QuestId = _questId,
            Sequence = mine ? _controller.Sequence : 0,
            Step = mine ? _controller.StepIndex : 0,
            Status = mine || _state == "Faulted" ? _controller.StatusLine : string.Empty,
            Reason = _reason,
        }, Json);
    }

    /// <summary>How the asked-for run stands now, read off the controller and the quest itself.</summary>
    private void Refresh()
    {
        if (_state != "Running") return;
        if (_isComplete(_questId))
            _state = "Done";
        else if (_controller.State == RunState.Faulted && _controller.QuestId == _questId)
        {
            _state = "Faulted";
            _reason = _controller.StatusLine;
        }
        else if (!_controller.State.IsDriving() || _controller.QuestId != _questId)
        {
            _state = "Stopped";   // stopped by hand, or the player started something else
            _reason = _controller.StatusLine.Length > 0 ? _controller.StatusLine : "stopped before the quest was done";
        }
    }
}
