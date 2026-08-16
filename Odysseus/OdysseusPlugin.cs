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

        // Published once the controller exists, so the gate never reports on a half-built run.
        _ipc = new OdysseusIpc(PluginInterface, () => _config.Enabled && _controller.State.IsDriving());

        _fleet = new FleetPublisher(
            new RelayIpc(PluginInterface, message => Log.Warning(message)),
            BuildFleetStatus,
            () => _config.PublishFleetStatus);

        _configWindow = new ConfigWindow(_config, SaveConfig, _presence, _pathStore,
            QuestionableImporter.DefaultBundlePath(PluginInterface.ConfigDirectory.Parent?.FullName ?? string.Empty));
        _pathEditorWindow = new PathEditorWindow(
            _pathStore, _catalog, _controller, _recorder,
            () => ClientState.TerritoryType,
            () => ObjectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero,
            () => TargetManager.Target?.BaseId);
        _logWindow = new LogWindow(_runLog);
        _debugWindow = new DebugWindow(_quests, _catalog);
        _fleetWindow = new FleetWindow(_config, _fleet);
        _mainWindow = new MainWindow(new MainWindowDeps(
            _config, SaveConfig, _presence, _quests, _catalog, _pathStore, _controller, _frontier, _fleet,
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

        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_mainWindow);
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
            HelpMessage = "Open Odysseus. \"/odysseus config\" settings, \"/odysseus fleet\" dashboard, \"/odysseus log\" step log, \"/odysseus paths\" step editor, \"/odysseus debug\" quest-state dump, \"/odysseus stop\" stops the run.",
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
        _controller.Tick();
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
            case "log":
                _logWindow.IsOpen = true;
                break;
            case "paths":
            case "edit":
                if (_controller.QuestId != 0) _pathEditorWindow.Open(_controller.QuestId);
                else _pathEditorWindow.IsOpen = true;
                break;
            case "stop":
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
