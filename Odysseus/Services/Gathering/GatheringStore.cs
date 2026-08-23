using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Gathering;

/// <summary>
/// The converted gathering paths, one file per <c>GatheringPointBase</c>.
///
/// <para>
/// Same shape and same reasoning as <see cref="PathStore"/>: converted once by an explicit import,
/// held as ours, and re-readable on demand so a second client picks up what the first one wrote.
/// It sits in its own folder rather than sharing the quest one because the two are keyed by
/// different things — a quest id and a gathering point — and 4,240 quest files should not have 175
/// node files mixed in among them.
/// </para>
/// </summary>
public sealed class GatheringStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true, // Vector3 exposes X/Y/Z as fields
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly Action<string>? _log;
    private readonly Dictionary<uint, GatheringPath> _paths = [];
    private bool _loaded;

    public GatheringStore(string directory, Action<string>? log = null)
    {
        _directory = directory;
        _log = log;
    }

    public string Directory => _directory;

    public int Count
    {
        get { EnsureLoaded(); return _paths.Count; }
    }

    public IReadOnlyCollection<GatheringPath> All
    {
        get { EnsureLoaded(); return _paths.Values; }
    }

    /// <summary>The path for one gathering point, or null when none was imported.</summary>
    public GatheringPath? ForPointBase(uint pointBaseId)
    {
        EnsureLoaded();
        return _paths.GetValueOrDefault(pointBaseId);
    }

    /// <summary>
    /// Every stored path that works one of these points, best first — most locations first, since a
    /// node with more spawns is less likely to be exhausted before the bag is full.
    /// </summary>
    public IReadOnlyList<GatheringPath> ForPointBases(IEnumerable<uint> pointBaseIds)
    {
        EnsureLoaded();
        return pointBaseIds
            .Select(ForPointBase)
            .Where(p => p is not null)
            .Select(p => p!)
            .OrderByDescending(p => p.AllLocations().Count())
            .ToList();
    }

    public void Save(GatheringPath path)
    {
        System.IO.Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, $"{path.PointBaseId}.json"), JsonSerializer.Serialize(path, JsonOptions));
        EnsureLoaded();
        _paths[path.PointBaseId] = path;
    }

    public int SaveAll(IEnumerable<GatheringPath> paths)
    {
        var count = 0;
        foreach (var path in paths)
        {
            Save(path);
            count++;
        }
        return count;
    }

    /// <summary>Read the folder again next time it is asked for. See <see cref="PathStore.Reload"/>.</summary>
    public void Reload()
    {
        _paths.Clear();
        _loaded = false;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!System.IO.Directory.Exists(_directory)) return;

        var failed = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var path = JsonSerializer.Deserialize<GatheringPath>(File.ReadAllText(file), JsonOptions);
                if (path is null || path.PointBaseId == 0) { failed++; continue; }
                _paths[path.PointBaseId] = path;
            }
            catch (Exception ex)
            {
                failed++;
                if (failed <= 5) _log?.Invoke($"Gathering path {Path.GetFileName(file)} unreadable: {ex.Message}");
            }
        }
        if (failed > 0) _log?.Invoke($"{failed} stored gathering path(s) unreadable — re-import to repair.");
    }
}
