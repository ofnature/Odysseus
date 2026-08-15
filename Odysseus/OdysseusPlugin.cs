using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Odysseus.Config;
using Odysseus.Services.Ipc;
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
    private readonly QuestionableOracle _oracle;
    private readonly OdysseusIpc _ipc;
    private readonly ConfigWindow _configWindow;
    private readonly RunWindow _runWindow;
    private readonly DebugWindow _debugWindow;

    // Framework cut: no controller yet, so the state is a constant. Replaced in P1.
    private RunState _state = RunState.Idle;

    public OdysseusPlugin(IDalamudPluginInterface pluginInterface)
    {
        // Create<T> constructs a T. It must be given the service holder, never this plugin —
        // see Service for why that distinction silently kills the client.
        pluginInterface.Create<Service>();

        _config = PluginInterface.GetPluginConfig() as OdysseusConfig ?? new OdysseusConfig();
        _presence = new PluginPresence(PluginInterface);
        _quests = new QuestStateReader(fault => Log.Warning($"Quest state read failed. {fault}"));
        _catalog = new QuestCatalog(DataManager, message => Log.Warning(message));
        // Differential test oracle only — see QuestionableOracle. Never on the run path.
        _oracle = new QuestionableOracle(PluginInterface);

        // Published now so Daedalus can find the gate; it reads false until a run exists.
        _ipc = new OdysseusIpc(PluginInterface, () => _config.Enabled && _state.IsDriving());

        _configWindow = new ConfigWindow(_config, SaveConfig, _presence);
        _runWindow = new RunWindow(_config, _presence, _quests, _catalog, () => _state, OpenConfig);
        _debugWindow = new DebugWindow(_quests, _catalog, _oracle);

        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_runWindow);
        _windowSystem.AddWindow(_debugWindow);

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Odysseus. \"/odysseus config\" opens settings, \"/odysseus debug\" the quest-state dump.",
        });
        CommandManager.AddHandler(CommandShort, new CommandInfo(OnCommand)
        {
            HelpMessage = "Short alias for /odysseus.",
        });

        Log.Information($"Odysseus v{PluginVersion} loaded.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandMain);
        CommandManager.RemoveHandler(CommandShort);

        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;

        _windowSystem.RemoveAllWindows();
        _ipc.Dispose();
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
            default:
                OpenMain();
                break;
        }
    }

    private void OpenMain() => _runWindow.IsOpen = true;

    private void OpenConfig() => _configWindow.IsOpen = true;

    private void SaveConfig() => PluginInterface.SavePluginConfig(_config);
}
