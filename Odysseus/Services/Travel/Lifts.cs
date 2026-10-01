using System;
using System.Collections.Generic;
using System.Numerics;

namespace Odysseus.Services.Travel;

/// <summary>
/// The city lifts: an attendant on each level, talked to, whose icon menu names the other stops
/// ("Ride Lift to the Airship Landing"). Positions read from the cities' planevent/planner layouts —
/// no sheet places these NPCs. A stop's <see cref="Name"/> is how the other attendants' menus call
/// it, where known; Willahelm's own level and Limsa's lower two are not named in any menu seen yet.
///
/// <para>
/// Why it matters: an Airship Landing has no shard. It joins the aethernet only once every shard
/// in the city is attuned, and until then the walk up to it ends at the lift doors.
/// </para>
/// </summary>
public static class Lifts
{
    public sealed record Stop(string City, uint TerritoryId, uint AttendantId, Vector3 At, string? Name);

    public static readonly IReadOnlyList<Stop> All =
    [
        new("Ul'dah", 130, 1001834, new Vector3(-23.3f, 10.0f, -43.4f), null),                  // Willahelm
        new("Ul'dah", 130, 1004339, new Vector3(-26.0f, 81.8f, -32.0f), "Airship Landing"),     // Nanahomi
        new("Ul'dah", 131, 1001854, new Vector3(-19.4f, 34.0f, -42.6f), "Ruby Road Exchange"),  // Lolomaya
        new("Limsa Lominsa", 128, 1003583, new Vector3(-7.2f, 91.5f, -16.1f), "Airship Landing"), // Blanmhas
        new("Limsa Lominsa", 128, 1003597, new Vector3(8.2f, 40.0f, 17.8f), null),              // Skaenrael
        new("Limsa Lominsa", 129, 1003611, new Vector3(9.8f, 21.0f, 15.1f), null),              // Grehfarr
    ];

    /// <summary>Levels apart count for more than distance across: a lift stop is found by its floor first.</summary>
    private static float Reach(Vector3 from, Vector3 to) =>
        Vector2.Distance(new Vector2(from.X, from.Z), new Vector2(to.X, to.Z)) + 5f * MathF.Abs(from.Y - to.Y);

    private static Stop? Nearest(uint territory, Vector3 at, string? city = null)
    {
        Stop? best = null;
        var bestReach = float.MaxValue;
        foreach (var stop in All)
        {
            if (stop.TerritoryId != territory || (city is not null && stop.City != city))
                continue;
            var reach = Reach(at, stop.At);
            if (reach < bestReach)
            {
                best = stop;
                bestReach = reach;
            }
        }
        return best;
    }

    /// <summary>The stop a destination is nearest, when it is in a city with lifts.</summary>
    public static Stop? StopFor(uint territory, Vector3 mark) => Nearest(territory, mark);

    /// <summary>
    /// The attendant to board at, standing here, to reach a mark — null when no lift serves this
    /// place or the nearest stop to the mark is the one already here.
    /// </summary>
    public static (Stop Board, Stop Alight)? Ride(uint hereTerritory, Vector3 here, uint markTerritory, Vector3 mark)
    {
        if (Nearest(hereTerritory, here) is not { } board || Nearest(markTerritory, mark, board.City) is not { } alight)
            return null;
        return board == alight ? null : (board, alight);
    }

    /// <summary>
    /// The menu entry that takes the lift to <paramref name="alight"/>: its name where menus give
    /// one, else the one entry naming no stop this city knows. The last entry is always "Nothing".
    /// </summary>
    public static int EntryFor(IReadOnlyList<string> entries, Stop alight)
    {
        if (alight.Name is { } name)
        {
            for (var i = 0; i < entries.Count; i++)
                if (entries[i].Contains(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
        for (var i = 0; i < entries.Count - 1; i++)
        {
            var known = false;
            foreach (var stop in All)
                if (stop.City == alight.City && stop.Name is { } other && entries[i].Contains(other, StringComparison.OrdinalIgnoreCase))
                    known = true;
            if (!known)
                return i;
        }
        return -1;
    }

    /// <summary>Is this menu a lift's? Every one seen says "Ride Lift to …".</summary>
    public static bool IsLiftMenu(IReadOnlyList<string> entries)
    {
        foreach (var entry in entries)
            if (entry.Contains("Lift", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
