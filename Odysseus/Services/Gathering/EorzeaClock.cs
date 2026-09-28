using System;
using System.Collections.Generic;

namespace Odysseus.Services.Gathering;

/// <summary>A timed node's window in Eorzea time: from this minute of the day, for this many minutes.</summary>
public readonly record struct NodeWindow(int StartMinute, int DurationMinutes)
{
    /// <summary>The sheets write times as HHMM — 600 is 06:00, 300 is three hours.</summary>
    public static int FromHhmm(int hhmm) => hhmm / 100 * 60 + hhmm % 100;
}

/// <summary>
/// Eorzea time, and when a timed node is next up. One Eorzean hour is 175 real seconds, counted from
/// the Unix epoch, so an Eorzean day is 70 real minutes.
/// </summary>
public static class EorzeaClock
{
    private const double RealSecondsPerEorzeaMinute = 175.0 / 60.0;
    private const int MinutesPerDay = 24 * 60;

    /// <summary>The Eorzean minute of the day, with its fraction.</summary>
    public static double MinuteOfDay(DateTime utc)
    {
        var realSeconds = (utc - DateTime.UnixEpoch).TotalSeconds;
        var eorzeaMinutes = realSeconds / RealSecondsPerEorzeaMinute;
        return eorzeaMinutes % MinutesPerDay;
    }

    /// <summary>
    /// Whether any window is up now, and the real time until that changes — how long it stays up,
    /// or how long until the next one opens. Null when there are no windows.
    /// </summary>
    public static (bool Up, TimeSpan Change)? Next(IReadOnlyList<NodeWindow> windows, DateTime utc)
    {
        if (windows.Count == 0) return null;
        var now = MinuteOfDay(utc);
        double? upFor = null;
        var wait = double.MaxValue;
        foreach (var w in windows)
        {
            var since = (now - w.StartMinute + MinutesPerDay) % MinutesPerDay;
            if (w.DurationMinutes > 0 && since < w.DurationMinutes)
                upFor = Math.Max(upFor ?? 0, w.DurationMinutes - since);
            else
                wait = Math.Min(wait, (w.StartMinute - now + MinutesPerDay) % MinutesPerDay);
        }
        return upFor is { } left
            ? (true, TimeSpan.FromSeconds(left * RealSecondsPerEorzeaMinute))
            : (false, TimeSpan.FromSeconds(wait * RealSecondsPerEorzeaMinute));
    }

    /// <summary>
    /// How long until a timed node can be gathered: zero while it is up with at least
    /// <paramref name="minLeft"/> of its window to go, else the wait for the next window worth going
    /// to. Null when there are no windows.
    /// </summary>
    public static TimeSpan? UntilGatherable(IReadOnlyList<NodeWindow> windows, DateTime utc, TimeSpan minLeft)
    {
        if (Next(windows, utc) is not { } now) return null;
        if (!now.Up) return now.Change;
        if (now.Change >= minLeft) return TimeSpan.Zero;
        // Up, but closing before anyone could get there: the next opening after this one.
        var after = utc + now.Change + TimeSpan.FromSeconds(1);
        return Next(windows, after) is { } later ? now.Change + TimeSpan.FromSeconds(1) + later.Change : null;
    }

    /// <summary>"up · 4:12 left" or "in 12:34" — empty when the item is not on a timed node.</summary>
    public static string Describe(IReadOnlyList<NodeWindow> windows, DateTime utc)
    {
        if (Next(windows, utc) is not { } next) return string.Empty;
        var span = next.Change;
        var clock = span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
        return next.Up ? $"up · {clock} left" : $"in {clock}";
    }
}
