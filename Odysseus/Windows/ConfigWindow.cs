using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Odysseus.Config;
using Odysseus.Services.Ipc;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;

namespace Odysseus.Windows;

/// <summary>Navigation sections in the settings sidebar.</summary>
internal enum ConfigSection
{
    General,
    Priority,
    Paths,
    Wake,
    Handoffs,
    Fleet,
    Debug,
}

/// <summary>
/// Odysseus settings window. Same layout as the rest of the suite — master toggle in the header,
/// left sidebar with dim small-cap group headers and an accent bar on the selected row, bordered
/// content pane, footer status line — carrying the wine-dark accent.
/// </summary>
public sealed class ConfigWindow : OdysseusWindow
{
    private const float SidebarWidth = 150f;

    private readonly OdysseusConfig _config;
    private readonly Action _save;
    private readonly PluginPresence _presence;
    private readonly PathStore _pathStore;
    private readonly string _defaultBundlePath;
    private readonly PriorityList _priority;
    private readonly IPriorityWorld _priorityWorld;
    private readonly QuestCatalog _catalog;
    /// <summary>How many reward items are banked and waiting for a vendor.</summary>
    private readonly Func<int> _pendingSales;

    private ConfigSection _currentSection = ConfigSection.General;
    private string _bundlePath;
    private string _importStatus = string.Empty;
    private string _prioritySearch = string.Empty;

    public ConfigWindow(OdysseusConfig config, Action save, PluginPresence presence, PathStore pathStore, string defaultBundlePath,
        PriorityList priority, IPriorityWorld priorityWorld, QuestCatalog catalog, Func<int> pendingSales)
        : base("Odysseus Settings##OdysseusConfig")
    {
        _config = config;
        _save = save;
        _presence = presence;
        _pathStore = pathStore;
        _defaultBundlePath = defaultBundlePath;
        _bundlePath = defaultBundlePath;
        _priority = priority;
        _priorityWorld = priorityWorld;
        _catalog = catalog;
        _pendingSales = pendingSales;

        Size = new Vector2(620, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(540, 400),
            MaximumSize = new Vector2(900, 900),
        };
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        try
        {
            DrawHeader();
            ImGui.Separator();
            ImGui.Spacing();
            DrawMainLayout();
            ImGui.Spacing();
            ImGui.Separator();
            DrawFooter();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    // ------------------------------------------------------------------ header / footer

    private void DrawHeader()
    {
        var enabled = _config.Enabled;
        if (ImGui.Checkbox("Enable Odysseus", ref enabled))
        {
            _config.Enabled = enabled;
            _save();
        }

        ImGui.SameLine(0f, 20f);
        var missing = _presence.MissingSummary();
        if (missing.Length == 0)
            OdysseusTheme.StatusDot(_config.Enabled, "Ready", "Disabled");
        else
            ImGui.TextColored(OdysseusTheme.StatusRed, "⚠ " + missing);

        ImGui.TextDisabled("Runs the Main Scenario unattended — and remembers where it stopped.");
    }

    private void DrawFooter()
    {
        ImGui.TextColored(OdysseusTheme.TextSecondary, $"Odysseus v{OdysseusPlugin.PluginVersion}");

        ImGui.SameLine(0f, 20f);
        OdysseusTheme.DependencyChip("vnavmesh", _presence.Vnavmesh);
        ImGui.SameLine(0f, 12f);
        OdysseusTheme.DependencyChip("Lifestream", _presence.Lifestream);
        ImGui.SameLine(0f, 12f);
        OdysseusTheme.DependencyChip("TextAdvance", _presence.TextAdvance);
        ImGui.SameLine(0f, 12f);
        OdysseusTheme.DependencyChip("Daedalus", _presence.Daedalus, required: false);
        ImGui.SameLine(0f, 12f);
        OdysseusTheme.DependencyChip("BossMod", _presence.BossMod, required: false);
        ImGui.SameLine(0f, 12f);
        OdysseusTheme.DependencyChip("Theseus", _presence.Theseus, required: false);
    }

    // ------------------------------------------------------------------ layout

    private void DrawMainLayout()
    {
        var availableHeight = ImGui.GetContentRegionAvail().Y - 32f; // reserve the footer line

        ImGui.BeginChild("##SidebarContainer", new Vector2(SidebarWidth + 10f, availableHeight), false);
        DrawSidebar();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##ContentArea", new Vector2(0, availableHeight), true);
        DrawCurrentSection();
        ImGui.EndChild();
    }

    private void DrawSidebar()
    {
        ImGui.BeginChild("##ConfigSidebar", new Vector2(SidebarWidth, 0), true);

        DrawCategoryHeader("RUN");
        DrawNavItem("General", ConfigSection.General);
        DrawNavItem("Priority", ConfigSection.Priority);
        DrawNavItem("Paths", ConfigSection.Paths);
        DrawNavItem("Handoffs", ConfigSection.Handoffs);
        ImGui.Spacing();

        DrawCategoryHeader("RECOVERY");
        // The Wake gets its own foam so the resume system is identifiable on sight.
        DrawNavItem("The Wake", ConfigSection.Wake, OdysseusTheme.WakeFoam);
        ImGui.Spacing();

        DrawCategoryHeader("FLEET");
        DrawNavItem("Dashboard", ConfigSection.Fleet);
        ImGui.Spacing();

        DrawCategoryHeader("SYSTEM");
        DrawNavItem("Debug", ConfigSection.Debug);

        ImGui.EndChild();
    }

    private static void DrawCategoryHeader(string label)
        => ImGui.TextColored(OdysseusTheme.StatusGrey, label);

    private void DrawNavItem(string label, ConfigSection section, Vector4? color = null)
    {
        var isSelected = _currentSection == section;
        var rowAccent = color ?? OdysseusTheme.AccentWine;
        var rowWash = color is null ? OdysseusTheme.AccentWash : OdysseusTheme.WakeWash;

        // Selection: faint wash + 2px accent bar on the left edge (suite identity).
        if (isSelected)
        {
            var cursorPos = ImGui.GetCursorScreenPos();
            var regionAvail = ImGui.GetContentRegionAvail();
            var drawList = ImGui.GetWindowDrawList();
            var rowMax = new Vector2(cursorPos.X + regionAvail.X,
                cursorPos.Y + ImGui.GetTextLineHeightWithSpacing());
            drawList.AddRectFilled(cursorPos, rowMax, ImGui.GetColorU32(rowWash));
            drawList.AddRectFilled(cursorPos, new Vector2(cursorPos.X + 2f, rowMax.Y),
                ImGui.GetColorU32(rowAccent));
        }

        ImGui.Indent(10);

        var textColor = isSelected ? rowAccent : color ?? OdysseusTheme.TextSecondary;
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        ImGui.PushStyleColor(ImGuiCol.Header, rowWash);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, rowWash);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, rowWash);

        var clicked = ImGui.Selectable($"  {label}##{section}", isSelected, ImGuiSelectableFlags.None,
            new Vector2(SidebarWidth - 25, 0));

        ImGui.PopStyleColor(4);
        ImGui.Unindent(10);

        if (clicked)
            _currentSection = section;
    }

    // ------------------------------------------------------------------ sections

    private void DrawCurrentSection()
    {
        switch (_currentSection)
        {
            case ConfigSection.General: DrawGeneralSection(); break;
            case ConfigSection.Priority: DrawPrioritySection(); break;
            case ConfigSection.Paths: DrawPathsSection(); break;
            case ConfigSection.Handoffs: DrawHandoffsSection(); break;
            case ConfigSection.Wake: DrawWakeSection(); break;
            case ConfigSection.Fleet: DrawFleetSection(); break;
            case ConfigSection.Debug: DrawDebugSection(); break;
        }
    }

    private void DrawGeneralSection()
    {
        OdysseusTheme.SectionHeader("GENERAL");

        var continueNext = _config.ContinueToNextQuest;
        if (ImGui.Checkbox("Continue into the next MSQ quest", ref continueNext))
        {
            _config.ContinueToNextQuest = continueNext;
            _save();
        }
        OdysseusTheme.HelpMarker(
            "Off runs one quest and stops — what you want while a path is still being proven.");

        var stopAt = _config.StopAtLevel;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Stop at level", ref stopAt))
        {
            _config.StopAtLevel = Math.Clamp(stopAt, 0, 100);
            _save();
        }
        OdysseusTheme.HelpMarker("0 = never. Parks a trial alt short of its cap, or holds a sync level.");

        var gcNames = new[] { "Not chosen (stop and ask)", "Maelstrom", "Twin Adder", "Immortal Flames" };
        var gc = (int)_config.PreferredGrandCompany;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Grand Company", ref gc, gcNames, gcNames.Length))
        {
            _config.PreferredGrandCompany = (byte)Math.Clamp(gc, 0, 3);
            _save();
        }
        OdysseusTheme.HelpMarker("Which company to join when the ARR story asks. Ignored once the character has joined one.");

        var pickRewards = _config.PickQuestRewards;
        if (ImGui.Checkbox("Pick quest rewards automatically", ref pickRewards))
        {
            _config.PickQuestRewards = pickRewards;
            _save();
        }
        OdysseusTheme.HelpMarker(
            "Done by TextAdvance under Odysseus's control, using TextAdvance's own reward priority (gil, vendor " +
            "value, gear coffers, gear for your job). Odysseus then presses Complete. Off = the reward window " +
            "waits for you and the run says so.");
        if (!_presence.TextAdvance)
        {
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.StatusYellow, "(TextAdvance not loaded)");
        }

        var sellRewards = _config.SellQuestRewards;
        if (ImGui.Checkbox("Sell crafter and gatherer quest rewards", ref sellRewards))
        {
            _config.SellQuestRewards = sellRewards;
            _save();
        }
        OdysseusTheme.HelpMarker(
            "Disciple of the Hand and Land quest lines pay in tools and gear that a finished character has no use " +
            "for. With this on, whatever such a quest hands over is sold at the next vendor a run opens.\n\n" +
            "Only what the quest measurably added to your bags is ever offered, capped at that many — the bag is " +
            "counted before the hand-in and again after, so a reward of one Venture can never reach the stack " +
            "behind it, and nothing you already owned is touched.\n\n" +
            "It sells rather than discards because a vendor sale can be undone from the buyback list. It will " +
            "still sell materia and Allagan pieces, which are worth more elsewhere.");
        if (_config.SellQuestRewards && _pendingSales() is > 0 and var owed)
        {
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.TextSecondary, $"({owed} waiting for a vendor)");
        }
    }

    private void DrawPrioritySection()
    {
        OdysseusTheme.SectionHeader("PRIORITY QUESTS");
        ImGui.TextWrapped(
            "Quests to run before the Main Scenario continues, in this order. Checked at every quest boundary — " +
            "when you press Start and after each quest completes — never mid-quest. The first entry that is ready " +
            "(unlocked, level met, has a path) runs; an entry already in your journal runs first of all.");
        ImGui.Spacing();

        var persist = _config.PersistPriorityList;
        if (ImGui.Checkbox("Keep the list across sessions", ref persist))
        {
            _config.PersistPriorityList = persist;
            _priority.SetPersist(persist);
            _save();
        }
        OdysseusTheme.HelpMarker("Off: the list lives until the client closes, and the saved copy is cleared now.");

        var autoRemove = _config.AutoRemoveCompletedPriority;
        if (ImGui.Checkbox("Remove quests from the list when they complete", ref autoRemove))
        {
            _config.AutoRemoveCompletedPriority = autoRemove;
            _priority.AutoRemoveCompleted = autoRemove;
            _save();
        }
        OdysseusTheme.HelpMarker("Checked every few seconds against the game, so quests you finish by hand leave the list too.");

        // ── search / add ──
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##prisearch", "Search quests by name or id…", ref _prioritySearch, 80);
        var query = _prioritySearch.Trim();
        if (query.Length >= 2)
        {
            var results = ushort.TryParse(query, out var id) && _catalog.ById(id) is { } byId
                ? [byId]
                : _catalog.Search(query, 12).ToList();
            if (results.Count == 0)
                ImGui.TextColored(OdysseusTheme.TextDisabled, "No quest matches.");
            foreach (var r in results)
            {
                var already = _priority.Contains(r.QuestId);
                using (ImRaii.Disabled(already))
                {
                    if (OdysseusTheme.IconButton($"add{r.QuestId}", FontAwesomeIcon.Plus, already ? "Already listed" : "Add to priority", new Vector2(24, 22)))
                    {
                        _priority.Add(r.QuestId);
                        _prioritySearch = string.Empty;
                    }
                }
                ImGui.SameLine();
                ImGui.TextColored(OdysseusTheme.TextPrimary, r.Name);
                ImGui.SameLine();
                ImGui.TextColored(OdysseusTheme.TextDisabled, $"#{r.QuestId} · Lv {r.ClassJobLevel}{(r.IsMainScenario ? " · MSQ" : "")}{(_pathStore.Has(r.QuestId) ? "" : " · no path")}");
            }
        }

        // ── the list ──
        OdysseusTheme.SectionHeader($"LIST ({_priority.Count})");
        var entries = _priority.Entries(_priorityWorld);
        if (entries.Count == 0)
        {
            ImGui.TextColored(OdysseusTheme.TextDisabled, "Empty — the story runs on its own.");
        }
        else
        {
            var next = _priority.NextReady(_priorityWorld);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var isNext = e.QuestId == next;
                using (ImRaii.Disabled(i == 0))
                {
                    if (OdysseusTheme.IconButton($"up{e.QuestId}", FontAwesomeIcon.ArrowUp, "Move up", new Vector2(24, 22))) _priority.Move(e.QuestId, -1);
                }
                ImGui.SameLine(0f, 2f);
                using (ImRaii.Disabled(i == entries.Count - 1))
                {
                    if (OdysseusTheme.IconButton($"dn{e.QuestId}", FontAwesomeIcon.ArrowDown, "Move down", new Vector2(24, 22))) _priority.Move(e.QuestId, +1);
                }
                ImGui.SameLine(0f, 2f);
                if (OdysseusTheme.IconButton($"rm{e.QuestId}", FontAwesomeIcon.Trash, "Remove", new Vector2(24, 22))) _priority.Remove(e.QuestId);
                ImGui.SameLine(0f, 8f);
                var color = e.Status switch
                {
                    PriorityStatus.Ready or PriorityStatus.Accepted => OdysseusTheme.TextPrimary,
                    PriorityStatus.Complete => OdysseusTheme.TextDisabled,
                    _ => OdysseusTheme.TextSecondary,
                };
                ImGui.TextColored(isNext ? OdysseusTheme.WakeFoam : color, (isNext ? "▸ " : "") + e.Name);
                ImGui.SameLine();
                var detailColor = e.Status switch
                {
                    PriorityStatus.Ready => OdysseusTheme.StatusGreen,
                    PriorityStatus.Accepted => OdysseusTheme.WakeFoam,
                    PriorityStatus.Complete => OdysseusTheme.TextDisabled,
                    PriorityStatus.NoPath or PriorityStatus.UnknownQuest => OdysseusTheme.StatusYellow,
                    _ => OdysseusTheme.TextDisabled,
                };
                ImGui.TextColored(detailColor, $"· #{e.QuestId} · {e.Detail}");
            }
            ImGui.Spacing();
            if (ImGui.SmallButton("Clear list"))
                _priority.Clear();
        }
    }

    private void DrawPathsSection()
    {
        OdysseusTheme.SectionHeader("QUEST PATHS");
        ImGui.TextWrapped(
            "Odysseus converts the quest paths already installed on this machine into its own format, " +
            "once, and runs from the converted copy. Nothing is downloaded and nothing leaves this PC.");
        ImGui.Spacing();

        ImGui.TextColored(OdysseusTheme.TextSecondary, $"{_pathStore.Count} quest paths stored in {_pathStore.Directory}");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##bundle", ref _bundlePath, 512);
        var exists = File.Exists(_bundlePath);
        if (!exists)
            ImGui.TextColored(OdysseusTheme.StatusYellow, "Bundle not found at that path.");
        if (_bundlePath != _defaultBundlePath && ImGui.SmallButton("Use default location"))
            _bundlePath = _defaultBundlePath;

        ImGui.Spacing();
        using (ImRaii.Disabled(!exists))
        {
            if (ImGui.Button("Import Main Scenario only"))
                RunImport(folder => folder.Contains("/MSQ", StringComparison.OrdinalIgnoreCase));
            ImGui.SameLine();
            if (ImGui.Button("Import everything"))
                RunImport(null);
        }
        OdysseusTheme.HelpMarker(
            "MSQ only is ~1,000 quests and is all v1 runs. Everything is ~4,700; harmless, just more files.");

        if (_importStatus.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_importStatus);
        }
    }

    private void RunImport(Func<string, bool>? filter)
    {
        try
        {
            var report = _pathStore.ImportBundle(_bundlePath, filter);
            _importStatus = report.ToString();
            if (report.Errors.Count > 0)
                _importStatus += "\nFirst errors: " + string.Join("; ", report.Errors.GetRange(0, Math.Min(3, report.Errors.Count)));
        }
        catch (Exception ex)
        {
            _importStatus = $"Import failed: {ex.Message}";
        }
    }

    private void DrawHandoffsSection()
    {
        OdysseusTheme.SectionHeader("HANDOFFS");
        ImGui.TextWrapped(
            "Instanced content inside a quest is handed to the plugin that already does it. " +
            "With a handoff off, Odysseus walks to the entrance and waits for you.");
        ImGui.Spacing();

        var solo = _config.HandOffSoloDuties;
        if (ImGui.Checkbox("Solo duties → BossMod Reborn", ref solo))
        {
            _config.HandOffSoloDuties = solo;
            _save();
        }
        if (!_presence.BossMod)
        {
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.StatusYellow, "(BossMod not loaded)");
        }

        var duties = _config.HandOffDutiesToTheseus;
        if (ImGui.Checkbox("Dungeons and trials → Theseus", ref duties))
        {
            _config.HandOffDutiesToTheseus = duties;
            _save();
        }
        if (!_presence.Theseus)
        {
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.StatusYellow, "(Theseus not loaded)");
        }
    }

    private void DrawWakeSection()
    {
        OdysseusTheme.SectionHeader("THE WAKE", OdysseusTheme.WakeFoam);
        ImGui.TextWrapped(
            "Quest, sequence and the quest's own progress variables are read straight from the game, " +
            "so an interrupted quest picks up where the game says it stopped — after a crash, a " +
            "logout or a reload. Nothing saved locally is trusted over that.");
        ImGui.Spacing();

        var resume = _config.EnableResume;
        if (ImGui.Checkbox("Resume interrupted quests", ref resume))
        {
            _config.EnableResume = resume;
            _save();
        }

        using (ImRaii.Disabled(!resume))
        {
            var confirm = _config.ConfirmBeforeResume;
            if (ImGui.Checkbox("Ask before resuming", ref confirm))
            {
                _config.ConfirmBeforeResume = confirm;
                _save();
            }
        }
    }

    private void DrawFleetSection()
    {
        OdysseusTheme.SectionHeader("FLEET DASHBOARD");
        ImGui.TextWrapped(
            "Read-only. Each box publishes where it is in the MSQ over the Daedalus relay; the " +
            "dashboard shows every box at once. Nothing on the wire changes what this box does.");
        ImGui.Spacing();

        var publish = _config.PublishFleetStatus;
        if (ImGui.Checkbox("Publish this character's status", ref publish))
        {
            _config.PublishFleetStatus = publish;
            _save();
        }
        if (!_presence.Daedalus)
        {
            ImGui.SameLine();
            ImGui.TextColored(OdysseusTheme.StatusYellow, "(Daedalus not loaded — no relay)");
        }

        var stale = _config.PeerStaleSeconds;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Peer stale after (s)", ref stale, 3f, 60f, "%.0f"))
        {
            _config.PeerStaleSeconds = stale;
            _save();
        }
    }

    private void DrawDebugSection()
    {
        OdysseusTheme.SectionHeader("LOOK");

        var themeNames = new[] { "Day — light blue", "Dusk — slate" };
        var theme = (int)_config.Theme;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Theme", ref theme, themeNames, themeNames.Length))
        {
            _config.Theme = (ThemeMode)theme;
            _save();
        }

        var compact = _config.CompactMode;
        if (ImGui.Checkbox("Compact main window", ref compact))
        {
            _config.CompactMode = compact;
            _save();
        }
        OdysseusTheme.HelpMarker("State, quest, step, progress and one control row — nothing else. The ▣/▢ button on the main window toggles it too.");

        OdysseusTheme.SectionHeader("DEBUG");

        var debug = _config.DebugMode;
        if (ImGui.Checkbox("Debug mode", ref debug))
        {
            _config.DebugMode = debug;
            _save();
        }
        OdysseusTheme.HelpMarker("Verbose step logging and the debug window (/odysseus debug).");
    }
}
