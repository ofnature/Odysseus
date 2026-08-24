using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Odysseus.Config;
using Odysseus.Services.Fleet;
using Odysseus.Services.Ipc;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;
using Odysseus.Windows;
using static Odysseus.Service;

namespace Odysseus;

/// <summary>
/// Odysseus — automated Main Scenario questing.
///
/// <para>
/// He took the long way home and it took ten years. This plugin walks the Main Scenario the same
/// way — quest by quest, zone by zone — and treats the wake behind the ship as the feature that
/// matters: an interrupted quest resumes where the game says it stopped.
/// </para>
///
/// <para>Design and phased scope live in <c>odysseus-plan.md</c> at the repo root (local-only).</para>
/// </summary>
public sealed class OdysseusPlugin : IDalamudPlugin
{
    private const string CommandMain = "/odysseus";
    private const string CommandShort = "/od";

    public static string PluginVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly WindowSystem _windowSystem = new("Odysseus");
    private readonly OdysseusConfig _config;
    private readonly PluginPresence _presence;
    private readonly IQuestStateReader _quests;
    private readonly QuestCatalog _catalog;
    private readonly PathStore _pathStore;
    private readonly GameStepWorld _world;
    private readonly QuestController _controller;
    private readonly StoryFrontier _frontier;
    private readonly PriorityList _priority;
    private readonly PriorityWorld _priorityWorld;
    private readonly Services.Tribes.TribeCatalog _tribes;
    private readonly Services.Tribes.TribeState _tribeState;
    private readonly Services.Tribes.TribeRunner _tribeRunner;
    private readonly Services.Deliveries.DeliveryRunner _deliveryRunner;
    private readonly Services.Deliveries.SpendRunner _spender;
    private readonly RewardLedger _rewardLedger;
    private readonly RewardSeller _seller;
    private readonly GameChestWorld _chestWorld;
    private readonly ChestWithdrawer _withdrawer;
    private readonly Services.Flight.CurrentCollector _collector;
    private readonly FlightWindow _flightWindow;
    private readonly JournalWindow _journalWindow;
    private readonly System.Collections.Generic.Queue<byte> _tribeQueue = new();
    private System.DateTime _lastQueueNote;
    private readonly TribesWindow _tribesWindow;
    private readonly Services.Deliveries.DeliveryCatalog _deliveries;
    private readonly DeliveriesWindow _deliveriesWindow;
    private System.DateTime _lastPrune;
    private readonly RunLog _runLog;
    private readonly FleetPublisher _fleet;
    private readonly Services.Run.ChocoboKeeper _chocobo;
    private readonly OdysseusIpc _ipc;
    private readonly ConfigWindow _configWindow;
    private readonly MainWindow _mainWindow;
    private readonly DebugWindow _debugWindow;
#if DEBUG
    private readonly Services.Gathering.GameGatherWorld _gatherWorld;
    private readonly Services.Gathering.IOwnGatherer _ownGatherer;

    // The work-list bench: debug builds only, so a release carries no half-finished feature.
    private readonly Services.Work.WorkList _workList = new();
    private readonly Services.Work.WorkRunner _workRunner;
    private readonly WorkbenchWindow _workbenchWindow;
#endif
    private readonly FleetWindow _fleetWindow;
    private readonly PathEditorWindow _pathEditorWindow;
    private readonly LogWindow _logWindow;
    private readonly PathRecorder _recorder = new();
    private readonly Services.Run.DialogueStagehand _stagehand = new();
    private readonly RecorderFeed _recorderFeed;

    public OdysseusPlugin(IDalamudPluginInterface pluginInterface)
    {
        // Create<T> constructs a T. It must be given the service holder, never this plugin —
        // see Service for why that distinction silently kills the client.
        pluginInterface.Create<Service>();
        OpenOwnLog(); // needs the services above; before them it is a load error

        // Everything below is wrapped so a construction failure lands in a file we control —
        // Dalamud's own log stops writing once it hits its size cap, and "failed to load" is all
        // the installer says.
        try
        {
        _config = PluginInterface.GetPluginConfig() as OdysseusConfig ?? new OdysseusConfig();
        OdysseusTheme.SetMode(_config.Theme);
        _presence = new PluginPresence(PluginInterface);
        _quests = new QuestStateReader(fault => Log.Warning($"Quest state read failed. {fault}"));
        _catalog = new QuestCatalog(DataManager, message => Warn(message));

        // Several installs can share one folder — that is the multi-account answer, and it needs
        // nobody to redistribute anybody's data. A pack put beside the DLL by hand is still read
        // under it; none is shipped.
        _pathStore = new PathStore(
            _config.PathsDirectory.Length > 0
                ? _config.PathsDirectory
                : System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "paths"),
            message => Say(message),
            Services.Paths.PathPack.ShippedPath(PluginInterface.AssemblyLocation.DirectoryName));

        var aetherytes = new Services.Travel.AetheryteCatalog(DataManager, message => Warn(message));
        var duties = new DutyCatalog(DataManager, message => Warn(message));
        // Built here rather than with the rest of the delivery services: the step world buys through
        // the shop half for PurchaseItem steps and hands Craft/Gather steps to the same Artisan and
        // GatherBuddy the deliveries use, so all of it has to exist first.
        var deliveryWorld = new Services.Deliveries.GameDeliveryWorld(DataManager, message => Warn(message));
        var artisan = new ArtisanIpc(PluginInterface, message => Warn(message));
        var gatherBuddy = new GatherBuddyIpc(PluginInterface, message => Warn(message));
        var recipes = new Services.Deliveries.RecipeLookup(DataManager, message => Warn(message));
        var ingredients = new Services.Deliveries.IngredientSource(DataManager, message => Warn(message));
        var making = new ItemMaking(artisan, gatherBuddy, recipes, ingredients,
            () => _config.DeliveryCraftJob, () => deliveryWorld.CurrentCraftType, id => deliveryWorld.ItemCount(id));
        _world = new GameStepWorld(
            ClientState, ObjectTable, Condition, GameGui, TargetManager, DataManager,
            new VnavIpc(PluginInterface, message => Warn(message)),
            new DaedalusIpc(PluginInterface, message => Warn(message)),
            new TextAdvanceIpc(PluginInterface, () => _config.PickQuestRewards, message => Warn(message)),
            new LifestreamIpc(PluginInterface, message => Warn(message)),
            aetherytes,
            new TheseusIpc(PluginInterface, message => Warn(message)),
            new ChatCommandSender(message => Warn(message)),
            duties,
            _quests, deliveryWorld, making, message => Say(message));
        _recorderFeed = new RecorderFeed(_world, _quests, aetherytes, duties);
        var dialogue = new DialogueCatalog(DataManager, message => Warn(message));
        _runLog = new RunLog(
            System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "runlog.jsonl"),
            message => Warn(message));
        _frontier = new StoryFrontier(_quests, _catalog, () => _config.PreferredGrandCompany);
        _controller = new QuestController(_quests, _pathStore, new StepExecutor(_world, dialogue, () => _config.AcceptRewardOvercap), _world, _world, _config,
            _frontier.Next,
            id => _catalog.ById(id)?.ClassJobLevel ?? 0,
            _runLog, message => Say(message),
            // Lets a purchase step see whether the craft it feeds is already made.
            itemId => making.Ingredients(itemId, 1).Select(i => i.ItemId).ToList(),
            // Custom delivery unlocks can only be taken as a crafter or gatherer.
            id => _catalog.ById(id)?.NeedsHandOrLand ?? false,
            id => _catalog.ById(id)?.NeedsCombat ?? false);

        // Priority list: saved in config only while the persist toggle is on.
        _priority = new PriorityList(_catalog, _config.PriorityQuests, _config.PersistPriorityList, ids =>
        {
            _config.PriorityQuests = new System.Collections.Generic.List<ushort>(ids);
            PluginInterface.SavePluginConfig(_config);
        })
        { AutoRemoveCompleted = _config.AutoRemoveCompletedPriority };
        _priorityWorld = new PriorityWorld(_quests, _pathStore, () => _world.PlayerLevel);
        _controller.PriorityNext = () => _priority.NextReady(_priorityWorld);
        _controller.StoryCurrent = () => _frontier.Current()?.QuestId;

        // Reward sweep. The ledger measures across the hand-in — counted as the CompleteQuest step
        // begins and again when the quest is confirmed complete — so only what the quest actually
        // added is ever offered to a vendor.
        var rewards = new QuestRewards(DataManager, message => Warn(message));
        _rewardLedger = new RewardLedger(_config.PendingRewardSales);
        _controller.QuestCompleting += id => _rewardLedger.Before(id, rewards.Candidates(id), itemId => deliveryWorld.ItemCount(itemId));
        _controller.QuestCompleted += id =>
        {
            var gained = _rewardLedger.After(id, itemId => deliveryWorld.ItemCount(itemId));
            if (gained.Count == 0) return;
            Log.Information($"Quest {id} rewards banked for selling: " +
                            string.Join(", ", gained.Select(g => $"{g.Quantity} × {g.ItemId}")));
            SaveRewardLedger();
        };
        _seller = new RewardSeller(
            new GameSellWorld(DataManager, () => deliveryWorld.IsShopOpen(0), itemId => deliveryWorld.ItemCount(itemId),
                message => Say(message)),
            _rewardLedger, () => _config.SellQuestRewards, SaveRewardLedger);

        // Fetching from the FC chest. Manual only, and gated on the chest window being open — that
        // window is the transfer session, so there is no version of this that works from anywhere.
        _chestWorld = new GameChestWorld(GameGui, DataManager, id => deliveryWorld.ItemCount(id), message => Say(message));
        _withdrawer = new ChestWithdrawer(_chestWorld);

        _tribes = new Services.Tribes.TribeCatalog(DataManager, message => Warn(message));
        _tribeState = new Services.Tribes.TribeState(_tribes, message => Warn(message));
        _tribeRunner = new Services.Tribes.TribeRunner(_world, _tribeState, _controller,
            new StepExecutor(_world, dialogue, () => _config.AcceptRewardOvercap), message => Say(message));

        // Published once the controller exists, so the gate never reports on a half-built run.
        _ipc = new OdysseusIpc(PluginInterface, () => _config.Enabled && _controller.State.IsDriving());

        _chocobo = new Services.Run.ChocoboKeeper(_world, () => _config.KeepChocoboOut, ChocoboUnlocked);

        _fleet = new FleetPublisher(
            new RelayIpc(PluginInterface, message => Warn(message)),
            BuildFleetStatus,
            () => _config.PublishFleetStatus);

        _configWindow = new ConfigWindow(_config, SaveConfig, _presence, _pathStore,
            QuestionableImporter.DefaultBundlePath(PluginInterface.ConfigDirectory.Parent?.FullName ?? string.Empty),
            Services.Paths.PathPack.SourceAssetPath(PluginInterface.AssemblyLocation.DirectoryName),
            _priority, _priorityWorld, _catalog,
            () => _rewardLedger.Pending.Sum(p => p.Quantity));
        _pathEditorWindow = new PathEditorWindow(
            _pathStore, _catalog, _controller, _recorder,
            () => ClientState.TerritoryType,
            () => ObjectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero,
            () => TargetManager.Target?.BaseId);
        _logWindow = new LogWindow(_runLog);
        _debugWindow = new DebugWindow(_quests, _catalog);
        _fleetWindow = new FleetWindow(_config, _fleet);

        var unlockPlanner = new UnlockPlanner(_catalog, _quests, _priority, _pathStore.Has, message => Say(message));
        _tribesWindow = new TribesWindow(_config, _tribes, _tribeState, _tribeRunner, unlockPlanner, new GameIcons(TextureProvider),
            id => { if (!_tribeQueue.Contains(id)) _tribeQueue.Enqueue(id); },
            () => { _tribeQueue.Clear(); _tribeRunner.Stop(); });
        _deliveries = new Services.Deliveries.DeliveryCatalog(DataManager, message => Warn(message));
        var deliveryState = new Services.Deliveries.DeliveryState(_quests, DataManager, message => Warn(message));
        var deliveryBonus = new Services.Deliveries.DeliveryBonus(DataManager, message => Warn(message));
        var deliveryRewards = new Services.Deliveries.DeliveryRewards(DataManager, message => Warn(message));
        var scrips = new Services.Deliveries.ScripLedger(DataManager, new Services.Deliveries.InventoryCurrencyReader(),
            _deliveries, deliveryState, deliveryRewards, deliveryBonus, message => Warn(message));
        var deliveryRequests = new Services.Deliveries.DeliveryRequests(DataManager, message => Warn(message), deliveryBonus);
        var gatheringSource = new Services.Deliveries.GatheringSource(DataManager, message => Warn(message));

#if DEBUG
        // Our own gathering, offered to the delivery runner in place of the GatherBuddy handoff.
        // Debug only until it has worked once: without it the gather route behaves exactly as it
        // does today, which is to stop and say what it needs.
        _gatherWorld = new Services.Gathering.GameGatherWorld(
            ClientState, ObjectTable, Condition, GameGui, _world, message => Say(message));
        // Probe by default. Opening a node has locked this client up more than once and the cause
        // is not yet understood, so the interaction is something you turn on deliberately in the
        // Workbench, not something a delivery does to you.
        _ownGatherer = new Services.Gathering.OwnGatherer(
            new Services.Gathering.GatherRunner(_gatherWorld, new StepExecutor(_world, dialogue, () => _config.AcceptRewardOvercap)),
            gatheringSource,
            new Services.Gathering.NodeAtlas(
                Services.Gathering.NodeAtlas.PathBeside(PluginInterface.AssemblyLocation.DirectoryName),
                message => Warn(message)),
            message => Say(message));
#endif

        _deliveryRunner = new Services.Deliveries.DeliveryRunner(
            _world,
            deliveryWorld,
            deliveryState,
            deliveryRequests,
            scrips,
            artisan,
            recipes,
            ingredients,
            gatheringSource,
            gatherBuddy,
            // Never through the overcap warning here: the delivery runner plans its scrip payouts
            // and stops short of the cap on purpose — a Yes at this window throws scrips away.
            new StepExecutor(_world, dialogue, () => false),
            () => _config.DeliveryCraftJob,
            message => Say(message)
#if DEBUG
            , _ownGatherer
#endif
            );
#if DEBUG
        // After the catalogues and both runners exist — it takes all four, and a debug-only window
        // is exactly the sort of thing that gets built too early and hands itself nulls.
        _workRunner = new Services.Work.WorkRunner(
            new WorkEngines(_tribes, _deliveries, _tribeRunner, _deliveryRunner),
            message => Say(message));
        _ownGatherer.ProbeOnly = true;
        _workbenchWindow = new WorkbenchWindow(_tribes, _deliveries, _workList, _workRunner, _ownGatherer, _gatherWorld);
#endif

        var scripShop = new Services.Deliveries.ScripShop(DataManager, scrips.Kinds, message => Warn(message));
        var spending = new Services.Deliveries.SpendPlanner(scripShop, scrips, id => deliveryWorld.ItemCount(id));
        _spender = new Services.Deliveries.SpendRunner(deliveryWorld, message => Say(message));
        _deliveriesWindow = new DeliveriesWindow(_deliveries, deliveryState, deliveryBonus, scrips,
            artisan, unlockPlanner, _deliveryRunner, deliveryRequests, _config, SaveConfig, gatherBuddy,
            scripShop, spending, _spender);

        var currents = new Services.Flight.AetherCurrentCatalog(DataManager, _pathStore, message => Warn(message));
        var flightState = new Services.Flight.FlightState(message => Warn(message));
        _collector = new Services.Flight.CurrentCollector(_world, flightState,
            new StepExecutor(_world, dialogue, () => _config.AcceptRewardOvercap), message => Say(message));
        _flightWindow = new FlightWindow(currents, flightState, _collector, _priority, _catalog, unlockPlanner,
            () => ClientState.TerritoryType);

        // The bill of materials for a queued line: its own steps, read against the bags and the FC
        // chest, with crafts expanded through the same recipe and ingredient lookups the deliveries
        // use. Assembled here so the window stays presentation and the maths stays testable.
        var itemNames = new Dictionary<uint, string>();
        string ItemName(uint id)
        {
            if (itemNames.TryGetValue(id, out var cached)) return cached;
            var name = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(id)?.Name.ExtractText();
            return itemNames[id] = string.IsNullOrEmpty(name) ? $"item {id}" : name;
        }
        IReadOnlyList<MaterialNeed> Bill(IEnumerable<Services.Paths.QuestPath> paths, bool inStepOrder)
            => ChainMaterials.For(paths, ItemName, id => deliveryWorld.ItemCount(id),
                _world.FreeCompanyChestCount, making.Ingredients, inStepOrder);

        _journalWindow = new JournalWindow(_catalog, _quests, unlockPlanner, _priority, _pathStore.Has,
            questIds => Bill(questIds.Select(_pathStore.ForQuest).OfType<Services.Paths.QuestPath>(), inStepOrder: false),
            questId => _pathStore.ForQuest(questId) is { } path ? Bill([path], inStepOrder: true) : [],
            questId => _pathStore.ForQuest(questId) is { } path && ChainMaterials.NamesItems(path),
            () => _pathStore.OutdatedCount,
            () => _chestWorld.ChestOpen,
            needs => _withdrawer.Start(needs) is var queued && queued > 0
                ? $"Fetching {queued} item(s) from the FC chest — keep it open."
                : _withdrawer.Status);

        // Built after the windows it can open: the deps record captures them, and the
        // nullable analysis is right that a field assigned later is null at this point.
        _mainWindow = new MainWindow(new MainWindowDeps(
            _config, SaveConfig, _presence, _quests, _catalog, _pathStore, _controller, _frontier, _fleet, _priority, _priorityWorld,
            OpenConfig,
            () => _fleetWindow.IsOpen = true,
            () => _logWindow.IsOpen = true,
            () => _debugWindow.IsOpen = true,
            () => _tribesWindow.IsOpen = true,
            () => _deliveriesWindow.IsOpen = true,
            () => _journalWindow.IsOpen = true,
            questId => _pathEditorWindow.Open(questId),
            () => TargetManager.Target?.BaseId,
            () => TargetManager.Target?.Position,
            () => ObjectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero,
            () => ClientState.TerritoryType,
            () => ObjectTable.LocalPlayer?.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "—"));

        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_tribesWindow);
        _windowSystem.AddWindow(_deliveriesWindow);
        _windowSystem.AddWindow(_flightWindow);
        _windowSystem.AddWindow(_journalWindow);
        _windowSystem.AddWindow(_debugWindow);
#if DEBUG
        _windowSystem.AddWindow(_workbenchWindow);
#endif
        _windowSystem.AddWindow(_fleetWindow);
        _windowSystem.AddWindow(_pathEditorWindow);
        _windowSystem.AddWindow(_logWindow);

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        Framework.Update += OnFrameworkUpdate;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Odysseus. \"/odysseus config\" settings, \"/odysseus tribes\" allied societies, \"/odysseus deliveries\" custom deliveries, \"/odysseus fleet\" dashboard, \"/odysseus log\" step log, \"/odysseus paths\" step editor, \"/odysseus debug\" quest-state dump, \"/odysseus stop\" stops the run.",
        });
        CommandManager.AddHandler(CommandShort, new CommandInfo(OnCommand)
        {
            HelpMessage = "Short alias for /odysseus.",
        });

        Log.Information($"Odysseus v{PluginVersion} loaded.");
        }
        catch (System.Exception ex)
        {
            WriteStartupError(pluginInterface, ex);
            throw;
        }
    }

    /// <summary>Full exception to <c>startup-error.txt</c> in the plugin's config directory, and to the log.</summary>
    private static void WriteStartupError(IDalamudPluginInterface pluginInterface, System.Exception ex)
    {
        try
        {
            Log.Error(ex, "Odysseus failed to construct.");
            var dir = pluginInterface.ConfigDirectory.FullName;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "startup-error.txt"),
                $"{System.DateTime.Now:O}\nOdysseus v{PluginVersion}\n\n{ex}\n");
        }
        catch
        {
            // Nothing left to report to.
        }
    }

    // ── Odysseus's own log file ──
    // Dalamud's log stops writing at its size cap (100 MB on the korha client, mid-afternoon),
    // and a run's own narration is the one thing worth having when something goes wrong. So it
    // also goes to odysseus.log beside the config, trimmed at start when it grows past a few MB.
    private StreamWriter? _ownLog;

    private void Say(string message) { Log.Information(message); WriteOwn("INF", message); }
    private void Warn(string message) { Log.Warning(message); WriteOwn("WRN", message); }

    private void OpenOwnLog()
    {
        try
        {
            var file = System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "odysseus.log");
            if (File.Exists(file) && new FileInfo(file).Length > 8_000_000)
                File.Move(file, System.IO.Path.ChangeExtension(file, ".old.log"), overwrite: true);
            _ownLog = new StreamWriter(file, append: true) { AutoFlush = true };
            WriteOwn("INF", $"— Odysseus v{typeof(OdysseusPlugin).Assembly.GetName().Version} —");
        }
        catch (Exception ex)
        {
            Log.Warning($"Own log file unavailable ({ex.GetType().Name}: {ex.Message}); Dalamud's log only.");
        }
    }

    private void WriteOwn(string level, string message)
    {
        try { _ownLog?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}"); }
        catch { /* a log line is never worth a fault */ }
    }

    public void Dispose()
    {
        _ownLog?.Dispose();
        CommandManager.RemoveHandler(CommandMain);
        CommandManager.RemoveHandler(CommandShort);

        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;

        _controller.Stop();
        _windowSystem.RemoveAllWindows();
        _fleet.Dispose();
        _ipc.Dispose();
    }

    /// <summary>
    /// "My Little Chocobo" is three quests, one per Grand Company. Nothing to summon before one of
    /// them is done, and which one depends on who you signed with.
    /// </summary>
    private bool ChocoboUnlocked()
    {
        var quest = Services.Run.ChocoboKeeper.UnlockQuestFor(_quests.Character().GrandCompany);
        return quest != 0 && _quests.IsComplete(quest);
    }

    /// <summary>
    /// Say who is eating the frames while something is queued behind them. Five runners take the
    /// frame with a bare return, and a stuck one is invisible: a Run click enqueues, the queue is
    /// never reached, and nothing anywhere says why. Once every ten seconds, this does.
    /// </summary>
    private void NameFrameOwner(string owner)
    {
        if (_tribeQueue.Count == 0) return;
        if (System.DateTime.UtcNow - _lastQueueNote < System.TimeSpan.FromSeconds(10)) return;
        _lastQueueNote = System.DateTime.UtcNow;
        Log.Information($"A society run is queued, but {owner} owns the frame.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // The dashboard is useful even with the runner off: it says who is where.
        _fleet.Tick();

        _chocobo.Tick();

        // Auto-remove completed priority entries — quests finished by hand count too, so poll.
        var now = System.DateTime.UtcNow;
        if (now - _lastPrune > System.TimeSpan.FromSeconds(5))
        {
            _lastPrune = now;
            _priority.AutoRemoveCompleted = _config.AutoRemoveCompletedPriority;
            var pruned = _priority.Prune(_quests.IsComplete);
            if (pruned > 0)
                Log.Information($"Priority list: removed {pruned} completed quest(s).");
        }

        if (_recorder.IsRecording)
        {
            try
            {
                _recorder.Observe(_recorderFeed.Next(_pathEditorWindow.RecordingQuestId));
            }
            catch (System.Exception ex)
            {
                Log.Warning($"Recorder tick failed: {ex.Message}");
            }
        }

        if (!_config.Enabled)
        {
            if (_controller.State.IsDriving())
                _controller.Stop();
            return;
        }

        // The dialogue chores every run needs each frame, whoever owns it below: the subtitle
        // box advanced, the skip-cutscene prompt answered. Built-in, so a client without
        // TextAdvance still talks its way through.
        if (_config.AutoAdvanceDialogue
            && (_controller.State.IsDriving()
                || _tribeRunner.State is not (Services.Tribes.TribeRunState.Idle or Services.Tribes.TribeRunState.Done or Services.Tribes.TribeRunState.Faulted)
                || _deliveryRunner.State is not (Services.Deliveries.DeliveryRunState.Idle or Services.Deliveries.DeliveryRunState.Done
                    or Services.Deliveries.DeliveryRunState.Faulted or Services.Deliveries.DeliveryRunState.Blocked)))
            _stagehand.Tick(_world);

        // Fetching from the chest is something you asked for by pressing a button, so it runs
        // whatever else is going on and owns the frame until it is done.
        if (_withdrawer.Busy) { NameFrameOwner("the chest withdrawal"); _withdrawer.Tick(); return; }

        // Collecting currents owns the frame; it drives its own executor, not the controller.
        if (!_collector.IsFinished) { NameFrameOwner($"the current collector ({_collector.State})"); _collector.Tick(); return; }

        // Spending owns the frame while it runs; it is short and never touches the controller.
        if (!_spender.IsFinished) { NameFrameOwner($"scrip spending ({_spender.State}: {_spender.StatusLine})"); _spender.Tick(); return; }

        // A delivery run owns the frame outright — it never uses the quest controller.
        if (_deliveryRunner.State is not (Services.Deliveries.DeliveryRunState.Idle or Services.Deliveries.DeliveryRunState.Done
            or Services.Deliveries.DeliveryRunState.Faulted or Services.Deliveries.DeliveryRunState.Blocked))
        {
            NameFrameOwner($"the delivery runner ({_deliveryRunner.State}: {_deliveryRunner.StatusLine})");
            _deliveryRunner.Tick();
            return;
        }

        // Tribe dailies own the controller while a run is active; otherwise the MSQ controller does.
        if (_tribeRunner.State is not (Services.Tribes.TribeRunState.Idle or Services.Tribes.TribeRunState.Done or Services.Tribes.TribeRunState.Faulted))
        {
            NameFrameOwner($"the tribe runner ({_tribeRunner.State}: {_tribeRunner.StatusLine})");
            _tribeRunner.Tick();
            return;
        }
        // The reward sweep runs off whatever vendor window happens to be open — a run's own
        // PurchaseItem step, or one you opened yourself. It ticks only once every other runner has
        // declined the frame, and never while a purchase is in flight: both drive the same shop,
        // and a sale landing between our buy and its verification would read as the buy failing.
        if (!_controller.Phase.StartsWith("Shop", System.StringComparison.Ordinal))
            _seller.Tick();

#if DEBUG
        _workRunner.Tick();
#endif

        if (_tribeQueue.Count > 0)
        {
            if (_controller.State != RunState.Idle)
            {
                // Not silently: a queued society waiting on a busy controller looked, from the
                // window, like a click that did nothing.
                if (System.DateTime.UtcNow - _lastQueueNote > System.TimeSpan.FromSeconds(10))
                {
                    _lastQueueNote = System.DateTime.UtcNow;
                    Log.Information($"Tribe queue waiting: the quest controller is {_controller.State} ({_controller.StatusLine}).");
                }
            }
            else
            {
                var next = _tribeQueue.Dequeue();
                if (_tribes.ById(next) is not { } tribe)
                    Log.Information($"Tribe queue: id {next} is not in the catalogue — dropped.");
                else if (_tribeRunner.Start(tribe))
                    return;
                else
                    // The refusal was being discarded with the queue entry, which made every
                    // failure here read as a click that did nothing.
                    Log.Information($"{tribe.Name}: not started — {_tribeRunner.StatusLine}");
            }
        }

        _controller.Tick();
    }

    /// <summary>What the priority list needs from the game, adapted from the pieces we already have.</summary>
    private sealed class PriorityWorld : IPriorityWorld
    {
        private readonly IQuestStateReader _quests;
        private readonly PathStore _paths;
        private readonly System.Func<int> _level;

        public PriorityWorld(IQuestStateReader quests, PathStore paths, System.Func<int> level)
        {
            _quests = quests;
            _paths = paths;
            _level = level;
        }

        public bool IsComplete(ushort questId) => _quests.IsComplete(questId);
        public bool IsAccepted(ushort questId) => _quests.IsAccepted(questId);
        public bool HasPath(ushort questId) => _paths.Has(questId);
        public int PlayerLevel => _level();
        public CharacterFacts Character => _quests.Character();
    }

    /// <summary>This box's line for the fleet, or null before login.</summary>
    private FleetStatus? BuildFleetStatus()
    {
        var player = ObjectTable.LocalPlayer;
        if (player is null)
            return null;

        var name = player.Name.TextValue;
        var world = player.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
        var questId = _controller.QuestId;
        var running = _controller.State != RunState.Idle;
        return new FleetStatus
        {
            SenderId = $"{name}@{world}",
            Character = name,
            World = world,
            Level = player.Level,
            QuestId = running ? questId : (ushort)0,
            QuestName = running ? _catalog.NameOf(questId) : string.Empty,
            Sequence = running ? _controller.Sequence : 0,
            State = _controller.State.ToString(),
            StatusLine = _controller.StatusLine,
            SentUnixMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
            case "settings":
                OpenConfig();
                break;
            case "debug":
                _debugWindow.IsOpen = true;
                break;
#if DEBUG
            case "work":
                _workbenchWindow.IsOpen = true;
                break;
#endif
            case "fleet":
                _fleetWindow.IsOpen = true;
                break;
            case "tribes":
                _tribesWindow.IsOpen = true;
                break;
            case "deliveries":
                _deliveriesWindow.IsOpen = true;
                break;
            case "flight":
            case "currents":
                _flightWindow.IsOpen = true;
                break;
            case "journal":
            case "quests":
                _journalWindow.IsOpen = true;
                break;
            case "log":
                _logWindow.IsOpen = true;
                break;
            case "paths":
            case "edit":
                if (_controller.QuestId != 0) _pathEditorWindow.Open(_controller.QuestId);
                else _pathEditorWindow.IsOpen = true;
                break;
            case "stop":
                _tribeQueue.Clear();
                _tribeRunner.Stop();
                _controller.Stop();
                break;
            default:
                OpenMain();
                break;
        }
    }

    private void OpenMain() => _mainWindow.IsOpen = true;

    private void OpenConfig() => _configWindow.IsOpen = true;

    private void SaveConfig()
    {
        OdysseusTheme.SetMode(_config.Theme);
        PluginInterface.SavePluginConfig(_config);
    }

    /// <summary>Persist what is banked, so a hand-in is still swept up after a restart.</summary>
    private void SaveRewardLedger()
    {
        _config.PendingRewardSales = _rewardLedger.Pending.ToList();
        PluginInterface.SavePluginConfig(_config);
    }
}
