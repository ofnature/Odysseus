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
    private readonly System.Collections.Generic.Queue<byte> _tribeQueue = new();
    private readonly TribesWindow _tribesWindow;
    private readonly Services.Deliveries.DeliveryCatalog _deliveries;
    private readonly DeliveriesWindow _deliveriesWindow;
    private System.DateTime _lastPrune;
    private readonly RunLog _runLog;
    private readonly FleetPublisher _fleet;
    private readonly OdysseusIpc _ipc;
    private readonly ConfigWindow _configWindow;
    private readonly MainWindow _mainWindow;
    private readonly DebugWindow _debugWindow;
    private readonly FleetWindow _fleetWindow;
    private readonly PathEditorWindow _pathEditorWindow;
    private readonly LogWindow _logWindow;
    private readonly PathRecorder _recorder = new();
    private readonly RecorderFeed _recorderFeed;

    public OdysseusPlugin(IDalamudPluginInterface pluginInterface)
    {
        // Create<T> constructs a T. It must be given the service holder, never this plugin —
        // see Service for why that distinction silently kills the client.
        pluginInterface.Create<Service>();

        // Everything below is wrapped so a construction failure lands in a file we control —
        // Dalamud's own log stops writing once it hits its size cap, and "failed to load" is all
        // the installer says.
        try
        {
        _config = PluginInterface.GetPluginConfig() as OdysseusConfig ?? new OdysseusConfig();
        OdysseusTheme.SetMode(_config.Theme);
        _presence = new PluginPresence(PluginInterface);
        _quests = new QuestStateReader(fault => Log.Warning($"Quest state read failed. {fault}"));
        _catalog = new QuestCatalog(DataManager, message => Log.Warning(message));

        _pathStore = new PathStore(
            System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "paths"),
            message => Log.Information(message));

        var aetherytes = new Services.Travel.AetheryteCatalog(DataManager, message => Log.Warning(message));
        var duties = new DutyCatalog(DataManager, message => Log.Warning(message));
        _world = new GameStepWorld(
            ClientState, ObjectTable, Condition, GameGui, TargetManager, DataManager,
            new VnavIpc(PluginInterface, message => Log.Warning(message)),
            new DaedalusIpc(PluginInterface, message => Log.Warning(message)),
            new TextAdvanceIpc(PluginInterface, () => _config.PickQuestRewards, message => Log.Warning(message)),
            new LifestreamIpc(PluginInterface, message => Log.Warning(message)),
            aetherytes,
            new TheseusIpc(PluginInterface, message => Log.Warning(message)),
            new ChatCommandSender(message => Log.Warning(message)),
            duties,
            _quests, message => Log.Information(message));
        _recorderFeed = new RecorderFeed(_world, _quests, aetherytes, duties);
        var dialogue = new DialogueCatalog(DataManager, message => Log.Warning(message));
        _runLog = new RunLog(
            System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "runlog.jsonl"),
            message => Log.Warning(message));
        _frontier = new StoryFrontier(_quests, _catalog, () => _config.PreferredGrandCompany);
        _controller = new QuestController(_quests, _pathStore, new StepExecutor(_world, dialogue), _world, _world, _config,
            _frontier.Next,
            id => _catalog.ById(id)?.ClassJobLevel ?? 0,
            _runLog, message => Log.Information(message));

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

        _tribes = new Services.Tribes.TribeCatalog(DataManager, message => Log.Warning(message));
        _tribeState = new Services.Tribes.TribeState(_tribes, message => Log.Warning(message));
        _tribeRunner = new Services.Tribes.TribeRunner(_world, _tribeState, _controller,
            new StepExecutor(_world, dialogue), message => Log.Information(message));

        // Published once the controller exists, so the gate never reports on a half-built run.
        _ipc = new OdysseusIpc(PluginInterface, () => _config.Enabled && _controller.State.IsDriving());

        _fleet = new FleetPublisher(
            new RelayIpc(PluginInterface, message => Log.Warning(message)),
            BuildFleetStatus,
            () => _config.PublishFleetStatus);

        _configWindow = new ConfigWindow(_config, SaveConfig, _presence, _pathStore,
            QuestionableImporter.DefaultBundlePath(PluginInterface.ConfigDirectory.Parent?.FullName ?? string.Empty),
            _priority, _priorityWorld, _catalog);
        _pathEditorWindow = new PathEditorWindow(
            _pathStore, _catalog, _controller, _recorder,
            () => ClientState.TerritoryType,
            () => ObjectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero,
            () => TargetManager.Target?.BaseId);
        _logWindow = new LogWindow(_runLog);
        _debugWindow = new DebugWindow(_quests, _catalog);
        _fleetWindow = new FleetWindow(_config, _fleet);
        _mainWindow = new MainWindow(new MainWindowDeps(
            _config, SaveConfig, _presence, _quests, _catalog, _pathStore, _controller, _frontier, _fleet, _priority, _priorityWorld,
            OpenConfig,
            () => _fleetWindow.IsOpen = true,
            () => _logWindow.IsOpen = true,
            () => _debugWindow.IsOpen = true,
            questId => _pathEditorWindow.Open(questId),
            () => TargetManager.Target?.BaseId,
            () => TargetManager.Target?.Position,
            () => ObjectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero,
            () => ClientState.TerritoryType,
            () => ObjectTable.LocalPlayer?.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "—"));

        var unlockPlanner = new UnlockPlanner(_catalog, _quests, _priority, _pathStore.Has, message => Log.Information(message));
        _tribesWindow = new TribesWindow(_config, _tribes, _tribeState, _tribeRunner, unlockPlanner, new GameIcons(TextureProvider),
            id => { if (!_tribeQueue.Contains(id)) _tribeQueue.Enqueue(id); },
            () => { _tribeQueue.Clear(); _tribeRunner.Stop(); });
        _deliveries = new Services.Deliveries.DeliveryCatalog(DataManager, message => Log.Warning(message));
        var deliveryState = new Services.Deliveries.DeliveryState(_quests, DataManager, message => Log.Warning(message));
        var deliveryBonus = new Services.Deliveries.DeliveryBonus(DataManager, message => Log.Warning(message));
        var deliveryRewards = new Services.Deliveries.DeliveryRewards(DataManager, message => Log.Warning(message));
        var scrips = new Services.Deliveries.ScripLedger(DataManager, new Services.Deliveries.InventoryCurrencyReader(),
            _deliveries, deliveryState, deliveryRewards, deliveryBonus, message => Log.Warning(message));
        _deliveriesWindow = new DeliveriesWindow(_deliveries, deliveryState, deliveryBonus, scrips,
            new ArtisanIpc(PluginInterface, message => Log.Warning(message)), unlockPlanner);

        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_tribesWindow);
        _windowSystem.AddWindow(_deliveriesWindow);
        _windowSystem.AddWindow(_debugWindow);
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

    public void Dispose()
    {
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

    private void OnFrameworkUpdate(IFramework framework)
    {
        // The dashboard is useful even with the runner off: it says who is where.
        _fleet.Tick();

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

        // Tribe dailies own the controller while a run is active; otherwise the MSQ controller does.
        if (_tribeRunner.State is not (Services.Tribes.TribeRunState.Idle or Services.Tribes.TribeRunState.Done or Services.Tribes.TribeRunState.Faulted))
        {
            _tribeRunner.Tick();
            return;
        }
        if (_tribeQueue.Count > 0 && _controller.State == RunState.Idle)
        {
            var next = _tribeQueue.Peek();
            if (_tribes.ById(next) is { } tribe && _tribeRunner.Start(tribe))
            {
                _tribeQueue.Dequeue();
                return;
            }
            _tribeQueue.Dequeue(); // could not start (nothing left / not unlocked) — drop and move on
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
            case "fleet":
                _fleetWindow.IsOpen = true;
                break;
            case "tribes":
                _tribesWindow.IsOpen = true;
                break;
            case "deliveries":
                _deliveriesWindow.IsOpen = true;
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
}
