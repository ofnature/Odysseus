using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// Artisan, wrapped — the crafting handoff for custom deliveries.
///
/// <para>
/// Odysseus does not craft. It buys the ingredients, asks Artisan to make N of a recipe, waits for
/// its endurance loop to finish, then turns in. Same fail-open shape as every other handoff: with
/// Artisan absent the delivery step stops and says so rather than throwing.
/// </para>
///
/// <para>
/// Gates (as vsatisfy uses them): <c>Artisan.CraftItem(ushort recipeId, int amount)</c> starts a
/// craft, <c>Artisan.GetEnduranceStatus</c> is true while it runs. <c>Artisan.IsBusy</c> and
/// <c>Artisan.SetEnduranceStatus</c> are used when present, so a stuck endurance loop can be
/// stopped.
/// </para>
/// </summary>
public sealed class ArtisanIpc : Deliveries.ICrafter
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<ushort, int, object>? _craftItem;
    private ICallGateSubscriber<bool>? _endurance;
    private ICallGateSubscriber<bool>? _isBusy;
    private ICallGateSubscriber<bool, object>? _setEndurance;
    private bool _warned;

    public ArtisanIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>
    /// Why Artisan cannot be used, or empty when it can. Worth telling apart: "not installed" is
    /// something you fix in the plugin installer, "loaded but its gate refused" is something we
    /// fix here, and the two used to look identical from the outside — one yellow chip either way,
    /// with the exception swallowed.
    /// </summary>
    public string Unavailable { get; private set; } = string.Empty;

    private const string EnduranceGate = "Artisan.GetEnduranceStatus";

    /// <summary>Artisan is loaded and answering.</summary>
    public bool Available
    {
        get
        {
            try
            {
                _endurance ??= _pluginInterface.GetIpcSubscriber<bool>(EnduranceGate);
                _endurance.InvokeFunc();
                Unavailable = string.Empty;
                _warned = false;
                return true;
            }
            catch (Exception ex)
            {
                Unavailable = IsLoaded
                    ? $"Artisan is loaded but {EnduranceGate} refused ({ex.GetType().Name})"
                    : "Artisan is not loaded";
                if (!_warned)
                {
                    _warned = true;
                    _log?.Invoke($"{Unavailable}: {ex.Message}");
                }
                return false;
            }
        }
    }

    /// <summary>Dalamud's own answer, which does not depend on any gate being right.</summary>
    private bool IsLoaded
    {
        get
        {
            try
            {
                return _pluginInterface.InstalledPlugins.Any(p =>
                    string.Equals(p.InternalName, "Artisan", StringComparison.OrdinalIgnoreCase)
                    && p.IsLoaded && !p.IsOutdated);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Start crafting <paramref name="amount"/> of a recipe. False when Artisan is not there.</summary>
    public bool CraftItem(ushort recipeId, int amount)
    {
        try
        {
            (_craftItem ??= _pluginInterface.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem")).InvokeAction(recipeId, amount);
            _warned = false;
            return true;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"Artisan unavailable ({ex.GetType().Name}) — crafting for deliveries will stop and wait for you.");
            }
            return false;
        }
    }

    /// <summary>Artisan's endurance loop is running — i.e. it is still crafting.</summary>
    public bool IsCrafting
    {
        get
        {
            try
            {
                _endurance ??= _pluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus");
                if (_endurance.InvokeFunc())
                    return true;
                _isBusy ??= _pluginInterface.GetIpcSubscriber<bool>("Artisan.IsBusy");
                return _isBusy.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Stop Artisan's endurance loop, if it will let us.</summary>
    public void StopCrafting()
    {
        try
        {
            (_setEndurance ??= _pluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus")).InvokeAction(false);
        }
        catch
        {
            // Older Artisan without the setter; the loop ends on its own.
        }
    }
}
