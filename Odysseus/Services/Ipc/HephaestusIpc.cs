using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// Hephaestus, wrapped — the other crafter a Craft step or a delivery can hand its craft to.
///
/// <para>
/// Gates, as Hephaestus publishes them (extend-only): <c>Hephaestus.CraftItem(uint recipeId, int
/// amount)</c> → true when the work began, with <c>IsBusy</c> already true; <c>Hephaestus.IsBusy</c>
/// while it drives the character; <c>Hephaestus.Stop</c>; <c>Hephaestus.GetVersion</c>, which answering
/// at all is how "loaded" is told. The recipe id is the full <c>uint</c>, so nothing narrows here the way
/// it has to for Artisan. Same fail-open shape as every handoff: a missing gate is a stop with a reason,
/// never a throw.
/// </para>
/// </summary>
public sealed class HephaestusIpc : Deliveries.ICrafter
{
    private const string VersionGate = "Hephaestus.GetVersion";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<string>? _version;
    private ICallGateSubscriber<uint, int, bool>? _craftItem;
    private ICallGateSubscriber<bool>? _isBusy;
    private ICallGateSubscriber<object>? _stop;
    private bool _warned;

    public HephaestusIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    public string Name => "Hephaestus";

    /// <summary>Why Hephaestus cannot be used, or empty when it can — the same split as Artisan's.</summary>
    public string Unavailable { get; private set; } = string.Empty;

    public bool Available
    {
        get
        {
            try
            {
                _version ??= _pluginInterface.GetIpcSubscriber<string>(VersionGate);
                _version.InvokeFunc();
                Unavailable = string.Empty;
                _warned = false;
                return true;
            }
            catch (Exception ex)
            {
                Unavailable = IsLoaded
                    ? $"Hephaestus is loaded but {VersionGate} refused ({ex.GetType().Name})"
                    : "Hephaestus is not loaded";
                if (!_warned)
                {
                    _warned = true;
                    _log?.Invoke($"{Unavailable}: {ex.Message}");
                }
                return false;
            }
        }
    }

    private bool IsLoaded
    {
        get
        {
            try
            {
                return _pluginInterface.InstalledPlugins.Any(p =>
                    string.Equals(p.InternalName, "Hephaestus", StringComparison.OrdinalIgnoreCase)
                    && p.IsLoaded && !p.IsOutdated);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Make <paramref name="amount"/> of a recipe. False when Hephaestus is not there, or refused:
    /// not logged in, unknown recipe, or already busy with something else — its reason is in /xllog.
    /// </summary>
    public bool CraftItem(uint recipeId, int amount)
    {
        try
        {
            _craftItem ??= _pluginInterface.GetIpcSubscriber<uint, int, bool>("Hephaestus.CraftItem");
            if (_craftItem.InvokeFunc(recipeId, amount))
            {
                _warned = false;
                return true;
            }
            _log?.Invoke($"Hephaestus would not start recipe {recipeId} × {amount} (busy, unknown recipe, or not logged in — its reason is in /xllog).");
            return false;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"Hephaestus unavailable ({ex.GetType().Name}) — crafting will stop and wait for you.");
            }
            return false;
        }
    }

    public bool IsCrafting
    {
        get
        {
            try
            {
                return (_isBusy ??= _pluginInterface.GetIpcSubscriber<bool>("Hephaestus.IsBusy")).InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    public void StopCrafting()
    {
        try
        {
            (_stop ??= _pluginInterface.GetIpcSubscriber<object>("Hephaestus.Stop")).InvokeAction();
        }
        catch
        {
            // Gone already; nothing is crafting.
        }
    }
}
