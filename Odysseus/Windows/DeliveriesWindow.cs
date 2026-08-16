using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Deliveries;
using Odysseus.Services.Quest;

namespace Odysseus.Windows;

/// <summary>
/// The custom-delivery clients: who is unlocked, their rank, this week's deliveries — and for the
/// ones that are not open yet, an Unlock button that queues the whole quest chain (each client's
/// unlock quest has prerequisites of its own).
///
/// <para>Running deliveries — buy, craft, turn in — is P8. This window is the unlock half.</para>
/// </summary>
public sealed class DeliveriesWindow : OdysseusWindow
{
    private readonly DeliveryCatalog _catalog;
    private readonly IDeliveryState _state;
    private readonly UnlockPlanner _unlock;
    private string _status = string.Empty;

    public DeliveriesWindow(DeliveryCatalog catalog, IDeliveryState state, UnlockPlanner unlock)
        : base("Odysseus Deliveries##OdysseusDeliveries")
    {
        _catalog = catalog;
        _state = state;
        _unlock = unlock;
        Size = new Vector2(560, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 240), MaximumSize = new Vector2(900, 1200) };
    }

    public override void Draw()
    {
        var unlocked = _catalog.All.Count(_state.IsUnlocked);
        ImGui.TextColored(OdysseusTheme.TextSecondary, $"Clients unlocked: {unlocked} / {_catalog.All.Count}");
        ImGui.Separator();

        if (!ImGui.BeginTable("##deliveries", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Client", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Unlock quest", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, 46f);
        ImGui.TableSetupColumn("Week", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("##act", ImGuiTableColumnFlags.WidthFixed, 76f);
        ImGui.TableHeadersRow();

        foreach (var client in _catalog.All)
            DrawRow(client);

        ImGui.EndTable();
        ImGui.Spacing();
        if (_status.Length > 0)
            ImGui.TextWrapped(_status);
        ImGui.TextColored(OdysseusTheme.TextDisabled,
            "Unlock queues the client's opening quest and everything it needs onto the priority list. " +
            "Running deliveries themselves is not built yet.");
    }

    private void DrawRow(DeliveryClient client)
    {
        var open = _state.IsUnlocked(client);
        var plan = open || client.UnlockQuestId == 0 ? null : _unlock.Plan(client.UnlockQuestId);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(open ? OdysseusTheme.TextPrimary : OdysseusTheme.TextDisabled, client.Name);

        ImGui.TableNextColumn();
        if (open)
            ImGui.TextColored(OdysseusTheme.StatusGreen, "unlocked");
        else if (plan is null)
            ImGui.TextColored(OdysseusTheme.TextDisabled, "—");
        else
        {
            var target = plan.Steps.LastOrDefault();
            ImGui.TextColored(OdysseusTheme.TextSecondary, target?.Name ?? $"Quest {client.UnlockQuestId}");
            ImGui.SameLine();
            ImGui.TextColored(plan.IsRunnable ? OdysseusTheme.TextDisabled : OdysseusTheme.StatusYellow, $"· {plan.Summary}");
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(OdysseusTheme.TextSecondary, open ? _state.Rank(client).ToString() : "—");

        ImGui.TableNextColumn();
        var used = open ? _state.UsedThisWeek(client) : null;
        ImGui.TextColored(OdysseusTheme.TextSecondary, used is { } u ? $"{u}/{client.DeliveriesPerWeek}" : "—");

        ImGui.TableNextColumn();
        if (open)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "ready");
            return;
        }
        using (ImRaii.Disabled(plan is null || !plan.IsRunnable))
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Unlock, "Unlock", OdysseusTheme.AccentDim,
                    plan is null ? "No unlock quest in the sheet."
                    : plan.IsRunnable ? $"Queue {plan.Summary} (Lv {client.UnlockLevel})."
                    : $"Cannot queue: {plan.Summary}.",
                    new Vector2(72, 22)))
            {
                var queued = _unlock.Queue(client.UnlockQuestId, client.Name);
                _status = $"{client.Name}: queued {queued.Steps.Count} quest(s) — {string.Join(" → ", queued.Steps.Select(x => x.Name))}";
            }
        }
    }
}
