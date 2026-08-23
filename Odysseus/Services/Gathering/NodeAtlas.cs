using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace Odysseus.Services.Gathering;

/// <summary>Where one gathering node actually stands.</summary>
/// <param name="NodeId">The <c>GatheringPoint</c> row, which is also the object's data id.</param>
/// <param name="Spawns">Every fixed spot this node appears at.</param>
public sealed record NodeSpawns(uint NodeId, IReadOnlyList<Vector3> Spawns);

/// <summary>
/// Every gathering node's spawn points, read from the copy of GatherBuddy's
/// <c>world_locations.json</c> shipped beside the plugin.
///
/// <para>
/// The game's own <c>ExportedGatheringPoint</c> gives one coordinate per node <i>base</i> with a 60
/// to 100 yalm radius — the centre of the area a node spawns within, not a place to stand. Nodes
/// spawn at fixed spots and move to another fixed spot as they are worked, so what is needed is the
/// list of spots, and the sheets do not have it. GatherBuddy's file does: 4,064 nodes, 6,191 spawn
/// points, full 3D. Checked against an independent measurement of 1,297 of them (QuestFlow's) and
/// agreeing to a median of 0.0 yalms.
/// </para>
///
/// <para>
/// Shipped verbatim under Apache 2.0 rather than converted, so what we carry is their file and not
/// a transcription of it. See <c>NOTICE.md</c>.
/// </para>
/// </summary>
public sealed class NodeAtlas
{
    /// <summary>The file, as it sits beside the DLL.</summary>
    public const string FileName = "gatherbuddy-world_locations.json.gz";

    private readonly string? _file;
    private readonly Action<string>? _log;
    private Dictionary<uint, IReadOnlyList<Vector3>>? _nodes;

    public NodeAtlas(string? file, Action<string>? log = null)
    {
        _file = file;
        _log = log;
    }

    /// <summary>Where the shipped copy lives, given the folder the plugin is running from.</summary>
    public static string? PathBeside(string? assemblyDirectory)
        => string.IsNullOrEmpty(assemblyDirectory)
            ? null
            : Path.Combine(assemblyDirectory, PathPackFolder, FileName);

    private const string PathPackFolder = "Assets";

    public int Count
    {
        get { EnsureLoaded(); return _nodes!.Count; }
    }

    /// <summary>Every spot this node spawns at, or empty when it is not in the atlas.</summary>
    public IReadOnlyList<Vector3> SpawnsOf(uint nodeId)
    {
        EnsureLoaded();
        return _nodes!.GetValueOrDefault(nodeId, Array.Empty<Vector3>());
    }

    /// <summary>
    /// The nodes among these that the atlas knows, richest first — a node with more spawn points
    /// keeps a run going longer before it has to walk somewhere else.
    /// </summary>
    public IReadOnlyList<NodeSpawns> Best(IEnumerable<uint> nodeIds)
    {
        EnsureLoaded();
        return nodeIds.Distinct()
            .Select(id => new NodeSpawns(id, SpawnsOf(id)))
            .Where(n => n.Spawns.Count > 0)
            .OrderByDescending(n => n.Spawns.Count)
            .ToList();
    }

    private void EnsureLoaded()
    {
        if (_nodes is not null) return;
        _nodes = new Dictionary<uint, IReadOnlyList<Vector3>>();

        if (_file is null || !File.Exists(_file))
        {
            _log?.Invoke($"Node atlas missing at {_file ?? "(no path)"} — gathering has no coordinates to work from.");
            return;
        }

        try
        {
            using var raw = File.OpenRead(_file);
            using var gzip = new GZipStream(raw, CompressionMode.Decompress);
            using var doc = JsonDocument.Parse(gzip);
            foreach (var node in doc.RootElement.EnumerateObject())
            {
                if (!uint.TryParse(node.Name, out var id))
                    continue;
                var spawns = new List<Vector3>();
                foreach (var p in node.Value.EnumerateArray())
                    spawns.Add(new Vector3(
                        p.GetProperty("X").GetSingle(),
                        p.GetProperty("Y").GetSingle(),
                        p.GetProperty("Z").GetSingle()));
                if (spawns.Count > 0)
                    _nodes[id] = spawns;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Node atlas unreadable ({ex.GetType().Name}: {ex.Message}) — gathering has no coordinates.");
        }
    }
}
