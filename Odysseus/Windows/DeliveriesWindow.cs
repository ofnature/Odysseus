using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Deliveries;
using Odysseus.Services.Ipc;
using Odysseus.Services.Quest;

namespace Odysseus.Windows;

/// <summary>
/// The custom-delivery clients: this week's bonus routes, deliveries used, satisfaction, and the
/// actions — craft turn-in for an open client, Unlock for one that is not open yet. The scrip
/// table underneath is the point of the window: what you hold, what the remaining deliveries would
/// pay, and how much of that would be thrown away at the cap.
///
/// <para>
/// The craft route runs through <see cref="DeliveryRunner"/>. Buying ingredients does not exist
/// yet, so a run that comes up short stops and says what is missing.
/// </para>
/// </summary>
public sealed class DeliveriesWindow : OdysseusWindow
{
    private readonly DeliveryCatalog _catalog;
    private readonly IDeliveryState _state;
    private readonly IDeliveryBonus _bonus;
    private readonly ScripLedger _scrips;
    private readonly ArtisanIpc _artisan;
    private readonly UnlockPlanner _unlock;
    private readonly DeliveryRunner _runner;
    private readonly IDeliveryRequests _requests;

    /// <summary>
    /// On, a Craft turn-in runs exactly one delivery and stops. The craft-and-turn-in path has not
    /// been proven against the live game yet, so it starts safe — one delivery is enough to see the
    /// whole sequence, and cheap to undo if it goes wrong.
    /// </summary>
    private bool _oneShot = true;

    private string _status = string.Empty;
    private string _blockedReason = string.Empty;
    private bool _openBlockedPopup;
    private DeliveryRunState _lastRunState = DeliveryRunState.Idle;

    private const string BlockedPopup = "Turn-in stopped###OdysseusDeliveryBlocked";

    public DeliveriesWindow(DeliveryCatalog catalog, IDeliveryState state, IDeliveryBonus bonus, ScripLedger scrips,
        ArtisanIpc artisan, UnlockPlanner unlock, DeliveryRunner runner, IDeliveryRequests requests)
        : base("Odysseus Deliveries##OdysseusDeliveries")
    {
        _catalog = catalog;
        _state = state;
        _bonus = bonus;
        _scrips = scrips;
        _artisan = artisan;
        _unlock = unlock;
        _runner = runner;
        _requests = requests;
        Size = new Vector2(760, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 320), MaximumSize = new Vector2(1400, 1400) };
    }

    private static float ActionWidth => ImGui.CalcTextSize("Craft turn-in").X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 4f;

    public override void Draw()
    {
        TrackRunner();
        DrawHeader();
        DrawClients();
        DrawScrips();
        DrawBlockedPopup();
    }

    /// <summary>
    /// A run that stops itself — at the cap, out of materials — raises the same modal the button
    /// does, once, so the reason is not left sitting in a status line nobody is watching.
    /// </summary>
    private void TrackRunner()
    {
        var changed = _runner.State != _lastRunState;
        _lastRunState = _runner.State;

        if (changed && _runner.State is DeliveryRunState.Blocked or DeliveryRunState.Faulted)
        {
            _blockedReason = _runner.StatusLine;
            _openBlockedPopup = true;
        }
        // Follow the run every frame, not only on state changes — most of what is worth watching
        // (which delivery, what it is crafting) moves inside a single state.
        if (_runner.State != DeliveryRunState.Idle && _runner.StatusLine.Length > 0)
            _status = _runner.State is DeliveryRunState.Craft or DeliveryRunState.Travel
                or DeliveryRunState.Interact or DeliveryRunState.TurnIn
                ? $"[{_runner.Delivered}/{_runner.Target}] {_runner.StatusLine}"
                : _runner.StatusLine;
    }

    private void DrawHeader()
    {
        var unlocked = _catalog.All.Count(_state.IsUnlocked);
        OdysseusTheme.IdChip($"Unlocked {unlocked}/{_catalog.All.Count}");
        ImGui.SameLine(0f, 8f);
        if (_artisan.Available)
            OdysseusTheme.StateChip("Artisan ready");
        else
            OdysseusTheme.Chip("Artisan missing", OdysseusTheme.YellowDark, OdysseusTheme.TextPrimary);

        var overcapping = _scrips.WouldOvercap();
        if (overcapping.Count > 0)
        {
            ImGui.SameLine(0f, 8f);
            ImGui.TextColored(OdysseusTheme.StatusRed, $"{overcapping.Count} scrip(s) would overcap");
        }

        // Ranks and used-allowances stay zero until the client has fetched delivery data; say so
        // rather than presenting the zeros as facts. The bonus ticks do not need it — the week is
        // computed from the clock.
        if (!_state.DataLoaded)
        {
            ImGui.SameLine(0f, 8f);
            OdysseusTheme.Chip("Ranks not loaded", OdysseusTheme.YellowDark, OdysseusTheme.TextPrimary);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Rank and deliveries-used come from the server and read zero until the game's\n" +
                                 "Custom Deliveries window has been opened once this session.\n" +
                                 "Bonus ticks are unaffected.");
        }

        ImGui.SameLine(0f, 12f);
        ImGui.Checkbox("Test run — one delivery", ref _oneShot);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Craft turn-in stops after a single delivery so you can watch the whole\n" +
                             "sequence — craft, travel, hand over — before trusting it with a week.\n" +
                             "Turn it off to run the full remaining allowance.");
        ImGui.Separator();
    }

    private void DrawClients()
    {
        var nameWidth = ImGui.CalcTextSize("[00] Nitowikwe___").X;
        var bonusWidth = ImGui.CalcTextSize("Bonus").X + 26f;
        var deliveriesWidth = ImGui.CalcTextSize("Deliveries").X + 8f;
        var satWidth = ImGui.CalcTextSize("Satisfaction").X + 20f;

        if (!ImGui.BeginTable("##clients", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn("Client", ImGuiTableColumnFlags.WidthFixed, nameWidth);
        ImGui.TableSetupColumn("Bonus", ImGuiTableColumnFlags.WidthFixed, bonusWidth);
        ImGui.TableSetupColumn("Deliveries", ImGuiTableColumnFlags.WidthFixed, deliveriesWidth);
        ImGui.TableSetupColumn("Satisfaction", ImGuiTableColumnFlags.WidthFixed, satWidth);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        foreach (var client in _catalog.All)
            DrawClientRow(client);

        ImGui.EndTable();
    }

    private void DrawClientRow(DeliveryClient client)
    {
        var open = _state.IsUnlocked(client);
        var bonus = open ? _bonus.For(client) : BonusFlags.None;
        var remaining = _scrips.RemainingDeliveries(client);
        var used = client.DeliveriesPerWeek - remaining;

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextColored(OdysseusTheme.TextDisabled, $"[{client.Index - 1}]");
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(open ? OdysseusTheme.TextPrimary : OdysseusTheme.TextDisabled, client.Name);

        ImGui.TableNextColumn();
        if (open)
        {
            BonusTick(bonus.Craft, "Craft has this week's bonus — a bigger scrip payout.");
            ImGui.SameLine(0f, 3f);
            BonusTick(bonus.Gather, "Gathering has this week's bonus.");
            ImGui.SameLine(0f, 3f);
            BonusTick(bonus.Fish, "Fishing has this week's bonus.");
        }
        else
            ImGui.TextColored(OdysseusTheme.TextDisabled, "—");

        ImGui.TableNextColumn();
        ImGui.TextColored(open ? (remaining > 0 ? OdysseusTheme.TextSecondary : OdysseusTheme.TextDisabled) : OdysseusTheme.TextDisabled,
            open ? $"{used} / {client.DeliveriesPerWeek}" : "—");

        ImGui.TableNextColumn();
        if (open)
        {
            var (gauge, needed) = _state.Satisfaction(client);
            ImGui.TextColored(OdysseusTheme.TextSecondary,
                needed > 0 ? $"rank {_state.Rank(client)} · {gauge}/{needed}" : $"rank {_state.Rank(client)}");
            if (ImGui.IsItemHovered())
            {
                var paying = _scrips.PayingDeliveries(client);
                ImGui.SetTooltip(paying < remaining
                    ? $"{paying} of the {remaining} remaining deliveries are counted in the estimate —\n" +
                      "the gauge fills first and the rank-up changes the payout."
                    : "All remaining deliveries pay at this rate.");
            }
        }
        else
            ImGui.TextColored(OdysseusTheme.TextDisabled, "—");

        ImGui.TableNextColumn();
        if (!open)
        {
            DrawUnlock(client);
            return;
        }
        DrawRunActions(client, bonus, remaining);
    }

    private static void BonusTick(bool on, string tooltip)
    {
        ImGui.TextColored(on ? OdysseusTheme.StatusGreen : OdysseusTheme.TextDisabled, on ? "■" : "□");
        if (on && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private void DrawUnlock(DeliveryClient client)
    {
        var plan = client.UnlockQuestId == 0 ? null : _unlock.Plan(client.UnlockQuestId);
        using (ImRaii.Disabled(plan is null || !plan.IsRunnable))
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Unlock, "Unlock", OdysseusTheme.AccentDim,
                    plan is null ? "No unlock quest in the sheet."
                    : plan.IsRunnable ? $"Queue {plan.Summary} (Lv {client.UnlockLevel})."
                    : $"Cannot queue: {plan.Summary}.",
                    new Vector2(ActionWidth, 22)))
            {
                var queued = _unlock.Queue(client.UnlockQuestId, client.Name);
                _status = $"{client.Name}: queued {queued.Steps.Count} quest(s) — {string.Join(" → ", queued.Steps.Select(x => x.Name))}";
            }
        }
        if (plan is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(plan.IsRunnable ? OdysseusTheme.TextDisabled : OdysseusTheme.StatusYellow, $"{plan.Summary} · Lv {client.UnlockLevel}");
        }
    }

    private void DrawRunActions(DeliveryClient client, BonusFlags bonus, int remaining)
    {
        if (remaining <= 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "done this week");
            return;
        }

        // The route carrying this week's bonus is the one worth running, so it wears the accent.
        var (allowed, reason) = _scrips.MayTurnIn(client);
        var colour = !allowed ? OdysseusTheme.NeutralDark : bonus.Craft ? OdysseusTheme.AccentDim : OdysseusTheme.GreenDark;
        var payout = string.Join(", ", _scrips.PerDelivery(client).Select(p =>
            $"{p.Value:N0} {_scrips.Kinds.FirstOrDefault(k => k.RewardCurrency == p.Key)?.Name ?? p.Key.ToString()}"));
        var running = _runner.Client?.Index == client.Index && _runner.State is not (DeliveryRunState.Idle or DeliveryRunState.Done);

        // Name what they are actually asking for — it decides whether this client is worth a run.
        var wanted = _requests.For(client, _state.Rank(client)).FirstOrDefault(r => r.Route == DeliveryRoute.Craft);
        var asking = wanted is null
            ? "Cannot read this week's request yet."
            : $"Wants {wanted.ItemName} (collectability {wanted.CollectabilityHigh}).";

        var tip = allowed
            ? $"Craft turn-in{(bonus.Craft ? " (bonus week)" : "")} — pays {payout} each.\n{asking}\n" +
              (_oneShot ? "Test run: one delivery, then stop." : $"Runs all {remaining} remaining.") +
              "\nCrafts through Artisan; ingredients must already be stocked."
            : reason;

        if (running)
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Stop, "Stop", OdysseusTheme.NeutralDark,
                    "Stop the run.", new Vector2(ActionWidth, 22)))
            {
                _runner.Stop();
                _status = $"{client.Name}: stopped.";
            }
        }
        else if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Hammer, "Craft turn-in", colour, tip ?? string.Empty, new Vector2(ActionWidth, 22)))
        {
            if (!allowed)
            {
                _blockedReason = reason ?? "At the scrip cap.";
                _openBlockedPopup = true;
            }
            else if (!_runner.Start(client, _oneShot ? 1 : 0))
            {
                _blockedReason = _runner.StatusLine;
                _openBlockedPopup = true;
            }
            else
            {
                _status = _runner.StatusLine;
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(true))
        {
            ImGui.Button("Gather##g" + client.Index, new Vector2(62, 22));
            ImGui.SameLine();
            ImGui.Button("Fish##f" + client.Index, new Vector2(50, 22));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Gathering and fishing deliveries are not automated.");
    }

    private void DrawScrips()
    {
        OdysseusTheme.SectionHeader("SCRIPS");
        var currentWidth = ImGui.CalcTextSize("Current").X + 12f;
        var capWidth = ImGui.CalcTextSize("00,000").X + 12f;
        var gainWidth = ImGui.CalcTextSize("Max gain").X + 12f;
        var overWidth = ImGui.CalcTextSize("Overcap").X + 12f;

        if (!ImGui.BeginTable("##scrips", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, currentWidth);
        ImGui.TableSetupColumn("Cap", ImGuiTableColumnFlags.WidthFixed, capWidth);
        ImGui.TableSetupColumn("Max gain", ImGuiTableColumnFlags.WidthFixed, gainWidth);
        ImGui.TableSetupColumn("Overcap", ImGuiTableColumnFlags.WidthFixed, overWidth);
        ImGui.TableSetupColumn("Headroom", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableHeadersRow();

        foreach (var s in _scrips.Read())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(OdysseusTheme.TextDisabled, $"[{s.Scrip.RewardCurrency}]");
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(OdysseusTheme.TextPrimary, s.Scrip.Name);
            ImGui.TableNextColumn(); Number(s.Current, s.WouldOvercap ? OdysseusTheme.StatusYellow : OdysseusTheme.TextSecondary);
            ImGui.TableNextColumn(); Number(s.Cap, OdysseusTheme.TextDisabled);
            ImGui.TableNextColumn(); Number(s.MaxGain, OdysseusTheme.TextSecondary);
            ImGui.TableNextColumn();
            if (s.WouldOvercap) Number(s.Overcap, OdysseusTheme.StatusRed);
            else ImGui.TextColored(OdysseusTheme.TextDisabled, "—");
            ImGui.TableNextColumn();
            OdysseusTheme.ProgressBar(s.Cap > 0 ? s.Current / (float)s.Cap : 0f, 12f);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{s.Headroom:N0} until the cap");
        }
        ImGui.EndTable();

        ImGui.Spacing();
        if (_status.Length > 0)
            OdysseusTheme.TextWrappedColored(OdysseusTheme.TextSecondary, _status);
        OdysseusTheme.TextWrappedColored(OdysseusTheme.TextDisabled,
            "Max gain assumes every remaining delivery pays its highest rate, bonus weeks included. Overcap is what would " +
            "be thrown away — spend those scrips first. A turn-in that would overcap is stopped and says so. Unlock queues " +
            "the client's opening quest chain onto the priority list.");
    }

    private static void Number(int value, Vector4 colour)
    {
        var text = value.ToString("N0");
        var width = ImGui.GetContentRegionAvail().X;
        var offset = width - ImGui.CalcTextSize(text).X;
        if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        ImGui.TextColored(colour, text);
    }

    private void DrawBlockedPopup()
    {
        if (_openBlockedPopup)
        {
            ImGui.OpenPopup(BlockedPopup);
            _openBlockedPopup = false;
        }
        var open = true;
        if (!ImGui.BeginPopupModal(BlockedPopup, ref open, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ImGui.PushTextWrapPos(420f);
        ImGui.TextColored(OdysseusTheme.StatusRed, "Stopped before the scrip cap");
        ImGui.Spacing();
        ImGui.TextWrapped(_blockedReason);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (ImGui.Button("Understood", new Vector2(120, 24)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
