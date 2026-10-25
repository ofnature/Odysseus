using System;
using System.Diagnostics;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Gathering;

/// <summary>
/// Gathering, by asking Demeter — the gatherer that used to live here, moved into a plugin of its
/// own on 2026-10-22 so Hephaestus and the gather lists can use it without Odysseus.
///
/// <para>
/// The <see cref="IOwnGatherer"/> seam is unchanged, so quest Gather steps and gathered custom
/// deliveries behave exactly as before: when Demeter is missing or switched off here,
/// <see cref="Enabled"/> reads false and they hand to GatherBuddy as they always did.
/// </para>
///
/// <para>
/// Demeter ticks its own gatherer, so <see cref="Tick"/> does nothing; the state is read from
/// <c>Demeter.Item.GetStatusJson</c>. That is read at most once a frame or so — the callers ask
/// Busy, Faulted and Status back to back — and re-read straight after a Start or Stop, because a
/// stale "not busy" read the moment after starting is a run that looks finished before it moved.
/// </para>
/// </summary>
public sealed class DemeterGatherer : IOwnGatherer
{
    private const string VersionGate = "Demeter.GetVersion";
    private const string CanGatherGate = "Demeter.Item.CanGather";
    private const string WhyNotGate = "Demeter.Item.WhyNot";
    private const string DescribeGate = "Demeter.Item.DescribeJson";
    private const string StartGate = "Demeter.Item.Start";
    private const string StopGate = "Demeter.Item.Stop";
    private const string StatusGate = "Demeter.Item.GetStatusJson";
    private const string ToggleListsGate = "Demeter.Ui.ToggleLists";

    private static readonly TimeSpan PresenceFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusFor = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan DescribeFor = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string> _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private ICallGateSubscriber<int>? _version;
    private ICallGateSubscriber<uint, uint, bool>? _canGather;
    private ICallGateSubscriber<uint, uint, string>? _whyNot;
    private ICallGateSubscriber<uint, string>? _describe;
    private ICallGateSubscriber<uint, int, int, uint, bool>? _start;
    private ICallGateSubscriber<object?>? _stop;
    private ICallGateSubscriber<string>? _status;
    private ICallGateSubscriber<object?>? _toggleLists;

    private bool _present;
    private TimeSpan _presentUntil = TimeSpan.MinValue;
    private ItemStatus _lastStatus = new();
    private TimeSpan _statusUntil = TimeSpan.MinValue;
    private (uint ItemId, Description Value, TimeSpan Until) _description;

    public DemeterGatherer(IDalamudPluginInterface pluginInterface, Action<string> log)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>Demeter is loaded and answers. Checked every few seconds, not every call.</summary>
    public bool Present
    {
        get
        {
            var now = _clock.Elapsed;
            if (now < _presentUntil)
                return _present;
            try
            {
                _present = (_version ??= _pluginInterface.GetIpcSubscriber<int>(VersionGate)).InvokeFunc() >= 1;
            }
            catch
            {
                _present = false;
            }
            _presentUntil = now + PresenceFor;
            return _present;
        }
    }

    private bool _enabled;

    /// <summary>"Gather with Demeter" is on here, and Demeter is there to ask.</summary>
    public bool Enabled
    {
        get => _enabled && Present;
        set => _enabled = value;
    }

    public bool CanGather(uint itemId, uint territoryHint = 0)
        => Enabled && Call(() => (_canGather ??= _pluginInterface.GetIpcSubscriber<uint, uint, bool>(CanGatherGate))
            .InvokeFunc(itemId, territoryHint), false);

    public string WhyNot(uint itemId, uint territoryHint = 0)
        => !Present ? "Demeter is not loaded"
            : !_enabled ? "gathering with Demeter is switched off in Odysseus's settings"
            : Call(() => (_whyNot ??= _pluginInterface.GetIpcSubscriber<uint, uint, string>(WhyNotGate))
                .InvokeFunc(itemId, territoryHint), "Demeter could not say");

    public uint? ZoneOf(uint itemId) => Describe(itemId).ZoneId;

    public string Where(uint itemId) => Describe(itemId).Where ?? string.Empty;

    public string Timer(uint itemId, DateTime utc) => Describe(itemId).Timer ?? string.Empty;

    public TimeSpan? WaitFor(uint itemId, DateTime utc)
        => Enabled && Describe(itemId).WaitSeconds is { } s ? TimeSpan.FromSeconds(s) : null;

    public TimeSpan? UpFor(uint itemId, DateTime utc)
        => Describe(itemId).UpForSeconds is { } s ? TimeSpan.FromSeconds(s) : null;

    public bool Start(uint itemId, int count, int collectability, uint territoryHint = 0)
    {
        if (!Enabled)
            return false;
        var started = Call(() => (_start ??= _pluginInterface.GetIpcSubscriber<uint, int, int, uint, bool>(StartGate))
            .InvokeFunc(itemId, count, collectability, territoryHint), false);
        _statusUntil = TimeSpan.MinValue;
        _log(started
            ? $"Asked Demeter for {count} × item {itemId} at {collectability}+."
            : $"Demeter refused {count} × item {itemId} — see Demeter's log.");
        return started;
    }

    /// <summary>Nothing to do: Demeter ticks its own gatherer.</summary>
    public void Tick()
    {
    }

    public bool Busy => Status_().Busy;

    public bool Faulted => Status_().Faulted;

    public bool AtNode => Status_().AtNode;

    public string Status => Status_().Status ?? string.Empty;

    /// <summary>The bench modes are Demeter's now (its debug window); a quest step never sees them set.</summary>
    public bool DryRun { get => false; set { } }

    public bool ProbeOnly { get => false; set { } }

    public void Stop()
    {
        Call<object?>(() => { (_stop ??= _pluginInterface.GetIpcSubscriber<object?>(StopGate)).InvokeAction(); return null; }, null);
        _statusUntil = TimeSpan.MinValue;
    }

    /// <summary>Show or hide Demeter's gather lists window. False when Demeter is not there.</summary>
    public bool ToggleLists()
        => Present && Call(() => { (_toggleLists ??= _pluginInterface.GetIpcSubscriber<object?>(ToggleListsGate)).InvokeAction(); return true; }, false);

    private ItemStatus Status_()
    {
        var now = _clock.Elapsed;
        if (now < _statusUntil)
            return _lastStatus;
        _statusUntil = now + StatusFor;
        _lastStatus = Call(() =>
        {
            var json = (_status ??= _pluginInterface.GetIpcSubscriber<string>(StatusGate)).InvokeFunc();
            return JsonSerializer.Deserialize<ItemStatus>(json, Json) ?? new ItemStatus();
        }, new ItemStatus { Status = "Demeter is not answering" });
        return _lastStatus;
    }

    private Description Describe(uint itemId)
    {
        var now = _clock.Elapsed;
        if (_description.ItemId == itemId && now < _description.Until)
            return _description.Value;
        var value = !Present ? new Description() : Call(() =>
        {
            var json = (_describe ??= _pluginInterface.GetIpcSubscriber<uint, string>(DescribeGate)).InvokeFunc(itemId);
            return JsonSerializer.Deserialize<Description>(json, Json) ?? new Description();
        }, new Description());
        _description = (itemId, value, now + DescribeFor);
        return value;
    }

    /// <summary>Demeter unloading mid-call is a degraded answer, never an exception here.</summary>
    private static T Call<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch
        {
            return fallback;
        }
    }

    private sealed class ItemStatus
    {
        public bool Busy { get; set; }
        public bool Faulted { get; set; }
        public bool AtNode { get; set; }
        public string? Status { get; set; }
    }

    private sealed class Description
    {
        public uint? ZoneId { get; set; }
        public string? Where { get; set; }
        public string? Timer { get; set; }
        public double? WaitSeconds { get; set; }
        public double? UpForSeconds { get; set; }
    }
}
