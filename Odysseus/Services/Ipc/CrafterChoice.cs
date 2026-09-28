using System;

namespace Odysseus.Services.Ipc;

/// <summary>
/// The crafter the settings name — Artisan or Hephaestus — behind the one seam Craft steps and
/// deliveries already use. Read on every call, so switching in Settings takes effect at once.
///
/// <para>
/// A craft that has begun stays with the crafter that began it: switching mid-craft must not make
/// the other one's "idle" read as the craft being over.
/// </para>
/// </summary>
public sealed class CrafterChoice : Deliveries.ICrafter
{
    public const string ArtisanProvider = "Artisan";
    public const string HephaestusProvider = "Hephaestus";

    /// <summary>The names the settings combo offers, in order.</summary>
    public static readonly string[] Providers = [ArtisanProvider, HephaestusProvider];

    private readonly Deliveries.ICrafter _artisan;
    private readonly Deliveries.ICrafter _hephaestus;
    private readonly Func<string> _provider;
    private Deliveries.ICrafter? _working;

    public CrafterChoice(Deliveries.ICrafter artisan, Deliveries.ICrafter hephaestus, Func<string> provider)
    {
        _artisan = artisan;
        _hephaestus = hephaestus;
        _provider = provider;
    }

    private bool UsesHephaestus => string.Equals(_provider(), HephaestusProvider, StringComparison.OrdinalIgnoreCase);

    private Deliveries.ICrafter Chosen => UsesHephaestus ? _hephaestus : _artisan;

    /// <summary>The crafter in charge: the one working, else the one chosen.</summary>
    private Deliveries.ICrafter Current => _working ?? Chosen;

    public string Name => Current.Name;

    /// <summary>Why the chosen crafter cannot be used, or empty when it can.</summary>
    public string Unavailable => Chosen switch
    {
        ArtisanIpc artisan => artisan.Unavailable,
        HephaestusIpc hephaestus => hephaestus.Unavailable,
        _ => string.Empty,
    };

    public bool Available => Chosen.Available;

    public bool CraftItem(uint recipeId, int amount)
    {
        var crafter = Chosen;
        if (!crafter.CraftItem(recipeId, amount))
            return false;
        _working = crafter;
        return true;
    }

    public bool IsCrafting
    {
        get
        {
            if (_working is { } working)
            {
                if (working.IsCrafting)
                    return true;
                _working = null;
                return false;
            }
            return Chosen.IsCrafting;
        }
    }

    public void StopCrafting()
    {
        Current.StopCrafting();
        _working = null;
    }
}
