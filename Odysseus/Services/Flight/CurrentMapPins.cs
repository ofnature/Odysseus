using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Odysseus.Services.Flight;

/// <summary>One marker on the map: a loose current, or the giver of a current quest.</summary>
/// <param name="Label">Text beside it on the full map, or null for none.</param>
/// <param name="Collected">The current is already held (shown only when everything is asked for).</param>
/// <param name="Later">On ground the story has not opened yet.</param>
public sealed record MapPin(Vector3 At, uint Icon, string? Label, uint CurrentId, bool Collected, bool Later);

/// <summary>
/// What goes on a zone's map: its loose currents, and the givers of current
/// quests taken there — whichever zone the current itself belongs to. Missing ones only, unless
/// everything is asked for, collected or not.
/// </summary>
public static class CurrentMapPins
{
    /// <summary>The whirlwind: a loose current (labelled "collected" once held).</summary>
    public const uint CurrentIcon = 63907;
    /// <summary>The side-quest "!": a current quest still to do.</summary>
    public const uint QuestIcon = 71021;
    /// <summary>The green tick: a current quest done. (71024 beside it is a small yellow dot.)</summary>
    public const uint DoneIcon = 71025;

    public static IReadOnlyList<MapPin> For(IEnumerable<ZoneFlight> zones, uint territory, Func<uint, bool> unlocked,
        bool showAll, Func<ushort, string> questName, Func<uint, Vector3, bool>? storyHasBeen)
    {
        var pins = new List<MapPin>();
        foreach (var zone in zones)
            foreach (var current in zone.Currents)
            {
                var collected = unlocked(current.Id);
                if (collected && !showAll) continue;

                if (current.FromQuest)
                {
                    if (current.GiverTerritory != territory || current.GiverAt is not { } giver) continue;
                    var later = !collected && storyHasBeen?.Invoke(territory, giver) == false;
                    pins.Add(new MapPin(giver, collected ? DoneIcon : QuestIcon,
                        questName(current.QuestId) + (later ? " (later)" : ""), current.Id, collected, later));
                }
                else
                {
                    if (zone.TerritoryId != territory || current.Position is not { } at) continue;
                    var later = !collected && storyHasBeen?.Invoke(territory, at) == false;
                    // Always the whirlwind, so a loose current reads as one whether held or not.
                    pins.Add(new MapPin(at, CurrentIcon,
                        collected ? "collected" : later ? "later" : null, current.Id, collected, later));
                }
            }
        return pins;
    }

    /// <summary>
    /// The nearest one still to get, across the ground — ground the story has opened first, then any.
    /// Null when nothing is missing here.
    /// </summary>
    public static MapPin? Nearest(IEnumerable<MapPin> pins, Vector3 from)
    {
        var missing = pins.Where(p => !p.Collected).ToList();
        var here = new Vector2(from.X, from.Z);
        return (missing.Any(p => !p.Later) ? missing.Where(p => !p.Later) : missing)
            .OrderBy(p => Vector2.Distance(here, new Vector2(p.At.X, p.At.Z)))
            .FirstOrDefault();
    }
}
