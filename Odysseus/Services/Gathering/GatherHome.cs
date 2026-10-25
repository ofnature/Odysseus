namespace Odysseus.Services.Gathering;

/// <summary>
/// Where a finished gather run takes you. Legacy: the gather lists moved to Demeter, which reads this
/// once from Odysseus's saved config and keeps its own from then on. Kept only so that saved value
/// survives until Demeter has read it; drop it with <see cref="Config.OdysseusConfig.GatherHome"/>.
/// </summary>
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
