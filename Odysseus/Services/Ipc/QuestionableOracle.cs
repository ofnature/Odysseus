using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// Read-only view of what Questionable (if loaded) thinks the current quest is.
///
/// <para>
/// <b>Test oracle, not a dependency.</b> Odysseus reads quest state from <c>QuestManager</c>
/// itself; this exists so the debug window can put our numbers next to an independent reading of
/// the same memory and flag any disagreement. Nothing in the run path consumes it, and it must
/// stay that way — Questionable ≥6.9 is proprietary and the whole point of the rebuild is not to
/// depend on it.
/// </para>
///
/// <para>
/// Every call fails open: a missing plugin, a renamed gate or a type mismatch all read as
/// "unavailable", never as a throw into the draw loop.
/// </para>
/// </summary>
public sealed class QuestionableOracle
{
    private readonly ICallGateSubscriber<ushort?> _currentQuestId;
    private readonly ICallGateSubscriber<bool> _isRunning;
    private readonly ICallGateSubscriber<ushort, bool> _isQuestAccepted;
    private readonly ICallGateSubscriber<ushort, bool> _isQuestComplete;

    public QuestionableOracle(IDalamudPluginInterface pluginInterface)
    {
        _currentQuestId = pluginInterface.GetIpcSubscriber<ushort?>("Questionable.GetCurrentQuestId");
        _isRunning = pluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
        _isQuestAccepted = pluginInterface.GetIpcSubscriber<ushort, bool>("Questionable.IsQuestAccepted");
        _isQuestComplete = pluginInterface.GetIpcSubscriber<ushort, bool>("Questionable.IsQuestComplete");
    }

    /// <summary>Questionable's current quest id, or null when it has none / is not loaded.</summary>
    public ushort? CurrentQuestId()
    {
        try { return _currentQuestId.InvokeFunc(); }
        catch { return null; }
    }

    public bool? IsRunning()
    {
        try { return _isRunning.InvokeFunc(); }
        catch { return null; }
    }

    public bool? IsQuestAccepted(ushort questId)
    {
        try { return _isQuestAccepted.InvokeFunc(questId); }
        catch { return null; }
    }

    public bool? IsQuestComplete(ushort questId)
    {
        try { return _isQuestComplete.InvokeFunc(questId); }
        catch { return null; }
    }

    /// <summary>True when at least one gate answers — i.e. the plugin is loaded and speaking our dialect.</summary>
    public bool Available => IsRunning() is not null;
}
