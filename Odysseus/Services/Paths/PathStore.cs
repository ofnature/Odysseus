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

    /// <summary>The same shape without the indenting — the pack is read by machines only.</summary>
    internal static readonly JsonSerializerOptions PackJsonOptions = new(JsonOptions) { WriteIndented = false };

    private readonly string _directory;
    private readonly string? _packFile;
    private readonly Action<string>? _log;
    private readonly Dictionary<ushort, QuestPath> _paths = [];
    private bool _loaded;
    private int _outdated;
    private int _fromPack;
    private int _fromFolder;

    /// <param name="directory">Where this client's own paths live — imported, recorded, edited.</param>
    /// <param name="packFile">
    /// The library shipped with the build, read first so a fresh install has every quest without
    /// importing anything. The folder is laid over it, so anything here can still be replaced.
    /// </param>
    public PathStore(string directory, Action<string>? log = null, string? packFile = null)
    {
        _directory = directory;
        _log = log;
        _packFile = packFile;
    }

    public string Directory => _directory;

    /// <summary>How many paths came with the build.</summary>
    public int FromPack
    {
        get { EnsureLoaded(); return _fromPack; }
    }

    /// <summary>How many came from this client's own folder — some of which replace a shipped one.</summary>
    public int FromFolder
    {
        get { EnsureLoaded(); return _fromFolder; }
    }

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

    /// <summary>
    /// Paths a re-import would materially improve — see <see cref="QuestPath.NeedsReconvert"/>.
    ///
    /// <para>
    /// They load and run, but only as far as the converter that wrote them understood: a verb named
    /// after they were converted is stored as <see cref="StepKind.Unknown"/>, and every field added
    /// since is simply absent. That degrades <i>silently</i> — a Craft step from a version-1 path is
    /// indistinguishable from a step with no item, so it offers no material list and stops the run
    /// blaming a feature that exists. Hence a count worth surfacing.
    /// </para>
    /// </summary>
    public int OutdatedCount
    {
        get { EnsureLoaded(); return _outdated; }
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
        if (_paths.TryGetValue(path.QuestId, out var replaced) && replaced.NeedsReconvert)
            _outdated--;
        if (path.NeedsReconvert)
            _outdated++;
        _paths[path.QuestId] = path;
    }

    private string FileFor(ushort questId) => Path.Combine(_directory, $"{questId}.json");

    /// <summary>
    /// Read the folder again next time it is asked for.
    ///
    /// <para>
    /// The store is read once per plugin load and then held. That is fine for one client, and
    /// wrong for two: importing on one leaves the other still holding whatever the folder had when
    /// it first looked — usually nothing — so every quest reads as "no path" on a character that
    /// has a full folder sitting on disk.
    /// </para>
    /// </summary>
    public void Reload()
    {
        _paths.Clear();
        _outdated = 0;
        _fromPack = 0;
        _fromFolder = 0;
        _loaded = false;
    }

    /// <summary>Write everything currently loaded out as a shipping pack.</summary>
    public void Pack(string file)
    {
        EnsureLoaded();
        PathPack.WriteFile(file, _paths.Values.OrderBy(p => p.QuestId));
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        // The shipped library first: the folder is an overlay on it, so a path this client imported
        // or recorded for a quest wins over the one that came with the build.
        if (_packFile is not null && File.Exists(_packFile))
        {
            try
            {
                using var stream = File.OpenRead(_packFile);
                foreach (var path in PathPack.Read(stream, _log))
                {
                    if (path.NeedsReconvert) _outdated++;
                    _paths[path.QuestId] = path;
                }
                _fromPack = _paths.Count;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Shipped path library unreadable ({ex.GetType().Name}: {ex.Message}) — falling back to imported paths.");
            }
        }

        if (!System.IO.Directory.Exists(_directory)) return;

        var failed = 0;
        var superseded = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var path = JsonSerializer.Deserialize<QuestPath>(File.ReadAllText(file), JsonOptions);
                if (path is null || path.QuestId == 0) { failed++; continue; }
                // A folder entry converted by an older build does not beat the shipped one for
                // the same quest when the shipped one is current: a re-import would replace it
                // with exactly that conversion, and until then it would run the stale parse.
                if (path.FormatVersion < QuestPath.CurrentFormatVersion
                    && _paths.TryGetValue(path.QuestId, out var shipped) && shipped.FormatVersion >= QuestPath.CurrentFormatVersion)
                {
                    superseded++;
                    continue;
                }
                if (path.NeedsReconvert) _outdated++;
                _paths[path.QuestId] = path;
                _fromFolder++;
            }
            catch (Exception ex)
            {
                failed++;
                if (failed <= 5) _log?.Invoke($"Path {Path.GetFileName(file)} unreadable: {ex.Message}");
            }
        }
        if (failed > 0) _log?.Invoke($"{failed} stored path(s) unreadable — re-import to repair.");
        if (superseded > 0) _log?.Invoke($"{superseded} stored path(s) were converted by an older build and the shipped library is current — using the shipped copies; re-import to refresh your own.");
    }
}
