namespace Odysseus.Services.Gathering;

/// <summary>Where a finished gather run takes you, as GatherBuddy's "go home" does.</summary>
public enum GatherHome
{
    Stay = 0,
    /// <summary>The Return action — your home point, on its own cooldown.</summary>
    Return = 1,
    /// <summary>Lifestream: the inn.</summary>
    Inn = 2,
    /// <summary>Lifestream: whichever estate its own preference names (/li auto).</summary>
    Estate = 3,
    /// <summary>Lifestream: the free company estate.</summary>
    FreeCompany = 4,
    /// <summary>Lifestream: your private estate.</summary>
    Private = 5,
    /// <summary>Lifestream: your apartment.</summary>
    Apartment = 6,
}

public static class GatherHomeCommands
{
    /// <summary>The chat command that goes there, or null for staying put. Lifestream owns every trip but Return.</summary>
    public static string? For(GatherHome home) => home switch
    {
        GatherHome.Return => "/generalaction Return",
        GatherHome.Inn => "/li inn",
        GatherHome.Estate => "/li auto",
        GatherHome.FreeCompany => "/li fc",
        GatherHome.Private => "/li home",
        GatherHome.Apartment => "/li apt",
        _ => null,
    };

    public static string Label(GatherHome home) => home switch
    {
        GatherHome.Stay => "Stay where I am",
        GatherHome.Return => "Return",
        GatherHome.Inn => "Inn",
        GatherHome.Estate => "Estate (Lifestream's preference)",
        GatherHome.FreeCompany => "Free company estate",
        GatherHome.Private => "Private estate",
        GatherHome.Apartment => "Apartment",
        _ => home.ToString(),
    };
}
