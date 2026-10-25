namespace Odysseus.Services.Gathering;

/// <summary>A timed node's window in Eorzea time: from this minute of the day, for this many minutes.</summary>
public readonly record struct NodeWindow(int StartMinute, int DurationMinutes)
{
    /// <summary>The sheets write times as HHMM — 600 is 06:00, 300 is three hours.</summary>
    public static int FromHhmm(int hhmm) => hhmm / 100 * 60 + hhmm % 100;
}
