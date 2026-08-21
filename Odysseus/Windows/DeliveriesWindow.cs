using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Odysseus.Config;
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
    private readonly OdysseusConfig _config;
    private readonly IGatherer _gatherer;
    private readonly IScripShop _shop;
    private readonly SpendPlanner _spending;
    private readonly SpendRunner _spender;
    private readonly Action _save;

    /// <summary>
    /// On, a Craft turn-in runs exactly one delivery and stops. The craft-and-turn-in path has not
    /// been proven against the live game yet, so it starts safe — one delivery is enough to see the
    /// whole sequence, and cheap to undo if it goes wrong.
    /// </summary>
    private bool _oneShot = true;

    private string _status = string.Empty;
    private string _blockedReason = string.Empty;
    private DeliveryStop _blockedKind = DeliveryStop.ScripCap;
    private bool _openBlockedPopup;
    private DeliveryRunState _lastRunState = DeliveryRunState.Idle;
    /// <summary>Which client's "everything it can ask for" list is expanded, 0 for none.</summary>
    private uint _gatherListFor;

    private const string BlockedPopup = "Turn-in stopped###OdysseusDeliveryBlocked";

    public DeliveriesWindow(DeliveryCatalog catalog, IDeliveryState state, IDeliveryBonus bonus, ScripLedger scrips,
        ArtisanIpc artisan, UnlockPlanner unlock, DeliveryRunner runner, IDeliveryRequests requests,
        OdysseusConfig config, Action save, IGatherer gatherer, IScripShop shop, SpendPlanner spending, SpendRunner spender)
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
        _config = config;
        _save = save;
        _gatherer = gatherer;
        _shop = shop;
        _spending = spending;
        _spender = spender;
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
            _blockedKind = _runner.StoppedBecause;
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

        // The twelve allowances are shared across every client, so this — not the per-client 0/6 —
        // is what actually decides how much of the week is left.
        var weekly = _scrips.WeeklyRemaining;
        ImGui.SameLine(0f, 8f);
        if (weekly > 0)
            OdysseusTheme.Chip($"{weekly}/{DeliveryLimits.WeeklyAllowance} deliveries left",
                OdysseusTheme.GreenDark, OdysseusTheme.TextPrimary);
        else
            OdysseusTheme.Chip("Weekly limit hit", OdysseusTheme.NeutralDark, OdysseusTheme.TextPrimary);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{DeliveryLimits.WeeklyAllowance} deliveries a week across all clients together,\n" +
                             "at most 6 with any one of them. Resets Tuesday 08:00 UTC.\n" +
                             (weekly > 0
                                 ? "Per-client counts are capped by whatever is left here."
                                 : "Nothing more can be turned in until the reset."));

        ImGui.SameLine(0f, 8f);
        if (_artisan.Available)
            OdysseusTheme.StateChip("Artisan ready");
        else
        {
            var why = _artisan.Unavailable;
            var loaded = why.StartsWith("Artisan is loaded", StringComparison.Ordinal);
            OdysseusTheme.Chip(loaded ? "Artisan not answering" : "Artisan missing",
                OdysseusTheme.YellowDark, OdysseusTheme.TextPrimary);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(why.Length == 0
                    ? "Artisan is not available."
                    : why + '\u000A' + (loaded
                        ? "Nothing to install — the handoff itself is at fault."
                        : "Install or enable Artisan, then reopen this window."));
        }

        ImGui.SameLine(0f, 6f);
        if (_gatherer.Available)
        {
            OdysseusTheme.StateChip("GatherBuddy ready");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"GatherBuddy Reborn, IPC v{_gatherer.Version}.\n" +
                                 "It takes no request — Odysseus switches auto-gather on and watches\n" +
                                 "the bag, so the item must be on one of its auto-gather lists.\n" +
                                 "Use \"Gather list\" on a client to see everything it can ask for.");
        }
        else
        {
            OdysseusTheme.Chip("GatherBuddy missing", OdysseusTheme.YellowDark, OdysseusTheme.TextPrimary);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Gather deliveries will stop and tell you what to collect.");
        }

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

        // Artisan switches to whatever job the recipe belongs to, so the choice is yours to make.
        ImGui.SameLine(0f, 12f);
        ImGui.TextColored(OdysseusTheme.TextSecondary, "Craft as");
        ImGui.SameLine(0f, 4f);
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("Current job").X + ImGui.GetFrameHeight() + 12f);
        var job = _config.DeliveryCraftJob;
        var label = job >= 0 && job < RecipeOption.JobNames.Length ? RecipeOption.JobNames[job] : "Current job";
        if (ImGui.BeginCombo("##craftjob", label))
        {
            if (ImGui.Selectable("Current job", job < 0)) { _config.DeliveryCraftJob = -1; _save(); }
            for (var i = 0; i < RecipeOption.JobNames.Length; i++)
                if (ImGui.Selectable(RecipeOption.JobNames[i], job == i)) { _config.DeliveryCraftJob = i; _save(); }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which job crafts delivery items. Artisan switches the character to the\n" +
                             "recipe's job, so this decides what you get pulled onto.\n" +
                             "\"Current job\" keeps you where you are whenever that job can make it.");
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
            var weeklySpent = _scrips.WeeklyRemaining <= 0;
            ImGui.TextColored(OdysseusTheme.TextDisabled, weeklySpent ? "weekly limit hit" : "done this week");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(weeklySpent
                    ? $"All {DeliveryLimits.WeeklyAllowance} weekly deliveries are used across every client,\n" +
                      "so nothing more can be turned in until Tuesday 08:00 UTC."
                    : $"{client.Name} has taken all {client.DeliveriesPerWeek} of its own deliveries this week.\n" +
                      $"{_scrips.WeeklyRemaining} of the shared allowance remain for other clients.");
            return;
        }

        var running = _runner.Client?.Index == client.Index && !_runner.IsFinished;
        if (running)
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Stop, "Stop", OdysseusTheme.NeutralDark,
                    "Stop the run.", new Vector2(ActionWidth, 22)))
            {
                _runner.Stop();
                _status = $"{client.Name}: stopped.";
            }
            return;
        }

        RouteButton(client, DeliveryRoute.Craft, bonus, remaining, FontAwesomeIcon.Hammer, "Craft turn-in", ActionWidth);
        ImGui.SameLine();
        RouteButton(client, DeliveryRoute.Gather, bonus, remaining, FontAwesomeIcon.Leaf, "Gather", 74f);
        ImGui.SameLine();
        RouteButton(client, DeliveryRoute.Fish, bonus, remaining, FontAwesomeIcon.Fish, "Fish", 62f);

        // GatherBuddy cannot be told what to fetch, so the way to make the handoff work every week
        // is to seed its list with everything this client can ever ask for. That set is small.
        ImGui.SameLine();
        if (OdysseusTheme.IconButton($"##gl{client.Index}", FontAwesomeIcon.ListUl,
                "Everything this client can ask for on the gather and fish routes —\n" +
                "add these to a GatherBuddy auto-gather list once and the handoff\n" +
                "works whatever the week rolls."))
            _gatherListFor = _gatherListFor == client.Index ? 0 : client.Index;

        if (_gatherListFor == client.Index)
            DrawGatherList(client);
    }

    /// <summary>The full set of possible gather and fish requests, with ids to paste into GatherBuddy.</summary>
    private void DrawGatherList(DeliveryClient client)
    {
        var rank = _state.Rank(client);
        var rows = _requests.Possible(client, rank, DeliveryRoute.Gather)
            .Concat(_requests.Possible(client, rank, DeliveryRoute.Fish)).ToList();

        ImGui.Indent(12f);
        if (rows.Count == 0)
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Nothing listed for this client's rank.");
        else
        {
            ImGui.TextColored(OdysseusTheme.TextSecondary,
                $"{client.Name} rank {rank} can ask for any of these. Add them to a GatherBuddy auto-gather list:");
            foreach (var row in rows)
                ImGui.TextColored(OdysseusTheme.TextDisabled,
                    $"  [{row.Route}] {row.ItemName}  ·  id {row.ItemId}  ·  collectability {row.CollectabilityLow}+");

            if (ImGui.SmallButton($"Copy ids##ci{client.Index}"))
            {
                ImGui.SetClipboardText(string.Join(", ", rows.Select(r => r.ItemId)));
                _status = $"{client.Name}: copied {rows.Count} item id(s).";
            }
        }
        ImGui.Unindent(12f);
    }

    /// <summary>
    /// One route's button. All three run the same travel and turn-in; only the sourcing differs, so
    /// the tooltip is where the difference is spelled out.
    /// </summary>
    private void RouteButton(DeliveryClient client, DeliveryRoute route, BonusFlags bonus, int remaining,
        FontAwesomeIcon icon, string label, float width)
    {
        var (allowed, reason) = _scrips.MayTurnIn(client, route);
        var hasBonus = bonus[route];
        // The route carrying this week's bonus is the one worth running, so it wears the accent.
        var colour = !allowed ? OdysseusTheme.NeutralDark : hasBonus ? OdysseusTheme.AccentDim : OdysseusTheme.GreenDark;

        var payout = string.Join(", ", _scrips.PerDelivery(client, route).Select(p =>
            $"{p.Value:N0} {_scrips.Kinds.FirstOrDefault(k => k.RewardCurrency == p.Key)?.Name ?? p.Key.ToString()}"));

        // Name what they are actually asking for — it decides whether this route is worth running.
        var wanted = _requests.For(client, _state.Rank(client)).FirstOrDefault(r => r.Route == route);
        var asking = wanted is null
            ? "Cannot read this week's request yet."
            : $"Wants {wanted.ItemName} (collectability {wanted.CollectabilityHigh}).";
        var sourcing = route == DeliveryRoute.Craft
            ? "Buys ingredients from the merchant nearby, then crafts through Artisan."
            : $"Odysseus does not {(route == DeliveryRoute.Fish ? "fish" : "gather")} yet — have them in the bag and it\n" +
              "handles the travel and the turn-in, or it stops and says where to find them.";

        var tip = allowed
            ? $"{label}{(hasBonus ? " (bonus week)" : "")} — pays {payout} each.\n{asking}\n" +
              (_oneShot ? "Test run: one delivery, then stop." : $"Runs all {remaining} remaining.") +
              $"\n{sourcing}"
            : reason;

        if (!OdysseusTheme.IconTextButton(icon, label, colour, tip ?? string.Empty, new Vector2(width, 22)))
            return;

        if (!allowed)
        {
            _blockedReason = reason ?? "At the scrip cap.";
            _blockedKind = DeliveryStop.ScripCap;
            _openBlockedPopup = true;
        }
        else if (!_runner.Start(client, route, _oneShot ? 1 : 0))
        {
            _blockedReason = _runner.StatusLine;
            _blockedKind = _runner.StoppedBecause;
            _openBlockedPopup = true;
        }
        else
        {
            _status = _runner.StatusLine;
        }
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

        DrawSpending();

        ImGui.Spacing();
        if (_status.Length > 0)
            OdysseusTheme.TextWrappedColored(OdysseusTheme.TextSecondary, _status);
        OdysseusTheme.TextWrappedColored(OdysseusTheme.TextDisabled,
            $"Max gain spends the {DeliveryLimits.WeeklyAllowance} shared weekly allowances on the best-paying clients " +
            "at their highest rate, bonus weeks included — it is a ceiling, not a forecast. Overcap is what would " +
            "be thrown away — spend those scrips first. A turn-in that would overcap is stopped and says so. Unlock queues " +
            "the client's opening quest chain onto the priority list.");
    }

    /// <summary>
    /// What to spend scrips on. The auto toggle is the parent and the rules cascade under it, but
    /// they also drive the Spend button — so what happens unattended is exactly what the button
    /// would have done, rather than a second, hidden policy.
    /// </summary>
    private void DrawSpending()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Spending"))
            return;

        var auto = _config.AutoSpendScrips;
        if (ImGui.Checkbox("Spend scrips automatically when a turn-in would overcap", ref auto))
        {
            _config.AutoSpendScrips = auto;
            _save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off, nothing is ever bought without pressing Spend.\n" +
                             "On, the rules below run themselves rather than letting a run stall at the cap.");

        ImGui.Indent(18f);

        var books = _config.SpendOnMasterBooks;
        if (ImGui.Checkbox("Master recipe tomes I have not read", ref books))
        {
            _config.SpendOnMasterBooks = books;
            _save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Bought cheapest first, one copy each, and only if unread.\n" +
                             "The one rule that empties itself — once they are all read it stops.\n" +
                             "Gathering folklore tomes are not detected; tick those below instead.");

        DrawSpendList();

        var reserve = _config.SpendReserve;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Keep in reserve", ref reserve, 100))
        {
            _config.SpendReserve = Math.Max(0, reserve);
            _save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scrips left untouched, so spending makes room rather than stripping the balance.");

        ImGui.Unindent(18f);

        // The plan is shown before it is run, and it is the same plan the auto trigger would use.
        var plan = _spending.Plan(_config.SpendOnMasterBooks, _config.SpendList, _config.SpendReserve);
        ImGui.Spacing();

        if (!_spender.IsFinished)
        {
            if (OdysseusTheme.IconTextButton(FontAwesomeIcon.Stop, "Stop", OdysseusTheme.NeutralDark,
                    "Stop spending.", new Vector2(110, 22)))
                _spender.Stop();
        }
        else if (OdysseusTheme.IconTextButton(FontAwesomeIcon.ShoppingCart, "Spend",
                     plan.IsEmpty ? OdysseusTheme.NeutralDark : OdysseusTheme.GreenDark,
                     plan.IsEmpty ? plan.Summary : plan.Summary + "\n" + string.Join("\n",
                         plan.Lines.Select(l => $"  {l.Quantity} × {l.Offer.Name} — {l.Total:N0}")) +
                     "\nStand at a scrip vendor; Odysseus does not travel to them.",
                     new Vector2(110, 22)) && !plan.IsEmpty)
        {
            if (!_spender.Start(plan))
            {
                _blockedReason = _spender.StatusLine;
                _blockedKind = DeliveryStop.Setup;
                _openBlockedPopup = true;
            }
        }

        ImGui.SameLine();
        var line = _spender.IsFinished ? plan.Summary : _spender.StatusLine;
        ImGui.TextColored(plan.IsEmpty && _spender.IsFinished ? OdysseusTheme.TextDisabled : OdysseusTheme.TextSecondary, line);
    }

    private void DrawSpendList()
    {
        foreach (var entry in _config.SpendList.ToList())
        {
            var offer = _shop.Offers.FirstOrDefault(o => o.ItemId == entry.ItemId);
            var enabled = entry.Enabled;
            if (ImGui.Checkbox($"##en{entry.ItemId}", ref enabled)) { entry.Enabled = enabled; _save(); }
            ImGui.SameLine();
            ImGui.TextColored(enabled ? OdysseusTheme.TextPrimary : OdysseusTheme.TextDisabled,
                offer is null ? $"item {entry.ItemId} (not on any vendor)" : $"{offer.Name} — {offer.Cost:N0}");

            ImGui.SameLine();
            var keep = entry.KeepStocked;
            ImGui.SetNextItemWidth(90f);
            if (ImGui.InputInt($"keep##k{entry.ItemId}", ref keep)) { entry.KeepStocked = Math.Max(0, keep); _save(); }

            ImGui.SameLine();
            if (ImGui.SmallButton($"×##rm{entry.ItemId}")) { _config.SpendList.Remove(entry); _save(); }
        }

        // Add from what the vendors actually stock, so an id can never be mistyped.
        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo("##addspend", "Add an item…"))
        {
            foreach (var offer in _shop.Offers
                         .Where(o => !o.IsBook && _config.SpendList.All(e => e.ItemId != o.ItemId))
                         .OrderBy(o => o.Name))
            {
                if (!ImGui.Selectable($"{offer.Name} — {offer.Cost:N0}##{offer.ItemId}")) continue;
                _config.SpendList.Add(new SpendEntry { ItemId = offer.ItemId, Enabled = true });
                _save();
            }
            ImGui.EndCombo();
        }
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
        var (heading, colour) = _blockedKind switch
        {
            DeliveryStop.ScripCap => ("Stopped before the scrip cap", OdysseusTheme.StatusRed),
            DeliveryStop.Materials => ("Nothing left to turn in", OdysseusTheme.StatusYellow),
            DeliveryStop.Fault => ("The run hit a problem", OdysseusTheme.StatusRed),
            _ => ("Cannot start", OdysseusTheme.StatusYellow),
        };
        ImGui.TextColored(colour, heading);
        ImGui.Spacing();
        ImGui.TextWrapped(_blockedReason);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (ImGui.Button("Understood", new Vector2(120, 24)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
