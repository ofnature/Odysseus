using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odysseus.Services.Gathering;

/// <summary>One line of a gathering request: an item and the bag count to reach.</summary>
public sealed class GatherRequestItem
{
    public uint ItemId { get; set; }

    /// <summary>A bag count to reach, the same meaning as <see cref="GatherListItem.TargetCount"/>.</summary>
    public int TargetCount { get; set; }

    /// <summary>"This many more", for callers that would rather not do the arithmetic. Optional.</summary>
    public int? Quantity { get; set; }
}

/// <summary>A whole request, as another plugin sends it.</summary>
public sealed class GatherRequest
{
    public string RequestedBy { get; set; } = string.Empty;
    public bool ReturnHome { get; set; }
    public List<GatherRequestItem> Items { get; set; } = [];
}

/// <summary>What a caller is told about a run in progress.</summary>
public sealed class GatherStatusDto
{
    public string State { get; set; } = nameof(GatherListRunState.Idle);
    public bool External { get; set; }
    public string Status { get; set; } = string.Empty;
    public uint CurrentItemId { get; set; }
    public int Remaining { get; set; }
    public List<GatherOutcomeDto> Outcomes { get; set; } = [];
}

public sealed class GatherOutcomeDto
{
    public uint ItemId { get; set; }
    public int Target { get; set; }
    public int Held { get; set; }
    public bool Reached { get; set; }
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// The way in for another plugin that wants something gathered — Hephaestus's material planner is
/// the first, turning the shortfalls it cannot buy or sub-craft into a list.
///
/// <para>
/// The list it runs is <b>temporary</b>: never saved, never shown among the player's named lists,
/// gone when the run ends. It cannot enable, disable or edit those lists. That is the whole
/// difference between this and the gather window — the engine underneath is the same
/// <see cref="GatherListRunner"/>, zone-ordered, one bad row not ending the run.
/// </para>
///
/// <para>
/// Validation is all-or-nothing on purpose. <c>Start</c> returning true has to mean "it began", so
/// a request with one item this character cannot gather is refused whole and says which — a run
/// that quietly dropped a row would leave the caller waiting for a material that was never coming.
/// </para>
/// </summary>
public sealed class GatherGateway
{
    /// <summary>
    /// Case-insensitive in, camelCase out — the shape the request document published, so a caller
    /// reading the status with a plain deserializer gets the names it was told to expect.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly GatherListRunner _runner;
    private readonly Func<uint, bool> _canGather;
    private readonly Func<uint, string> _whyNot;
    private readonly Func<uint, int> _held;
    private readonly Func<bool> _somethingElseIsRunning;
    private readonly Action<string> _log;

    public GatherGateway(
        GatherListRunner runner,
        Func<uint, bool> canGather,
        Func<uint, string> whyNot,
        Func<uint, int> held,
        Func<bool> somethingElseIsRunning,
        Action<string> log)
    {
        _runner = runner;
        _canGather = canGather;
        _whyNot = whyNot;
        _held = held;
        _somethingElseIsRunning = somethingElseIsRunning;
        _log = log;
    }

    /// <summary>True while a run this gateway started is going. Told apart from the player's own runs.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Who asked, while a run of theirs is going.</summary>
    public string RequestedBy { get; private set; } = string.Empty;

    /// <summary>Whether this character could gather the item unattended right now.</summary>
    public bool CanGather(uint itemId) => _canGather(itemId);

    /// <summary>
    /// Begin a temporary run. True means it began and <c>Odysseus.IsBusy</c> is already true.
    /// </summary>
    public bool Start(string json)
    {
        GatherRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<GatherRequest>(json, Json);
        }
        catch (Exception ex)
        {
            _log($"A gathering request could not be read: {ex.Message}");
            return false;
        }

        if (request is null || request.Items.Count == 0)
        {
            _log("A gathering request named no items.");
            return false;
        }

        var who = string.IsNullOrWhiteSpace(request.RequestedBy) ? "another plugin" : request.RequestedBy.Trim();

        if (_somethingElseIsRunning())
        {
            _log($"{who} asked for gathering while Odysseus is already busy — refused.");
            return false;
        }

        // Every row is checked before any of it starts.
        var items = new List<GatherListItem>();
        foreach (var line in request.Items)
        {
            if (line.ItemId == 0)
            {
                _log($"{who}'s gathering request has a row with no item — refused.");
                return false;
            }

            var target = line.TargetCount > 0 ? line.TargetCount : _held(line.ItemId) + (line.Quantity ?? 0);
            if (target <= 0)
            {
                _log($"{who} asked for item {line.ItemId} without saying how many — refused.");
                return false;
            }

            if (!_canGather(line.ItemId))
            {
                _log($"{who} asked for item {line.ItemId}, which Odysseus cannot gather: {_whyNot(line.ItemId)}. Refused.");
                return false;
            }

            items.Add(new GatherListItem { ItemId = line.ItemId, TargetCount = target });
        }

        var list = new GatherList { Name = $"requested by {who}", Enabled = true, Items = items };
        if (!_runner.Begin([list]))
        {
            _log($"{who}'s gathering request started nothing: {_runner.Status}");
            return false;
        }

        IsRunning = true;
        RequestedBy = who;
        _log($"Gathering {items.Count} item(s) for {who}.");
        return true;
    }

    /// <summary>
    /// Stop a run this gateway started. A run the player started by hand is left alone — the gate
    /// must not be a remote stop button for the gather window.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
            return;
        _runner.Stop();
        Finish();
    }

    /// <summary>Called every tick by whoever drives the runner, so the flag falls when the run ends.</summary>
    public void Tick()
    {
        if (IsRunning && _runner.State != GatherListRunState.Running)
            Finish();
    }

    public string StatusJson()
    {
        try
        {
            var dto = new GatherStatusDto
            {
                State = _runner.State.ToString(),
                External = IsRunning,
                Status = _runner.Status,
                CurrentItemId = _runner.CurrentItem ?? 0,
                Remaining = _runner.Remaining,
            };
            foreach (var outcome in _runner.Outcomes)
                dto.Outcomes.Add(new GatherOutcomeDto
                {
                    ItemId = outcome.ItemId,
                    Target = outcome.Target,
                    Held = outcome.Held,
                    Reached = outcome.Reached,
                    Note = outcome.Note,
                });
            return JsonSerializer.Serialize(dto, Json);
        }
        catch
        {
            return "{}";   // a status that cannot be built is not worth throwing into a caller's poll
        }
    }

    private void Finish()
    {
        if (RequestedBy.Length > 0)
            _log($"Gathering for {RequestedBy} ended: {_runner.Status}");
        IsRunning = false;
        RequestedBy = string.Empty;
    }
}
