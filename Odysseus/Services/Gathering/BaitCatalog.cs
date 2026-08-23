using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Odysseus.Services.Gathering;

/// <summary>What to put on the hook for one fish.</summary>
/// <param name="Bait">The bait to start with; zero when the fish is only reached by mooching.</param>
/// <param name="Mooches">Fish to catch and mooch from, in order, before the target bites.</param>
/// <param name="IsSpearFish">Speared rather than hooked — no bait at all.</param>
public sealed record BaitPlan(uint ItemId, uint Bait, IReadOnlyList<uint> Mooches, bool IsSpearFish)
{
    /// <summary>Nothing to cast with and nothing to mooch from: we cannot fish for this.</summary>
    public bool IsEmpty => Bait == 0 && Mooches.Count == 0 && !IsSpearFish;
}

/// <summary>
/// Bait and mooch chains, read from AutoHook's own fish database.
///
/// <para>
/// The game's sheets say which fish live in which spot and nothing about what they bite. That data
/// is community-gathered, and AutoHook already ships it — 1,945 fish in
/// <c>Data/FishData/fish_list.json</c>, covering every fish a custom delivery asks for. It is read
/// from the user's own AutoHook installation at runtime rather than copied in: it is their data,
/// their file, and AutoHook has to be installed for the fishing to work anyway.
/// </para>
/// </summary>
public sealed class BaitCatalog
{
    /// <summary>Relative to an AutoHook installation's folder.</summary>
    public const string RelativePath = @"Data\FishData\fish_list.json";

    private readonly Func<string?> _autoHookFolder;
    private readonly Action<string>? _log;
    private Dictionary<uint, BaitPlan>? _fish;

    public BaitCatalog(Func<string?> autoHookFolder, Action<string>? log = null)
    {
        _autoHookFolder = autoHookFolder;
        _log = log;
    }

    /// <summary>How many fish the catalogue knows; zero means AutoHook was not found.</summary>
    public int Count
    {
        get { EnsureLoaded(); return _fish!.Count; }
    }

    public BaitPlan? For(uint itemId)
    {
        EnsureLoaded();
        return _fish!.GetValueOrDefault(itemId);
    }

    /// <summary>Parse the file's own shape. Public so it can be tested against a sample.</summary>
    public static Dictionary<uint, BaitPlan> Parse(Stream json, Action<string>? log = null)
    {
        var fish = new Dictionary<uint, BaitPlan>();
        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("ItemId", out var idElement) || idElement.GetUInt32() == 0)
                continue;
            var id = idElement.GetUInt32();
            var bait = entry.TryGetProperty("InitialBait", out var b) ? b.GetUInt32() : 0;
            var spear = entry.TryGetProperty("IsSpearFish", out var s) && s.ValueKind == JsonValueKind.True;
            var mooches = new List<uint>();
            if (entry.TryGetProperty("Mooches", out var m) && m.ValueKind == JsonValueKind.Array)
                foreach (var mooch in m.EnumerateArray())
                    if (mooch.ValueKind == JsonValueKind.Number)
                        mooches.Add(mooch.GetUInt32());
            fish[id] = new BaitPlan(id, bait, mooches, spear);
        }
        return fish;
    }

    private void EnsureLoaded()
    {
        if (_fish is not null) return;
        _fish = new Dictionary<uint, BaitPlan>();

        var folder = _autoHookFolder();
        if (string.IsNullOrEmpty(folder))
        {
            _log?.Invoke("AutoHook is not installed, so there is no bait data — fishing will say what it needs rather than guess.");
            return;
        }

        var file = Path.Combine(folder, RelativePath);
        if (!File.Exists(file))
        {
            _log?.Invoke($"AutoHook is installed but {RelativePath} is not where it was expected ({file}).");
            return;
        }

        try
        {
            using var stream = File.OpenRead(file);
            _fish = Parse(stream, _log);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"AutoHook's fish list would not parse ({ex.GetType().Name}: {ex.Message}).");
        }
    }
}
