using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Gathering;

/// <summary>What one import run did.</summary>
public sealed class GatheringImportReport
{
    public int Converted { get; set; }
    public int Failed { get; set; }
    /// <summary>Files that are not paths — the schema documents that sit in the same tree.</summary>
    public int Skipped { get; set; }
    public int Nodes { get; set; }
    public int Locations { get; set; }
    public List<string> Errors { get; } = [];

    public string Describe() =>
        $"{Converted} gathering paths ({Nodes} nodes, {Locations} locations)" +
        (Skipped > 0 ? $", {Skipped} skipped" : "") +
        (Failed > 0 ? $", {Failed} failed" : "");
}

/// <summary>
/// Reads QuestFlow's <c>GatheringPaths</c> tree into our own shape.
///
/// <para>
/// Converted once and stored, for the same reason the quest bundle is: it is another project's
/// working data and can change shape without warning, so a node that worked yesterday must not
/// quietly become an empty path today.
/// </para>
///
/// <para>
/// The file name carries what the body does not — <c>{GatheringPointBase}_{Place}_{JOB}.json</c> —
/// and the folders carry the expansion and zone. The travel steps in the body are the same shape as
/// a quest step's, so they go through <see cref="QuestionableImporter.ParseStep"/> unchanged.
/// </para>
/// </summary>
public static class GatheringPathImporter
{
    /// <summary>Read every path under a QuestFlow checkout's <c>GatheringPaths</c> folder.</summary>
    public static (List<GatheringPath> Paths, GatheringImportReport Report) Import(string folder)
    {
        var report = new GatheringImportReport();
        var paths = new List<GatheringPath>();
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"no GatheringPaths folder at {folder}");

        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            var category = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : string.Empty;
            try
            {
                var path = Parse(Path.GetFileName(file), category, File.ReadAllText(file));
                if (path is null)
                {
                    report.Skipped++;
                    continue;
                }
                foreach (var group in path.Groups)
                    foreach (var node in group.Nodes)
                    {
                        report.Nodes++;
                        report.Locations += node.Locations.Count;
                    }
                paths.Add(path);
                report.Converted++;
            }
            catch (Exception ex)
            {
                report.Failed++;
                if (report.Errors.Count < 50)
                    report.Errors.Add($"{relative}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return (paths, report);
    }

    /// <summary>
    /// Convert one file. Public and pure so it can be tested against sample JSON without a checkout.
    /// Returns null for a file that is not a path — the schema documents in the same tree have no
    /// numeric prefix and no <c>Groups</c>.
    /// </summary>
    public static GatheringPath? Parse(string fileName, string category, string json)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split('_');
        if (parts.Length < 2 || !uint.TryParse(parts[0], out var pointBase))
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return null;

        var path = new GatheringPath
        {
            PointBaseId = pointBase,
            Name = parts.Length > 2 ? string.Join('_', parts[1..^1]) : parts[1],
            Job = parts.Length > 2 ? parts[^1] : string.Empty,
            Category = category,
            Author = root.TryGetProperty("Author", out var a) ? a.GetString() ?? string.Empty : string.Empty,
            SourceHash = QuestionableImporter.Hash(json),
            FlyBetweenNodes = root.TryGetProperty("FlyBetweenNodes", out var fb)
                              && fb.ValueKind == JsonValueKind.True,
        };

        if (root.TryGetProperty("Steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            foreach (var step in steps.EnumerateArray())
                path.Steps.Add(QuestionableImporter.ParseStep(step));

        foreach (var group in groups.EnumerateArray())
        {
            var converted = new GatheringGroup();
            if (group.TryGetProperty("Nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var node in nodes.EnumerateArray())
                    converted.Nodes.Add(ParseNode(node));
            path.Groups.Add(converted);
        }

        return path;
    }

    private static GatheringNode ParseNode(JsonElement e)
    {
        var node = new GatheringNode
        {
            DataId = e.TryGetProperty("DataId", out var id) ? id.GetUInt32() : 0,
            Fly = e.TryGetProperty("Fly", out var fly) && fly.ValueKind == JsonValueKind.True,
        };
        if (e.TryGetProperty("Locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
            foreach (var location in locations.EnumerateArray())
                node.Locations.Add(ParseLocation(location));
        return node;
    }

    private static GatheringLocation ParseLocation(JsonElement e) => new()
    {
        Position = e.TryGetProperty("Position", out var p) ? ParseVector(p) : default,
        MinimumAngle = F32(e, "MinimumAngle"),
        MaximumAngle = F32(e, "MaximumAngle"),
        MinimumDistance = F32(e, "MinimumDistance"),
        MaximumDistance = F32(e, "MaximumDistance"),
    };

    private static Vector3 ParseVector(JsonElement e) => new(
        e.TryGetProperty("X", out var x) ? x.GetSingle() : 0f,
        e.TryGetProperty("Y", out var y) ? y.GetSingle() : 0f,
        e.TryGetProperty("Z", out var z) ? z.GetSingle() : 0f);

    private static float? F32(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
}
