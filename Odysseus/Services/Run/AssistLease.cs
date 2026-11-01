using System;
using System.Collections.Generic;

namespace Odysseus.Services.Run;

/// <summary>
/// Odysseus's dialogue and Request hand-over handling, lent to another plugin while no run of ours is
/// driving — Parcae's Collect FATE turn-ins, which used to need TextAdvance for exactly this.
///
/// <para>
/// A lease per caller, with a time limit (capped at five minutes) the caller keeps renewing: a caller
/// that crashes or unloads stops renewing and its lease lapses, so the windows are never left being
/// pressed for nobody. While any lease is live: the Talk box is advanced, and a Request window is
/// filled from the bags and handed over — only when the game says the bags can cover it, and only
/// what the window asks for. A yes/no raised just after Hand Over is confirmed. Cutscenes are not
/// touched (Charon's), and the quest offer and reward windows are not either.
/// </para>
///
/// <para>
/// While a run of ours is driving, it handles its own windows as always and the lease does nothing.
/// </para>
/// </summary>
public sealed class AssistLease
{
    public const int MaxSeconds = 300;

    /// <summary>The Request window fills a frame or two after it shows; judged once it has settled.</summary>
    private static readonly TimeSpan HandOverSettle = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan HandOverRetry = TimeSpan.FromSeconds(1.5);
    /// <summary>A yes/no this soon after Hand Over is the hand-over's own confirm.</summary>
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, DateTime> _until = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _requestSince;
    private DateTime _lastTry;
    private DateTime _handedOverAt;

    /// <summary>
    /// Take, renew or release <paramref name="caller"/>'s lease. Seconds over the cap are capped; 0 or
    /// less releases it. False only for a nameless caller.
    /// </summary>
    public bool Lease(string? caller, int seconds, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(caller))
            return false;
        if (seconds <= 0)
            _until.Remove(caller);
        else
            _until[caller] = now + TimeSpan.FromSeconds(Math.Min(seconds, MaxSeconds));
        return true;
    }

    public bool IsActive(DateTime now)
    {
        foreach (var until in _until.Values)
            if (until > now)
                return true;
        return false;
    }

    /// <summary>Who holds a live lease, for the log and the settings line.</summary>
    public IEnumerable<string> Holders(DateTime now)
    {
        foreach (var (caller, until) in _until)
            if (until > now)
                yield return caller;
    }

    /// <param name="ourRunDriving">A run or job of ours is driving: it has the windows.</param>
    public void Tick(IStepWorld world, bool ourRunDriving, DateTime now)
    {
        if (ourRunDriving || !IsActive(now))
        {
            _requestSince = default;
            return;
        }

        if (world.IsAddonVisible("Talk"))
            world.AdvanceTalk();

        if (world.IsAddonVisible("Request"))
        {
            if (_requestSince == default)
                _requestSince = now;
            // Never a partial or wrong hand-in: if the bags cannot cover it, the caller handles the stall.
            if (now - _requestSince >= HandOverSettle && now - _lastTry >= HandOverRetry && world.CanSatisfyHandOver)
            {
                _lastTry = now;
                if (world.CompleteHandOverWindow())
                    _handedOverAt = now;
            }
            return;
        }
        _requestSince = default;

        if (_handedOverAt != default && now - _handedOverAt <= ConfirmWindow && world.IsAddonVisible("SelectYesno"))
        {
            world.Log($"Hand-over confirm (\"{world.YesNoPrompt()}\") — yes, for {string.Join(", ", Holders(now))}.");
            world.SelectYesNo(true);
            _handedOverAt = default;
        }
    }
}
