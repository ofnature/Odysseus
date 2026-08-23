#if DEBUG
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Deliveries;
using Odysseus.Services.Gathering;
using Odysseus.Services.Tribes;
using Odysseus.Services.Work;

namespace Odysseus.Windows;

/// <summary>
/// A bench for trying the work list by hand. <b>Debug builds only</b> — the whole file is compiled
/// out of a release, so none of it can be reached by anyone who has not built it themselves.
///
/// <para>
/// It exists to answer one question before the planner is written: does a list of jobs run in the
/// order it is listed, and does a job that fails cost only itself? A one-shot proves a single job
/// end to end; a two-entry list proves the ordering. Both are things worth knowing before anything
/// decides what should go on the list.
/// </para>
///
/// <para>
/// Deliberately the only place any of this is wired: the shipped windows are untouched, so the
/// release has no half-finished feature in it and nothing has to be unpicked later.
/// </para>
/// </summary>
public sealed class WorkbenchWindow : Window
{
    private readonly TribeCatalog _tribes;
    private readonly DeliveryCatalog _clients;
    private readonly WorkList _list;
    private readonly WorkRunner _runner;

    private DeliveryRoute _route = DeliveryRoute.Craft;
    private int _count;

    private readonly IOwnGatherer? _gatherer;
    private readonly IGatherWorld? _gatherWorld;

    public WorkbenchWindow(TribeCatalog tribes, DeliveryCatalog clients, WorkList list, WorkRunner runner,
        IOwnGatherer? gatherer = null, IGatherWorld? gatherWorld = null)
        : base("Odysseus Workbench (debug)###OdysseusWorkbench")
    {
        // Thrown here rather than in Draw: this window is built in the middle of a long constructor
        // and taking a null quietly turns an ordering mistake into a crash on every frame instead of
        // one clear failure at load.
        ArgumentNullException.ThrowIfNull(tribes);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(runner);

        _tribes = tribes;
        _clients = clients;
        _list = list;
        _runner = runner;
        _gatherer = gatherer;
        _gatherWorld = gatherWorld;
        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.TextColored(OdysseusTheme.TextDisabled,
            "Debug build only. Build a list, run one job or all of them, and watch the order.");
        ImGui.Separator();

        DrawRunControls();

        DrawGathering();

        ImGui.Spacing();
        DrawList();
        ImGui.Spacing();
        DrawPickers();
        DrawOutcomes();
    }

    private void DrawRunControls()
    {
        var running = _runner.State is WorkRunState.Starting or WorkRunState.Running;

        using (ImRaii.Disabled(running || _list.Count == 0))
        {
            if (ImGui.Button("Run one"))
                _runner.Begin(_list.Items, limit: 1);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Runs the job at the top of the list and stops. The smallest thing that proves anything.");
            ImGui.SameLine();
            if (ImGui.Button("Run all"))
                _runner.Begin(_list.Items);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!running))
        {
            if (ImGui.Button("Stop"))
                _runner.Stop();
        }

        ImGui.SameLine();
        ImGui.TextColored(running ? OdysseusTheme.WakeFoam : OdysseusTheme.TextSecondary,
            _runner.Status.Length > 0 ? _runner.Status : "idle");
    }

    /// <summary>
    /// Three buttons, each doing exactly one thing and then stopping. Opening a node has locked
    /// this client several times, so nothing here loops, retries, or runs on from one step to the
    /// next: press one, look at what happened, decide.
    /// </summary>
    private void DrawGathering()
    {
        if (_gatherWorld is null || _gatherer is null)
            return;

        OdysseusTheme.SectionHeader("GATHERING (debug)");

        var on = _gatherer.Enabled;
        if (ImGui.Checkbox("Let deliveries gather with ours", ref on))
            _gatherer.Enabled = on;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join('\u000A',
                "Off, a gather delivery hands to GatherBuddy exactly as it always did.",
                "On, it uses ours — which has wedged the client. Leave it off until the",
                "buttons below say why."));

        var mode = _gatherer.DryRun ? 0 : _gatherer.ProbeOnly ? 1 : 2;
        ImGui.SetNextItemWidth(330f);
        // Built rather than written as a literal: the items are separated by NULs, and a
        // source file with real NUL bytes in it is not a source file.
        var modes = string.Join('\u0000', "Walk only — never touch a node",
            "Probe — open one node, report, stop", "Full — work the nodes") + '\u0000';
        if (ImGui.Combo("Mode", ref mode, modes))
        {
            _gatherer.DryRun = mode == 0;
            _gatherer.ProbeOnly = mode == 1;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join('\u000A',
                "Walk only proves the travel and node-finding and touches nothing.",
                "Probe opens one node, writes down what the window holds, and stops —",
                "it never chooses a row.",
                "Full is the real thing: choose the item, work it, collect, move on."));

        if (mode == 2)
            ImGui.TextColored(OdysseusTheme.StatusYellow, "Full mode — watch the first node.");

        ImGui.Spacing();
        ImGui.TextColored(OdysseusTheme.TextDisabled, "Or step through it by hand — stand next to a node:");

        if (ImGui.Button("1. Say what is here"))
        {
            var found = _gatherWorld.NearestLiveNode(_nearbyIds);
            _gatherWorld.Log(found is { } n
                ? $"Probe: nearest node {n.NodeId} at {n.Distance:F1}y — mounted={_gatherWorld.IsMounted}, {_gatherWorld.Conditions}"
                : $"Probe: nothing up nearby — mounted={_gatherWorld.IsMounted}, {_gatherWorld.Conditions}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Touches nothing. Writes the node, the distance and every condition flag to the log.");

        ImGui.SameLine();
        if (ImGui.Button("2. Open it"))
        {
            var found = _gatherWorld.NearestLiveNode(_nearbyIds);
            if (found is { } n)
            {
                _gatherWorld.StopMoving();
                _gatherWorld.Log($"Probe: opening node {n.NodeId} at {n.Distance:F1}y — {_gatherWorld.Conditions}");
                var accepted = _gatherWorld.TryInteractWithDataId(n.NodeId);
                _gatherWorld.Log($"Probe: the interaction returned {accepted}.");
            }
            else
            {
                _gatherWorld.Log("Probe: nothing up nearby to open.");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join('\u000A',
                "One interaction and nothing else. No slot is chosen, no window is closed,",
                "nothing is retried. If the client wedges on this, the interaction itself is",
                "the fault and not anything that follows it."));

        ImGui.SameLine();
        if (ImGui.Button("3. Dump the open window"))
            _gatherWorld.DescribeOpenWindow();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads the open window and writes what it contains. Changes nothing.");

        ImGui.SameLine();
        if (ImGui.Button("4. Leave the node"))
            _gatherWorld.CloseNode();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join('\u000A',
                "Sends the window's own cancel — what Escape does — and refuses while an action",
                "is still running. Forcing it shut mid-action is what locked the client."));
    }

    /// <summary>Every node id in the atlas is too many to pass; nearby ids come from the object table.</summary>
    private readonly uint[] _nearbyIds = [];

    private void DrawList()
    {
        OdysseusTheme.SectionHeader($"WORK LIST ({_list.Count})");
        if (_list.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Nothing listed. Add a society or a client below.");
            return;
        }

        WorkItem? remove = null;
        var moveFrom = -1;
        var moveTo = -1;

        for (var i = 0; i < _list.Items.Count; i++)
        {
            var item = _list.Items[i];
            using (ImRaii.Disabled(i == 0))
                if (ImGui.SmallButton($"^##up{i}")) { moveFrom = i; moveTo = i - 1; }
            ImGui.SameLine();
            using (ImRaii.Disabled(i == _list.Items.Count - 1))
                if (ImGui.SmallButton($"v##down{i}")) { moveFrom = i; moveTo = i + 1; }
            ImGui.SameLine();
            if (ImGui.SmallButton($"x##drop{i}")) remove = item;
            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}. {item.Describe(NameOf(item))}");
        }

        if (moveFrom >= 0) _list.Move(moveFrom, moveTo);
        if (remove is not null) _list.Remove(remove);

        if (ImGui.SmallButton("Clear the list")) _list.Clear();
    }

    private void DrawPickers()
    {
        ImGui.SetNextItemWidth(90f);
        ImGui.InputInt("Count", ref _count);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How many allowances to spend. Zero means as many as are left.");
        _count = Math.Max(0, _count);

        if (ImGui.CollapsingHeader("Allied societies"))
            foreach (var tribe in _tribes.All.Where(t => t.IsRunnableKind).OrderBy(t => t.ExpansionId).ThenBy(t => t.Name))
            {
                if (ImGui.SmallButton($"Add##tribe{tribe.Id}")) _list.Add(new WorkItem(WorkKind.SocietyDailies, tribe.Id, Count: _count));
                ImGui.SameLine();
                ImGui.TextUnformatted($"{tribe.Name} — {tribe.DailyQuestIds.Count} dailies");
            }

        if (ImGui.CollapsingHeader("Custom deliveries"))
        {
            var route = (int)_route - 1;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.Combo("Route", ref route, "Craft\0Gather\0Fish\0"))
                _route = (DeliveryRoute)(route + 1);
            if (_route != DeliveryRoute.Craft)
                ImGui.TextColored(OdysseusTheme.StatusYellow,
                    "Gather and fish stop for you until the gathering runner is wired in.");

            foreach (var client in _clients.All)
            {
                if (ImGui.SmallButton($"Add##client{client.Index}")) _list.Add(new WorkItem(WorkKind.Delivery, client.Index, _route, _count));
                ImGui.SameLine();
                ImGui.TextUnformatted($"{client.Name} — up to {client.DeliveriesPerWeek} a week");
            }
        }
    }

    private void DrawOutcomes()
    {
        if (_runner.Outcomes.Count == 0) return;

        OdysseusTheme.SectionHeader("WHAT HAPPENED");
        foreach (var outcome in _runner.Outcomes)
            ImGui.TextColored(outcome.Ran ? OdysseusTheme.TextPrimary : OdysseusTheme.StatusYellow,
                $"{(outcome.Ran ? "ran" : "skipped")}  {outcome.Item.Describe(outcome.Name)}" +
                (outcome.Note.Length > 0 ? $" — {outcome.Note}" : ""));
    }

    private string NameOf(WorkItem item) => item.Kind switch
    {
        WorkKind.SocietyDailies => _tribes.ById((byte)item.TargetId)?.Name ?? $"society {item.TargetId}",
        WorkKind.Delivery => _clients.All.FirstOrDefault(c => c.Index == item.TargetId)?.Name ?? $"client {item.TargetId}",
        _ => item.TargetId.ToString(),
    };
}

/// <summary>
/// The work list driven onto the runners that already exist. Debug-only for now: when the planner
/// lands this moves out of the gate unchanged, because it adds no behaviour of its own — it only
/// forwards, and reports whether the thing it started is still going.
/// </summary>
public sealed class WorkEngines : IWorkEngines
{
    private readonly TribeCatalog _tribes;
    private readonly DeliveryCatalog _clients;
    private readonly TribeRunner _tribeRunner;
    private readonly DeliveryRunner _deliveryRunner;

    public WorkEngines(TribeCatalog tribes, DeliveryCatalog clients, TribeRunner tribeRunner, DeliveryRunner deliveryRunner)
    {
        ArgumentNullException.ThrowIfNull(tribes);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(tribeRunner);
        ArgumentNullException.ThrowIfNull(deliveryRunner);

        _tribes = tribes;
        _clients = clients;
        _tribeRunner = tribeRunner;
        _deliveryRunner = deliveryRunner;
    }

    public bool StartSociety(uint societyId, int count, out string reason)
    {
        if (_tribes.ById((byte)societyId) is not { } tribe)
        {
            reason = "no such society";
            return false;
        }
        if (!tribe.IsRunnableKind)
        {
            reason = $"{tribe.Kind} societies are not runnable yet";
            return false;
        }
        if (_tribeRunner.Start(tribe))
        {
            reason = string.Empty;
            return true;
        }
        reason = _tribeRunner.StatusLine.Length > 0 ? _tribeRunner.StatusLine : "nothing to do there today";
        return false;
    }

    public bool StartDelivery(uint clientId, DeliveryRoute route, int count, out string reason)
    {
        if (_clients.All.FirstOrDefault(c => c.Index == clientId) is not { } client)
        {
            reason = "no such client";
            return false;
        }
        if (_deliveryRunner.Start(client, route, count))
        {
            reason = string.Empty;
            return true;
        }
        reason = _deliveryRunner.StatusLine.Length > 0 ? _deliveryRunner.StatusLine : "nothing to deliver";
        return false;
    }

    // The inverse, for the same reason as OwnGatherer.Busy: a state added to either enum must not
    // silently mean "finished" here.
    public bool Busy
        => _tribeRunner.State is not (TribeRunState.Idle or TribeRunState.Done or TribeRunState.Faulted)
           || !_deliveryRunner.IsFinished;

    public bool Faulted
        => _tribeRunner.State == TribeRunState.Faulted || _deliveryRunner.State == DeliveryRunState.Faulted;

    public string FaultReason
        => _tribeRunner.State == TribeRunState.Faulted ? _tribeRunner.StatusLine : _deliveryRunner.StatusLine;

    public string NameOf(WorkKind kind, uint targetId) => kind switch
    {
        WorkKind.SocietyDailies => _tribes.ById((byte)targetId)?.Name ?? $"society {targetId}",
        WorkKind.Delivery => _clients.All.FirstOrDefault(c => c.Index == targetId)?.Name ?? $"client {targetId}",
        _ => targetId.ToString(),
    };
}
#endif
