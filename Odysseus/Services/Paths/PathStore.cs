using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odysseus.Services.Paths;

/// <summary>
/// Persisted quest-path library, one file per quest under the plugin's config directory.
///
/// <para>
/// <b>Converted once, not re-read every launch.</b> The bundle is another plugin's internal data
/// and can change shape without warning; converting on every startup would mean a quest that ran
/// yesterday silently becomes an empty path today. So import is an explicit action, its output is
/// ours, and a stored path keeps working regardless of what happens to the file it came from.
/// </para>
/// </summary>
public sealed class PathStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Vector3 exposes X/Y/Z as fields; without this a position round-trips as {}.
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly Action<string>? _log;
    private readonly Dictionary<ushort, QuestPath> _paths = [];
    private bool _loaded;

    public PathStore(string directory, Action<string>? log = null)
    {
        _directory = directory;
        _log = log;
    }

    public string Directory => _directory;

    public int Count
    {
        get { EnsureLoaded(); return _paths.Count; }
    }

    public IReadOnlyCollection<QuestPath> All
    {
        get { EnsureLoaded(); return _paths.Values; }
    }

    public QuestPath? ForQuest(ushort questId)
    {
        EnsureLoaded();
        return _paths.TryGetValue(questId, out var p) ? p : null;
    }

    public bool Has(ushort questId)
    {
        EnsureLoaded();
        return _paths.ContainsKey(questId);
    }

    /// <summary>Import from a bundle and persist. Returns the report; errors are in it, not thrown, except for a bad bundle.</summary>
    public ImportReport ImportBundle(string bundlePath, Func<string, bool>? filter = null)
    {
        EnsureLoaded();
        var (paths, report) = QuestionableImporter.Import(bundlePath, filter);
        var written = 0;
        var reconverted = 0;
        foreach (var path in paths)
        {
            // Same source AND same converter — nothing to do. A converter bump re-parses
            // everything, which is how new step kinds and fields reach paths that did not change
            // upstream (see QuestPath.CurrentFormatVersion).
            if (_paths.TryGetValue(path.QuestId, out var existing))
            {
                if (existing.SourceHash == path.SourceHash && existing.FormatVersion == QuestPath.CurrentFormatVersion)
                    continue;
                if (existing.SourceHash == path.SourceHash)
                    reconverted++;
            }
            Save(path);
            written++;
        }
        report.Reconverted = reconverted;
        _log?.Invoke($"Import: {report}; {written} written to {_directory}");
        return report;
    }

    /// <summary>Write one path (also how the editor persists a fix).</summary>
    public void Save(QuestPath path)
    {
        EnsureLoaded();
        System.IO.Directory.CreateDirectory(_directory);
        var file = FileFor(path.QuestId);
        File.WriteAllText(file, JsonSerializer.Serialize(path, JsonOptions));
        _paths[path.QuestId] = path;
    }

    private string FileFor(ushort questId) => Path.Combine(_directory, $"{questId}.json");

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
                var path = JsonSerializer.Deserialize<QuestPath>(File.ReadAllText(file), JsonOptions);
                if (path is null || path.QuestId == 0) { failed++; continue; }
                _paths[path.QuestId] = path;
            }
            catch (Exception ex)
            {
                failed++;
                if (failed <= 5) _log?.Invoke($"Path {Path.GetFileName(file)} unreadable: {ex.Message}");
            }
        }
        if (failed > 0) _log?.Invoke($"{failed} stored path(s) unreadable — re-import to repair.");
    }
}
