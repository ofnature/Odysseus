using System;

namespace Odysseus.Services.Run;

/// <summary>What keeping a chocobo out needs to know and do.</summary>
public interface IChocoboWorld
{
    DateTime UtcNow { get; }

    /// <summary>Seconds the companion has left before it goes away; 0 when it is not out.</summary>
    float CompanionTimeLeft { get; }

    /// <summary>Somewhere a companion is allowed — the field, not a city or a duty.</summary>
    bool CanSummonHere { get; }

    /// <summary>Busy with a cutscene or a conversation; feeding it now would be swallowed.</summary>
    bool IsOccupied { get; }

    int ItemCount(uint itemId);

    bool UseItem(uint itemId);

    void Log(string message);
}

/// <summary>
/// Keeps the chocobo companion summoned.
///
/// <para>
/// One Gysahl Green buys half an hour, so this is not a loop that needs to be clever: notice the
/// timer running down, feed it again, and stay quiet the rest of the time. What it does have to be
/// careful about is <i>not</i> feeding — a green spent in a city, in a duty, or before the quest
/// that unlocks the companion is a green wasted and a message in the log nobody wants twice.
/// </para>
///
/// <para>
/// The unlock is one of three quests depending on the Grand Company you joined, so the caller
/// answers that question rather than this class guessing at it.
/// </para>
/// </summary>
public sealed class ChocoboKeeper
{
    /// <summary>Gysahl Greens.</summary>
    public const uint GysahlGreens = 4868;

    /// <summary>"My Little Chocobo", per Grand Company (1 Maelstrom, 2 Twin Adder, 3 Immortal Flames).</summary>
    public static ushort UnlockQuestFor(byte grandCompany) => grandCompany switch
    {
        1 => 701, // Maelstrom
        2 => 700, // Twin Adder
        3 => 702, // Immortal Flames
        _ => 0,
    };

    /// <summary>Feed it again with this much left, so it never actually vanishes mid-fight.</summary>
    private static readonly TimeSpan LowWater = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to leave it before trying again. The game refuses a summon for plenty of reasons we
    /// cannot see from here — a sanctuary we misjudged, a mount transition, a loading screen — and
    /// the cost of guessing wrong is a wasted green, so guess slowly.
    /// </summary>
    private static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(30);

    private readonly IChocoboWorld _world;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _unlocked;
    private DateTime _lastTry;
    private bool _saidNoGreens;
    private bool _saidLocked;

    public ChocoboKeeper(IChocoboWorld world, Func<bool> enabled, Func<bool> unlocked)
    {
        _world = world;
        _enabled = enabled;
        _unlocked = unlocked;
    }

    public void Tick()
    {
        if (!_enabled())
            return;

        if (!_unlocked())
        {
            if (!_saidLocked)
            {
                _saidLocked = true;
                _world.Log("Keep the chocobo out: \"My Little Chocobo\" is not done on this character, " +
                           "so there is nothing to summon yet. Queue it from the Journal and this starts working.");
            }
            return;
        }
        _saidLocked = false;

        if (!_world.CanSummonHere || _world.IsOccupied)
            return;

        if (_world.CompanionTimeLeft > LowWater.TotalSeconds)
            return;

        var now = _world.UtcNow;
        if (now - _lastTry < RetryGap)
            return;
        _lastTry = now;

        if (_world.ItemCount(GysahlGreens) <= 0)
        {
            if (!_saidNoGreens)
            {
                _saidNoGreens = true;
                _world.Log("Keep the chocobo out: no Gysahl Greens in the bags. Buy some and it resumes.");
            }
            return;
        }

        _saidNoGreens = false;
        _world.UseItem(GysahlGreens);
    }
}
