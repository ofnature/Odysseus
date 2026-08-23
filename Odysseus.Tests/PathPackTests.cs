using System.IO.Compression;
using Odysseus.Services.Paths;

namespace Odysseus.Tests;

public class PathPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static QuestPath Path1(ushort id, string name) => new()
    {
        QuestId = id,
        Name = name,
        Sequences = [new QuestSequence { Sequence = 1, Steps = [new QuestStep { Kind = StepKind.Interact, DataId = 7 }] }],
    };

    [Fact]
    public void A_packed_library_comes_back_whole()
    {
        var file = System.IO.Path.Combine(_dir, PathPack.FileName);
        PathPack.WriteFile(file, [Path1(1, "One"), Path1(2600, "Gemworks in Progress")]);

        using var stream = File.OpenRead(file);
        var read = PathPack.Read(stream).ToList();
        Assert.Equal([(ushort)1, (ushort)2600], read.Select(p => p.QuestId));
        Assert.Equal("Gemworks in Progress", read[1].Name);
        Assert.Equal(StepKind.Interact, read[1].Sequences[0].Steps[0].Kind);
        Assert.Equal(7u, read[1].Sequences[0].Steps[0].DataId);
    }

    [Fact]
    public void The_shipped_library_is_read_first_and_this_clients_own_paths_win()
    {
        var pack = System.IO.Path.Combine(_dir, PathPack.FileName);
        PathPack.WriteFile(pack, [Path1(1, "Shipped one"), Path1(2, "Shipped two")]);
        var folder = System.IO.Path.Combine(_dir, "paths");

        var store = new PathStore(folder, packFile: pack);
        Assert.Equal(2, store.Count);
        Assert.Equal(2, store.FromPack);
        Assert.Equal("Shipped one", store.ForQuest(1)!.Name);

        // What this client imported or recorded is an overlay, not an also-ran.
        new PathStore(folder).Save(Path1(1, "Mine"));
        new PathStore(folder).Save(Path1(3, "Also mine"));
        store.Reload();

        Assert.Equal(3, store.Count);
        Assert.Equal(2, store.FromPack);
        Assert.Equal(2, store.FromFolder); // one of them replacing a shipped path, one new
        Assert.Equal("Mine", store.ForQuest(1)!.Name);
        Assert.Equal("Shipped two", store.ForQuest(2)!.Name);
        Assert.Equal("Also mine", store.ForQuest(3)!.Name);
    }

    [Fact]
    public void A_clients_own_path_from_an_older_converter_yields_to_a_current_shipped_one()
    {
        // The 3 → 4 case: every stored path predates Land, the shipped library carries it.
        // Running the stale parse because it is "mine" is the wrong kind of loyalty; a
        // re-import would replace it with exactly the shipped conversion anyway.
        var pack = System.IO.Path.Combine(_dir, PathPack.FileName);
        PathPack.WriteFile(pack, [Path1(1, "Shipped, current")]);
        var folder = System.IO.Path.Combine(_dir, "paths");
        var stale = Path1(1, "Mine, older");
        stale.FormatVersion = QuestPath.CurrentFormatVersion - 1;
        new PathStore(folder).Save(stale);
        var logged = new List<string>();

        var store = new PathStore(folder, logged.Add, pack);
        Assert.Equal("Shipped, current", store.ForQuest(1)!.Name);
        Assert.Equal(0, store.FromFolder);
        Assert.Contains(logged, m => m.Contains("converted by an older build"));

        // A current one of mine still wins, as before.
        new PathStore(folder).Save(Path1(1, "Mine, current"));
        store.Reload();
        Assert.Equal("Mine, current", store.ForQuest(1)!.Name);
    }

    [Fact]
    public void A_missing_or_unreadable_pack_leaves_the_folder_working()
    {
        var folder = System.IO.Path.Combine(_dir, "paths");
        new PathStore(folder).Save(Path1(9, "Mine"));

        var absent = new PathStore(folder, packFile: System.IO.Path.Combine(_dir, "not-there.pak"));
        Assert.Equal(1, absent.Count);
        Assert.Equal(0, absent.FromPack);

        var corrupt = System.IO.Path.Combine(_dir, "corrupt.pak");
        File.WriteAllText(corrupt, "this is not a gzip stream");
        var logged = new List<string>();
        var store = new PathStore(folder, logged.Add, corrupt);
        Assert.Equal(1, store.Count);
        Assert.Contains(logged, m => m.Contains("Shipped path library unreadable"));
    }

    [Fact]
    public void The_pack_target_is_the_source_tree_or_nothing()
    {
        Assert.Equal(System.IO.Path.Combine(@"D:\Dev\Odysseus\Odysseus", "Assets", PathPack.FileName),
            PathPack.SourceAssetPath(@"D:\Dev\Odysseus\Odysseus\bin\Debug"));
        Assert.Equal(System.IO.Path.Combine(@"D:\Dev\Odysseus\Odysseus", "Assets", PathPack.FileName),
            PathPack.SourceAssetPath(@"D:\Dev\Odysseus\Odysseus\bin\x64\Release"));
        Assert.Null(PathPack.SourceAssetPath(@"C:\Users\me\AppData\Roaming\XIVLauncher\installedPlugins\Odysseus\0.1.0"));
        Assert.Null(PathPack.SourceAssetPath(null));
    }

    [Fact]
    public void The_shipped_pack_is_found_in_assets_or_beside_the_dll()
    {
        var dll = System.IO.Path.Combine(_dir, "plugin");
        Directory.CreateDirectory(System.IO.Path.Combine(dll, PathPack.AssetFolder));

        // Nothing there yet: still names where the build puts it, so the log says the right file.
        Assert.Equal(System.IO.Path.Combine(dll, PathPack.AssetFolder, PathPack.FileName), PathPack.ShippedPath(dll));

        var beside = System.IO.Path.Combine(dll, PathPack.FileName);
        PathPack.WriteFile(beside, [Path1(1, "One")]);
        Assert.Equal(beside, PathPack.ShippedPath(dll));

        var inAssets = System.IO.Path.Combine(dll, PathPack.AssetFolder, PathPack.FileName);
        PathPack.WriteFile(inAssets, [Path1(1, "One")]);
        Assert.Equal(inAssets, PathPack.ShippedPath(dll)); // the build's copy wins
    }
}
