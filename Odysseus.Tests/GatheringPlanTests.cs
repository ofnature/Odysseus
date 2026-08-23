using System.Numerics;
using Odysseus.Services.Deliveries;
using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

public class GatheringPlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Source : IGatheringSource
    {
        public List<GatheringPointRef> Points { get; } = [];
        public GatheringOrigin? For(uint itemId) => null;
        public IReadOnlyList<uint> BasesFor(uint itemId) => [];
        public IReadOnlyList<GatheringPointRef> PointsFor(uint itemId) => Points;
    }

    /// <summary>An atlas file in GatherBuddy's own shape, so the reader is exercised as it will be.</summary>
    private NodeAtlas Atlas(string json)
    {
        var file = Path.Combine(_dir, NodeAtlas.FileName);
        Directory.CreateDirectory(_dir);
        using (var raw = File.Create(file))
        using (var gzip = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionLevel.Optimal))
        using (var writer = new StreamWriter(gzip))
            writer.Write(json);
        return new NodeAtlas(file);
    }

    [Fact]
    public void The_gathering_windows_slot_layout_is_eleven_values_a_row()
    {
        // Read off a live window (2026-08-22): row n's item id sits at 6 + 11n. Two rows check
        // themselves — the empty row held 0 and the fifth held 12, the Lightning Crystal.
        Assert.Equal(6, SlotValueIndex(0));
        Assert.Equal(17, SlotValueIndex(1));
        Assert.Equal(39, SlotValueIndex(3));   // the row the window showed as NOTHING
        Assert.Equal(50, SlotValueIndex(4));   // Lightning Crystal
        Assert.Equal(72, SlotValueIndex(6));   // Glass Eye, the row we were after
        Assert.Equal(83, SlotValueIndex(7));

        static int SlotValueIndex(int slot) => 6 + 11 * slot;
    }

    [Fact]
    public void The_atlas_reads_gatherbuddys_shape()
    {
        var atlas = Atlas("""
        {
          "34838": [ {"X": 18.8, "Y": -161.2, "Z": 198.7}, {"X": 13.9, "Y": -157.3, "Z": 173.8} ],
          "34837": [ {"X": 10.1, "Y": -158.4, "Z": 184.5} ]
        }
        """);
        Assert.Equal(2, atlas.Count);
        Assert.Equal(2, atlas.SpawnsOf(34838).Count);
        Assert.Equal(new Vector3(10.1f, -158.4f, 184.5f), atlas.SpawnsOf(34837)[0]);
        Assert.Empty(atlas.SpawnsOf(999));
    }

    [Fact]
    public void A_missing_atlas_says_so_rather_than_throwing()
    {
        var logged = new List<string>();
        var atlas = new NodeAtlas(Path.Combine(_dir, "not-there.gz"), logged.Add);
        Assert.Equal(0, atlas.Count);
        Assert.Empty(atlas.SpawnsOf(1));
        Assert.Contains(logged, m => m.Contains("Node atlas missing"));
    }

    [Fact]
    public void The_node_with_the_most_spawn_points_is_chosen()
    {
        var atlas = Atlas("""
        { "100": [ {"X":1,"Y":0,"Z":1} ],
          "200": [ {"X":2,"Y":0,"Z":2}, {"X":3,"Y":0,"Z":3}, {"X":4,"Y":0,"Z":4} ] }
        """);
        var source = new Source();
        source.Points.Add(new GatheringPointRef(100, 1187, 0, 90));
        source.Points.Add(new GatheringPointRef(200, 1187, 0, 90));

        var target = GatheringPlan.For(44850, source, atlas);
        Assert.NotNull(target);
        Assert.Equal(200u, target!.NodeId);
        Assert.Equal(3, target.Spawns.Count);
        Assert.Equal(16u, target.ClassJobId); // gathering type 0 is a Miner's
        Assert.Equal(1187u, target.TerritoryId);

        // The other is kept as a fallback, not thrown away.
        Assert.Equal([200u, 100u], GatheringPlan.All(44850, source, atlas).Select(t => t.NodeId));
    }

    [Fact]
    public void A_point_the_sheets_cannot_place_is_no_use_however_many_spawns_it_has()
    {
        var atlas = Atlas("""
        { "100": [ {"X":1,"Y":0,"Z":1}, {"X":2,"Y":0,"Z":2}, {"X":3,"Y":0,"Z":3} ],
          "200": [ {"X":4,"Y":0,"Z":4} ] }
        """);
        var source = new Source();
        source.Points.Add(new GatheringPointRef(100, 1, 0, 90));   // placeholder territory
        source.Points.Add(new GatheringPointRef(200, 1187, 2, 90));

        var target = GatheringPlan.For(1, source, atlas);
        Assert.Equal(200u, target!.NodeId);
        Assert.Equal(17u, target.ClassJobId); // gathering type 2 is a Botanist's
    }

    [Fact]
    public void A_node_with_no_coordinates_is_left_out()
    {
        var atlas = Atlas("""{ "200": [ {"X":4,"Y":0,"Z":4} ] }""");
        var source = new Source();
        source.Points.Add(new GatheringPointRef(100, 1187, 0, 90)); // not in the atlas
        Assert.Null(GatheringPlan.For(1, source, atlas));

        source.Points.Add(new GatheringPointRef(200, 1187, 0, 90));
        Assert.Equal(200u, GatheringPlan.For(1, source, atlas)!.NodeId);
    }
}
