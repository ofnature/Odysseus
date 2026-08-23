using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Odysseus.Services.Paths;

/// <summary>
/// The whole path library as one file, for shipping with the plugin.
///
/// <para>
/// Importing is a developer's job, not everyone's: the bundle it reads from belongs to another
/// plugin, and on four accounts in four folders that is four imports of the same 4,240 quests to
/// keep in step. So the converted library travels with the build and the folder is only what a
/// particular client has changed on top.
/// </para>
///
/// <para>
/// One gzip stream of minified JSON lines, measured against the alternatives on the real library
/// (2026-08-20, 4,240 quests, 17.2 MB of loose files): <b>0.92 MB</b> here against 3.51 MB as a zip
/// of the same files. A zip compresses each entry alone; quest paths are near-identical in shape,
/// so a single stream gets to reuse what it learned from the last 4,000 of them. It is also one
/// sequential read instead of 4,240 opens.
/// </para>
/// </summary>
public static class PathPack
{
    /// <summary>The file the plugin ships and looks for beside itself.</summary>
    public const string FileName = "paths.pak";

    /// <summary>The folder it ships in, kept as a folder so the plugin zip stays tidy.</summary>
    public const string AssetFolder = "Assets";

    /// <summary>
    /// Where to read the shipped library from at runtime. The build copies it into an
    /// <c>Assets</c> folder beside the DLL; the bare-beside-the-DLL spelling is accepted too, so a
    /// pack dropped in by hand is still found.
    /// </summary>
    public static string? ShippedPath(string? assemblyDirectory)
    {
        if (string.IsNullOrEmpty(assemblyDirectory))
            return null;
        var inAssets = Path.Combine(assemblyDirectory, AssetFolder, FileName);
        if (File.Exists(inAssets))
            return inAssets;
        var beside = Path.Combine(assemblyDirectory, FileName);
        return File.Exists(beside) ? beside : inAssets;
    }

    public static void Write(Stream destination, IEnumerable<QuestPath> paths)
    {
        using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        foreach (var path in paths)
            writer.WriteLine(JsonSerializer.Serialize(path, PathStore.PackJsonOptions));
    }

    /// <summary>
    /// Read every path in the pack. A line that will not parse is skipped rather than taking the
    /// rest of the library with it — a pack half-read still runs most quests.
    /// </summary>
    public static IEnumerable<QuestPath> Read(Stream source, Action<string>? log = null)
    {
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var failed = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            QuestPath? path = null;
            try
            {
                path = JsonSerializer.Deserialize<QuestPath>(line, PathStore.PackJsonOptions);
            }
            catch (Exception ex)
            {
                if (++failed <= 3)
                    log?.Invoke($"A line of {FileName} would not parse: {ex.Message}");
            }
            if (path is { QuestId: not 0 })
                yield return path;
        }
    }

    /// <summary>
    /// Where the pack belongs in the source tree, worked out from where the plugin is running.
    ///
    /// <para>
    /// A dev build runs straight out of <c>&lt;project&gt;/bin/&lt;Config&gt;/</c>, so the project
    /// folder — and the <c>Assets</c> beside it that the build ships — is two or three levels up. A
    /// release build runs from Dalamud's installed-plugin folder, where there is no source tree to
    /// write to, and this returns null. That is the whole gate on packing: it is offered where it
    /// makes sense and absent everywhere else.
    /// </para>
    /// </summary>
    public static string? SourceAssetPath(string? assemblyDirectory)
    {
        if (string.IsNullOrEmpty(assemblyDirectory))
            return null;
        // Walked as a string rather than through DirectoryInfo: the paths this sees are the
        // game host's, which are Windows-shaped even under Wine, and the test runner's
        // filesystem is not always. Both separators count.
        var dir = assemblyDirectory.TrimEnd('\\', '/');
        for (var i = 0; i < 3; i++)
        {
            var cut = dir.LastIndexOfAny(['\\', '/']);
            if (cut <= 0)
                return null;
            var name = dir[(cut + 1)..];
            dir = dir[..cut];
            if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(dir, AssetFolder, FileName);
        }
        return null;
    }

    public static void WriteFile(string file, IEnumerable<QuestPath> paths)
    {
        var folder = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        using var stream = File.Create(file);
        Write(stream, paths);
    }
}
