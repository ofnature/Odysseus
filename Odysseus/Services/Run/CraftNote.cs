using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Odysseus.Services.Run;

/// <summary>
/// What a path's author wrote in its comment about the crafted items — "Crafted Item:\n 3x Square
/// Maple Shield", "1x Walnut Lumber HQ", "1x Crab Bow HQ with 1x Savage Aim Materia III" — the only
/// place the count, the quality and the materia a quest takes are written down. Only the "Crafted
/// Item" block is read: the "Autocraft requires" block below it lists materials, not what the quest
/// takes.
/// </summary>
public static partial class CraftNote
{
    /// <summary>Materia the quest wants melded into the item before it will take it.</summary>
    /// <param name="Materia">The materia's own name ("Savage Aim Materia III"), or null for any.</param>
    /// <param name="Grade">Only this grade counts (1 = I), or null for any grade.</param>
    public sealed record Meld(string? Materia, int? Grade, int Count);

    /// <param name="HighQuality">The note marks it HQ: the quest takes only a high-quality one.</param>
    /// <param name="Melded">Materia it must carry, or null when none is asked for.</param>
    public sealed record Entry(int Count, bool HighQuality, Meld? Melded = null);

    [GeneratedRegex(@"^\s*(\d+)\s*x\s+(.+?)(\s+HQ\b)?(?:\s+with\s+(.+?))?\s*,?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Line();

    /// <summary>"1x Savage Aim Materia III", "any Materia", "2 Quicktongue Materia III".</summary>
    [GeneratedRegex(@"^(?:(\d+)\s*x?\s+)?(.+?)$", RegexOptions.IgnoreCase)]
    private static partial Regex MeldPart();

    /// <summary>"Requires: 1x * Materia I (not II+)" — any materia, of one grade.</summary>
    [GeneratedRegex(@"^Requires:\s*(\d+)\s*x\s*\*\s*Materia\s+([IVX]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnyOfGrade();

    /// <summary>Item name, as written → count, quality and materia. Empty when the note says nothing usable.</summary>
    public static IReadOnlyDictionary<string, Entry> Read(string? note)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(note))
            return entries;
        var inBlock = false;
        string? last = null;
        // "3x Hi-Potion of Strength HQ// Autocraft requires:" — Might Made Right runs two lines into one.
        foreach (var raw in note.Replace("//", "\n").Split('\n'))
        {
            var line = raw.Trim();
            // The Lance's Lesson says its materia on a line of its own, under the one item it crafts.
            if (AnyOfGrade().Match(line) is { Success: true } grade && last is not null)
            {
                entries[last] = entries[last] with { Melded = new Meld(null, Roman(grade.Groups[2].Value), int.Parse(grade.Groups[1].Value)) };
                continue;
            }
            // "!!Requires Manual Melding!! Crafted Item:" — Saving Captain Gairhard and most of the
            // melding quests open the block mid-line, after their warning.
            var opens = line.IndexOf("Crafted Item", StringComparison.OrdinalIgnoreCase);
            if (opens < 0) opens = line.IndexOf("Crafts:", StringComparison.OrdinalIgnoreCase);
            if (opens >= 0)
            {
                inBlock = true;
                var colon = line.IndexOf(':', opens);
                line = colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
                if (line.Length == 0) continue;
            }
            else if (line.EndsWith(':') || line.StartsWith("Autocraft", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Requires", StringComparison.OrdinalIgnoreCase))
            {
                inBlock = false;
                continue;
            }
            if (!inBlock) continue;
            var match = Line().Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var count) || count <= 0)
                continue;
            var name = match.Groups[2].Value.Trim();
            entries[name] = new Entry(count, match.Groups[3].Success,
                match.Groups[4].Success ? ReadMeld(match.Groups[4].Value.Trim()) : null);
            last = name;
        }
        return entries;
    }

    /// <summary>Whether a materia, by its item name ("Savage Aim Materia I"), is one the requirement takes.</summary>
    public static bool Fits(string materiaName, Meld meld)
    {
        var name = materiaName.Trim();
        if (meld.Materia is { } wanted)
            return string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase);
        var marker = name.LastIndexOf(" Materia ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return false;
        return meld.Grade is not { } grade || Roman(name[(marker + " Materia ".Length)..].Trim()) == grade;
    }

    /// <summary>
    /// Which of the materia on offer to meld: the first that fits, and among those the lowest grade —
    /// "any Materia" is happiest with the cheapest, and a low grade is the one a low-level item takes.
    /// -1 when none fits.
    /// </summary>
    public static int Pick(IReadOnlyList<string> offered, Meld meld)
    {
        var best = -1;
        var bestGrade = int.MaxValue;
        for (var i = 0; i < offered.Count; i++)
        {
            if (!Fits(offered[i], meld)) continue;
            var grade = GradeOf(offered[i]) ?? int.MaxValue - 1;
            if (grade < bestGrade)
            {
                best = i;
                bestGrade = grade;
            }
        }
        return best;
    }

    /// <summary>"Savage Aim Materia III" → 3; null when the name carries no grade.</summary>
    public static int? GradeOf(string materiaName)
    {
        var name = materiaName.Trim();
        var marker = name.LastIndexOf(" Materia ", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? null : Roman(name[(marker + " Materia ".Length)..].Trim());
    }

    private static Meld? ReadMeld(string text)
    {
        var part = MeldPart().Match(text);
        if (!part.Success) return null;
        var count = part.Groups[1].Success && int.TryParse(part.Groups[1].Value, out var n) ? n : 1;
        var what = part.Groups[2].Value.Trim();
        return what.StartsWith("any", StringComparison.OrdinalIgnoreCase)
            ? new Meld(null, null, count)
            : new Meld(what, null, count);
    }

    private static int? Roman(string numeral) => numeral.ToUpperInvariant() switch
    {
        "I" => 1, "II" => 2, "III" => 3, "IV" => 4, "V" => 5, "VI" => 6,
        "VII" => 7, "VIII" => 8, "IX" => 9, "X" => 10, "XI" => 11, "XII" => 12,
        _ => null,
    };
}
