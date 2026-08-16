using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// TextAdvance, wrapped. It skips dialogue, accepts and completes quests, and skips cutscenes —
/// but only while its master switch is on, and the user may well keep that off. External control
/// asks it to do those things for <i>this plugin's</i> sake for as long as the run is driving,
/// without touching the user's own setting.
///
/// <para>
/// The options object crosses the IPC boundary as JSON, so the property names below must match
/// TextAdvance's <c>ExternalTerritoryConfig</c> exactly. Every call fails open: without TextAdvance
/// the run still walks and interacts, and dialogue simply waits for a human.
/// </para>
/// </summary>
public sealed class TextAdvanceIpc
{
    private const string EnableGate = "TextAdvance.EnableExternalControl";
    private const string DisableGate = "TextAdvance.DisableExternalControl";
    private const string IsInExternalControlGate = "TextAdvance.IsInExternalControl";

    /// <summary>Who we say we are to TextAdvance; it keys external control by this name.</summary>
    private const string PluginName = "Odysseus";

    /// <summary>Mirror of TextAdvance's <c>ExternalTerritoryConfig</c>. Field names are the contract.</summary>
    public sealed class ExternalTerritoryConfig
    {
        public bool? EnableQuestAccept { get; set; } = true;
        public bool? EnableQuestComplete { get; set; } = true;
        public bool? EnableRewardPick { get; set; } = true;
        public bool? EnableRequestHandin { get; set; } = true;
        public bool? EnableCutsceneEsc { get; set; } = true;
        public bool? EnableCutsceneSkipConfirm { get; set; } = true;
        public bool? EnableTalkSkip { get; set; } = true;
        public bool? EnableRequestFill { get; set; } = true;
        public bool? EnableAutoInteract { get; set; } = false;
    }

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;
    private readonly Func<bool> _pickRewards;

    private ICallGateSubscriber<string, ExternalTerritoryConfig, bool>? _enable;
    private ICallGateSubscriber<string, bool>? _disable;
    private ICallGateSubscriber<bool>? _isInExternalControl;
    private bool _warned;
    private bool _held;

    /// <param name="pickRewards">
    /// Whether TextAdvance may choose optional quest rewards for us. Read live so the setting
    /// applies to the next hold. Picking uses TextAdvance's own priority (gil, vendor value, gear
    /// coffers, current-job gear); Odysseus does not second-guess it.
    /// </param>
    public TextAdvanceIpc(IDalamudPluginInterface pluginInterface, Func<bool>? pickRewards = null, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _pickRewards = pickRewards ?? (() => true);
        _log = log;
    }

    /// <summary>Ask TextAdvance to handle dialogue for us. Idempotent; re-asserting is cheap.</summary>
    public bool Hold()
    {
        try
        {
            _enable ??= _pluginInterface.GetIpcSubscriber<string, ExternalTerritoryConfig, bool>(EnableGate);
            var ok = _enable.InvokeFunc(PluginName, new ExternalTerritoryConfig { EnableRewardPick = _pickRewards() });
            _held = ok;
            _warned = false;
            return ok;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"TextAdvance external control unavailable ({ex.GetType().Name}) — dialogue will wait for you.");
            }
            return false;
        }
    }

    /// <summary>Give dialogue back to the user's own TextAdvance settings.</summary>
    public void Release()
    {
        if (!_held) return;
        _held = false;
        try
        {
            _disable ??= _pluginInterface.GetIpcSubscriber<string, bool>(DisableGate);
            _disable.InvokeFunc(PluginName);
        }
        catch
        {
            // Nothing to release into; the plugin is gone.
        }
    }

    public bool IsInExternalControl
    {
        get
        {
            try
            {
                _isInExternalControl ??= _pluginInterface.GetIpcSubscriber<bool>(IsInExternalControlGate);
                return _isInExternalControl.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }
}
